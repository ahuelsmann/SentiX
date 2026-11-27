// SentiX native fast-path — ONNX-FinBERT INT8 sentiment engine (specs/002 §7).
//
// Only compiled when SENTIX_HAS_ONNX == 1 (CMake option SENTIX_ENABLE_ONNX). ONNX Runtime is loaded at runtime via
// dlopen("libonnxruntime.so.1"), first next to the running executable, then through the regular dynamic-loader search
// path. The daemon therefore has no hard link-time dependency on ONNX Runtime: when the library is absent, ONNX
// initialization fails closed (DEGRADED / MODEL_NOT_READY) while "mock" and "auto"-without-model keep working.
#pragma once

#include <cstddef>
#include <filesystem>
#include <memory>
#include <optional>
#include <string>
#include <string_view>

#include "sentix/finbert_postprocess.hpp"
#include "sentix/sentiment_engine.hpp"
#include "sentix/wordpiece_tokenizer.hpp"

namespace sentix::sentiment {

inline constexpr std::string_view kOnnxRuntimeSoname = "libonnxruntime.so.1";

struct OnnxFinbertOptions {
    std::filesystem::path modelPath;
    std::filesystem::path vocabPath;
    finbert::LabelOrder labels{finbert::kDefaultLabelOrder};
    int intraOpThreads{1};
    std::size_t maxSequenceLength{tokenizer::kMaxSequenceLength};
    std::filesystem::path runtimeLibrary;  // empty -> default search (executable dir, then dynamic loader)
};

/// Loads ONNX Runtime once per process and initializes the C++ API. Returns an error message on failure.
/// Thread-safe; once successful, later calls return std::nullopt immediately.
[[nodiscard]] std::optional<std::string> ensure_onnxruntime_loaded(const std::filesystem::path& runtimeLibrary = {});

class OnnxFinbertSentimentEngine final : public ISentimentEngine {
public:
    /// Loads the vocab + ONNX graph, validates the graph I/O contract (int64 input_ids / attention_mask
    /// [/ token_type_ids] of rank 2, float [1, 3] logits) and runs a warm-up inference.
    /// Throws (std::exception-derived) when anything is missing, corrupt or incompatible.
    explicit OnnxFinbertSentimentEngine(const OnnxFinbertOptions& options);
    ~OnnxFinbertSentimentEngine() override;

    /// Tokenize -> ONNX Runtime inference -> stable softmax -> label mapping. Throws on inference failure.
    [[nodiscard]] SentimentScores score(std::string_view text) override;
    [[nodiscard]] bool is_ready() const noexcept override { return impl_ != nullptr; }
    [[nodiscard]] std::string_view name() const noexcept override { return "onnx-finbert-int8"; }

    [[nodiscard]] bool uses_token_type_ids() const noexcept;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

}  // namespace sentix::sentiment
