// ONNX-FinBERT INT8 engine (specs/002 §7). Compiled only with SENTIX_ENABLE_ONNX=ON.
//
// ORT_API_MANUAL_INIT: the ONNX Runtime C++ wrapper must not reference OrtGetApiBase() at static-init time. The
// runtime library is dlopen()ed on demand and the API table is installed via Ort::InitApi(), so sentix-native starts
// (and serves mock / fail-closed DEGRADED) even on hosts without libonnxruntime.so.
#ifndef ORT_API_MANUAL_INIT
#define ORT_API_MANUAL_INIT
#endif
#include <onnxruntime_cxx_api.h>

#include "sentix/onnx_finbert_engine.hpp"

#include <dlfcn.h>

#include <array>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <stdexcept>
#include <system_error>
#include <vector>

namespace sentix::sentiment {
namespace {

namespace fs = std::filesystem;

std::mutex& runtime_mutex() {
    static std::mutex mutex;
    return mutex;
}

bool g_runtimeReady = false;  // guarded by runtime_mutex()

std::vector<std::string> runtime_candidates(const fs::path& runtimeLibrary) {
    if (!runtimeLibrary.empty()) {
        return {runtimeLibrary.string()};
    }
    std::vector<std::string> candidates;
    std::error_code ec;
    const fs::path executable = fs::read_symlink("/proc/self/exe", ec);
    if (!ec && !executable.empty()) {
        candidates.push_back((executable.parent_path() / std::string(kOnnxRuntimeSoname)).string());
        candidates.push_back((executable.parent_path() / "libonnxruntime.so").string());
    }
    candidates.emplace_back(kOnnxRuntimeSoname);
    candidates.emplace_back("libonnxruntime.so");
    return candidates;
}

enum class InputKind { InputIds, AttentionMask, TokenTypeIds };

/// One ONNX Runtime environment per process (must outlive every session). Intentionally leaked so no static
/// destruction order issue can tear it down while a session is alive. Call only after ensure_onnxruntime_loaded().
Ort::Env& process_env() {
    static Ort::Env* const env = new Ort::Env(ORT_LOGGING_LEVEL_WARNING, "sentix-native");
    return *env;
}

}  // namespace

std::optional<std::string> ensure_onnxruntime_loaded(const fs::path& runtimeLibrary) {
    const std::lock_guard lock(runtime_mutex());
    if (g_runtimeReady) {
        return std::nullopt;
    }

    std::string failures;
    for (const auto& candidate : runtime_candidates(runtimeLibrary)) {
        ::dlerror();
        void* handle = ::dlopen(candidate.c_str(), RTLD_NOW | RTLD_LOCAL);
        if (handle == nullptr) {
            const char* error = ::dlerror();
            failures += "\n  " + candidate + ": " + (error != nullptr ? error : "dlopen failed");
            continue;
        }
        void* symbol = ::dlsym(handle, "OrtGetApiBase");
        if (symbol == nullptr) {
            failures += "\n  " + candidate + ": missing symbol OrtGetApiBase";
            ::dlclose(handle);
            continue;
        }
        using GetApiBaseFn = const OrtApiBase* (*)();
        GetApiBaseFn getApiBase = nullptr;
        static_assert(sizeof(getApiBase) == sizeof(symbol), "function and object pointers must have the same size");
        std::memcpy(&getApiBase, &symbol, sizeof(getApiBase));

        const OrtApiBase* base = getApiBase();
        const OrtApi* api = base != nullptr ? base->GetApi(ORT_API_VERSION) : nullptr;
        if (api == nullptr) {
            const std::string runtimeVersion =
                (base != nullptr && base->GetVersionString != nullptr) ? std::string(base->GetVersionString()) : std::string("unknown");
            failures += "\n  " + candidate + ": runtime " + runtimeVersion + " does not provide ORT API version " +
                        std::to_string(ORT_API_VERSION);
            ::dlclose(handle);
            continue;
        }
        Ort::InitApi(api);
        g_runtimeReady = true;
        return std::nullopt;  // the handle is intentionally kept open for the process lifetime
    }
    return "cannot load ONNX Runtime (" + std::string(kOnnxRuntimeSoname) + "):" + failures;
}

struct OnnxFinbertSentimentEngine::Impl {
    explicit Impl(const OnnxFinbertOptions& options)
        : wordpiece(tokenizer::WordPieceTokenizer::from_file(options.vocabPath)),
          labelOrder(options.labels),
          maxSequenceLength(options.maxSequenceLength) {
        if (maxSequenceLength < 2 || maxSequenceLength > tokenizer::kMaxSequenceLength) {
            throw std::invalid_argument("max sequence length must be within [2, " + std::to_string(tokenizer::kMaxSequenceLength) + "]");
        }
        if (options.intraOpThreads < 1) {
            throw std::invalid_argument("intra-op thread count must be >= 1");
        }
        std::error_code ec;
        if (!fs::is_regular_file(options.modelPath, ec)) {
            throw std::runtime_error("ONNX model '" + options.modelPath.string() + "' does not exist or is not a regular file");
        }
        if (const auto failure = ensure_onnxruntime_loaded(options.runtimeLibrary)) {
            throw std::runtime_error(*failure);
        }

        Ort::SessionOptions sessionOptions;
        sessionOptions.SetIntraOpNumThreads(options.intraOpThreads);
        sessionOptions.SetInterOpNumThreads(1);
        sessionOptions.SetExecutionMode(ExecutionMode::ORT_SEQUENTIAL);
        sessionOptions.SetGraphOptimizationLevel(GraphOptimizationLevel::ORT_ENABLE_ALL);
        session.emplace(process_env(), options.modelPath.c_str(), sessionOptions);
        memoryInfo.emplace(Ort::MemoryInfo::CreateCpu(OrtArenaAllocator, OrtMemTypeDefault));

        bind_inputs();
        bind_output();
        static_cast<void>(run(""));  // warm-up: proves the graph really yields 3 finite logits
    }

