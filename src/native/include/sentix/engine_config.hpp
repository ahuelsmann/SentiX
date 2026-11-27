// SentiX native fast-path — Phase 2 sentiment engine runtime settings (specs/002 §7).
#pragma once

#include <filesystem>
#include <functional>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "sentix/finbert_postprocess.hpp"

namespace sentix::engine {

enum class EngineMode { Auto, Onnx, Mock };

inline constexpr std::string_view kEnvEngineMode = "SENTIX_ENGINE_MODE";
inline constexpr std::string_view kEnvModelPath = "SENTIX_MODEL_PATH";
inline constexpr std::string_view kEnvVocabPath = "SENTIX_VOCAB_PATH";
inline constexpr std::string_view kEnvFinbertLabels = "SENTIX_FINBERT_LABELS";
inline constexpr std::string_view kEnvIntraOpThreads = "SENTIX_ONNX_INTRA_OP_THREADS";

inline constexpr std::string_view kDefaultModelPath = "/models/finbert-int8.onnx";
inline constexpr std::string_view kDefaultVocabPath = "/models/vocab.txt";
inline constexpr int kDefaultIntraOpThreads = 1;
inline constexpr int kMaxIntraOpThreads = 64;

[[nodiscard]] std::string_view to_string(EngineMode mode) noexcept;

/// "auto" | "onnx" | "mock" (case-insensitive, trimmed). Throws std::invalid_argument for anything else.
[[nodiscard]] EngineMode parse_engine_mode(std::string_view value);

/// Decimal integer in [1, kMaxIntraOpThreads]. Throws std::invalid_argument otherwise.
[[nodiscard]] int parse_intra_op_threads(std::string_view value);

struct EngineSettings {
    EngineMode mode{EngineMode::Auto};
    std::filesystem::path modelPath{kDefaultModelPath};
    std::filesystem::path vocabPath{kDefaultVocabPath};
    finbert::LabelOrder labels{finbert::kDefaultLabelOrder};
    int intraOpThreads{kDefaultIntraOpThreads};

    /// Non-empty when any variable was invalid. The engine factory then fails closed (DEGRADED, MODEL_NOT_READY).
    std::vector<std::string> errors;

    [[nodiscard]] bool valid() const noexcept { return errors.empty(); }
};

/// Returns the variable's value, or std::nullopt when unset. Empty values are treated as unset.
using EnvLookup = std::function<std::optional<std::string>(std::string_view name)>;

/// Reads all SENTIX_* engine variables through `lookup`. Never throws for invalid values; collects them in `errors`.
[[nodiscard]] EngineSettings load_engine_settings(const EnvLookup& lookup);

/// load_engine_settings() over the process environment (std::getenv).
[[nodiscard]] EngineSettings load_engine_settings_from_environment();

}  // namespace sentix::engine
