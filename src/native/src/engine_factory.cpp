#include "sentix/engine_factory.hpp"

#include <exception>
#include <system_error>
#include <utility>

#if SENTIX_HAS_ONNX
#include "sentix/onnx_finbert_engine.hpp"
#endif

namespace sentix::engine {
namespace {

EngineSelection unavailable(std::string reason) {
    std::string description = "sentiment engine UNAVAILABLE (fail-closed, DEGRADED): " + reason;
    return EngineSelection{std::make_unique<sentiment::UnavailableSentimentEngine>(std::move(reason)), std::move(description)};
}

EngineSelection mock(std::string why) {
    return EngineSelection{std::make_unique<sentiment::MockSentimentEngine>(), "sentiment engine mock-lexicon-v1 (" + std::move(why) + ")"};
}

EngineSelection onnx(const EngineSettings& settings, const std::filesystem::path& onnxRuntimeLibrary) {
#if SENTIX_HAS_ONNX
    sentiment::OnnxFinbertOptions options;
    options.modelPath = settings.modelPath;
    options.vocabPath = settings.vocabPath;
    options.labels = settings.labels;
    options.intraOpThreads = settings.intraOpThreads;
    options.runtimeLibrary = onnxRuntimeLibrary;
    try {
        auto onnxEngine = std::make_unique<sentiment::OnnxFinbertSentimentEngine>(options);
        std::string description = "sentiment engine onnx-finbert-int8 (model '" + settings.modelPath.string() + "', vocab '" +
                                  settings.vocabPath.string() + "', labels " + finbert::format_label_order(settings.labels) +
                                  ", intraOpThreads " + std::to_string(settings.intraOpThreads) +
                                  (onnxEngine->uses_token_type_ids() ? ", token_type_ids" : "") + ")";
        return EngineSelection{std::move(onnxEngine), std::move(description)};
    } catch (const std::exception& ex) {
        return unavailable("ONNX-FinBERT initialization failed: " + std::string(ex.what()));
    } catch (...) {
        return unavailable("ONNX-FinBERT initialization failed: unknown error");
    }
#else
    static_cast<void>(onnxRuntimeLibrary);
    return unavailable("ONNX-FinBERT requested (model '" + settings.modelPath.string() +
                       "') but sentix-native was built with SENTIX_ENABLE_ONNX=OFF");
#endif
}

}  // namespace

bool onnx_support_compiled() noexcept { return SENTIX_HAS_ONNX != 0; }

bool path_present(const std::filesystem::path& path) noexcept {
    std::error_code ec;
    const auto status = std::filesystem::symlink_status(path, ec);
    if (status.type() == std::filesystem::file_type::not_found) {
        return false;
    }
    return true;  // exists, or could not be probed (fail closed)
}

EngineSelection create_sentiment_engine(const EngineSettings& settings, const std::filesystem::path& onnxRuntimeLibrary) {
    try {
        if (!settings.valid()) {
            std::string reasons;
            for (const auto& error : settings.errors) {
                reasons += (reasons.empty() ? "" : "; ") + error;
            }
            return unavailable("invalid engine configuration: " + reasons);
        }

        switch (settings.mode) {
            case EngineMode::Mock:
                return mock("SENTIX_ENGINE_MODE=mock");
            case EngineMode::Onnx:
                return onnx(settings, onnxRuntimeLibrary);
            case EngineMode::Auto:
            default: {
                const bool modelPresent = path_present(settings.modelPath);
                const bool vocabPresent = path_present(settings.vocabPath);
                if (modelPresent && vocabPresent) {
                    return onnx(settings, onnxRuntimeLibrary);
                }
                if (!modelPresent && !vocabPresent) {
                    return mock("SENTIX_ENGINE_MODE=auto, no model at '" + settings.modelPath.string() + "' and no vocab at '" +
                                settings.vocabPath.string() + "'");
                }
                return unavailable(std::string("SENTIX_ENGINE_MODE=auto found only the ") + (modelPresent ? "model" : "vocab") +
                                   " ('" + (modelPresent ? settings.modelPath : settings.vocabPath).string() + "'), missing the " +
                                   (modelPresent ? "vocab" : "model") + " ('" +
                                   (modelPresent ? settings.vocabPath : settings.modelPath).string() + "')");
            }
        }
    } catch (const std::exception& ex) {
        return unavailable(std::string("engine selection failed: ") + ex.what());
    } catch (...) {
        return unavailable("engine selection failed: unknown error");
    }
}

}  // namespace sentix::engine