    void bind_inputs() {
        Ort::AllocatorWithDefaultOptions allocator;
        bool hasInputIds = false;
        bool hasAttentionMask = false;
        const std::size_t count = session->GetInputCount();
        for (std::size_t i = 0; i < count; ++i) {
            const Ort::AllocatedStringPtr namePtr = session->GetInputNameAllocated(i, allocator);
            const std::string inputName(namePtr.get());
            const Ort::TypeInfo typeInfo = session->GetInputTypeInfo(i);
            if (typeInfo.GetONNXType() != ONNX_TYPE_TENSOR) {
                throw std::runtime_error("graph input '" + inputName + "' is not a tensor");
            }
            const auto tensorInfo = typeInfo.GetTensorTypeAndShapeInfo();
            if (tensorInfo.GetElementType() != ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64) {
                throw std::runtime_error("graph input '" + inputName + "' must be int64");
            }
            if (tensorInfo.GetDimensionsCount() != 2) {
                throw std::runtime_error("graph input '" + inputName + "' must have rank 2 [batch, sequence]");
            }

            InputKind kind{};
            if (inputName == "input_ids") {
                kind = InputKind::InputIds;
                hasInputIds = true;
            } else if (inputName == "attention_mask") {
                kind = InputKind::AttentionMask;
                hasAttentionMask = true;
            } else if (inputName == "token_type_ids") {
                kind = InputKind::TokenTypeIds;
                usesTokenTypeIds = true;
            } else {
                throw std::runtime_error("unsupported graph input '" + inputName +
                                         "' (expected input_ids, attention_mask[, token_type_ids])");
            }
            inputNameStorage.push_back(inputName);
            inputKinds.push_back(kind);
        }
        if (!hasInputIds || !hasAttentionMask) {
            throw std::runtime_error("graph must declare int64 inputs 'input_ids' and 'attention_mask'");
        }
        inputNames.reserve(inputNameStorage.size());
        for (const auto& storedName : inputNameStorage) {
            inputNames.push_back(storedName.c_str());  // storage is final; pointers stay valid
        }
    }

    void bind_output() {
        Ort::AllocatorWithDefaultOptions allocator;
        const std::size_t count = session->GetOutputCount();
        if (count == 0) {
            throw std::runtime_error("graph declares no outputs");
        }
        std::size_t selected = 0;
        for (std::size_t i = 0; i < count; ++i) {
            const Ort::AllocatedStringPtr namePtr = session->GetOutputNameAllocated(i, allocator);
            if (std::string_view(namePtr.get()) == "logits") {
                selected = i;
                break;
            }
        }
        outputName = session->GetOutputNameAllocated(selected, allocator).get();

        const Ort::TypeInfo typeInfo = session->GetOutputTypeInfo(selected);
        if (typeInfo.GetONNXType() != ONNX_TYPE_TENSOR) {
            throw std::runtime_error("graph output '" + outputName + "' is not a tensor");
        }
        const auto tensorInfo = typeInfo.GetTensorTypeAndShapeInfo();
        if (tensorInfo.GetElementType() != ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT) {
            throw std::runtime_error("graph output '" + outputName + "' must be float32 logits");
        }
        const std::vector<std::int64_t> shape = tensorInfo.GetShape();
        const bool batchOk = shape.size() == 2 && (shape[0] == 1 || shape[0] == -1);
        const bool classesOk = shape.size() == 2 && (shape[1] == 3 || shape[1] == -1);
        if (!batchOk || !classesOk) {
            throw std::runtime_error("graph output '" + outputName + "' must have shape [1, 3]");
        }
    }

    SentimentScores run(std::string_view text) {
        tokenizer::Encoding encoding = wordpiece.encode(text, maxSequenceLength);
        const std::array<std::int64_t, 2> shape{1, static_cast<std::int64_t>(encoding.inputIds.size())};

        std::vector<Ort::Value> inputs;
        inputs.reserve(inputKinds.size());
        for (const InputKind kind : inputKinds) {
            std::vector<std::int64_t>& data = kind == InputKind::InputIds
                                                  ? encoding.inputIds
                                                  : (kind == InputKind::AttentionMask ? encoding.attentionMask : encoding.tokenTypeIds);
            inputs.push_back(Ort::Value::CreateTensor<std::int64_t>(*memoryInfo, data.data(), data.size(), shape.data(), shape.size()));
        }

        const char* const outputNames[] = {outputName.c_str()};
        std::vector<Ort::Value> outputs =
            session->Run(Ort::RunOptions{nullptr}, inputNames.data(), inputs.data(), inputs.size(), outputNames, 1);
        if (outputs.size() != 1 || !outputs.front().IsTensor()) {
            throw std::runtime_error("ONNX inference returned no logits tensor");
        }
        const auto info = outputs.front().GetTensorTypeAndShapeInfo();
        if (info.GetElementType() != ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT) {
            throw std::runtime_error("ONNX logits must be float32");
        }
        const std::size_t elements = info.GetElementCount();
        if (elements != 3) {
            throw std::runtime_error("ONNX logits must contain exactly 3 values, got " + std::to_string(elements));
        }
        const float* values = outputs.front().GetTensorData<float>();
        const std::array<float, 3> logits{values[0], values[1], values[2]};
        return finbert::scores_from_logits(logits, labelOrder);
    }

    tokenizer::WordPieceTokenizer wordpiece;
    finbert::LabelOrder labelOrder;
    std::size_t maxSequenceLength;
    std::optional<Ort::Session> session;
    std::optional<Ort::MemoryInfo> memoryInfo;
    std::vector<std::string> inputNameStorage;
    std::vector<const char*> inputNames;
    std::vector<InputKind> inputKinds;
    std::string outputName;
    bool usesTokenTypeIds{false};
};

OnnxFinbertSentimentEngine::OnnxFinbertSentimentEngine(const OnnxFinbertOptions& options) {
    try {
        impl_ = std::make_unique<Impl>(options);
    } catch (const Ort::Exception& ex) {
        throw std::runtime_error("ONNX Runtime rejected model '" + options.modelPath.string() + "': " + ex.what());
    }
}

OnnxFinbertSentimentEngine::~OnnxFinbertSentimentEngine() = default;

SentimentScores OnnxFinbertSentimentEngine::score(std::string_view text) {
    if (!impl_) {
        throw std::logic_error("ONNX-FinBERT engine is not initialized");
    }
    try {
        return impl_->run(text);
    } catch (const Ort::Exception& ex) {
        throw std::runtime_error(std::string("ONNX-FinBERT inference failed: ") + ex.what());
    }
}

bool OnnxFinbertSentimentEngine::uses_token_type_ids() const noexcept { return impl_ != nullptr && impl_->usesTokenTypeIds; }

}  // namespace sentix::sentiment
