#include "sentix/engine_config.hpp"

#include <charconv>
#include <cstdlib>
#include <stdexcept>
#include <system_error>

#include "sentix/text_cleaner.hpp"

namespace sentix::engine {

std::string_view to_string(EngineMode mode) noexcept {
    switch (mode) {
        case EngineMode::Onnx:
            return "onnx";
        case EngineMode::Mock:
            return "mock";
        case EngineMode::Auto:
        default:
            return "auto";
    }
}

EngineMode parse_engine_mode(std::string_view value) {
    const std::string mode = text::to_lower_ascii(text::trim(value));
    if (mode == "auto") {
        return EngineMode::Auto;
    }
    if (mode == "onnx") {
        return EngineMode::Onnx;
    }
    if (mode == "mock") {
        return EngineMode::Mock;
    }
    throw std::invalid_argument("SENTIX_ENGINE_MODE: unsupported value '" + std::string(value) + "' (expected auto|onnx|mock)");
}

int parse_intra_op_threads(std::string_view value) {
    const std::string_view trimmed = text::trim(value);
    int threads = 0;
    const char* const first = trimmed.data();
    const char* const last = trimmed.data() + trimmed.size();
    const auto [ptr, ec] = std::from_chars(first, last, threads);
    if (trimmed.empty() || ec != std::errc{} || ptr != last || threads < 1 || threads > kMaxIntraOpThreads) {
        throw std::invalid_argument("SENTIX_ONNX_INTRA_OP_THREADS: expected an integer in [1, " + std::to_string(kMaxIntraOpThreads) +
                                    "], got '" + std::string(value) + "'");
    }
    return threads;
}

EngineSettings load_engine_settings(const EnvLookup& lookup) {
    EngineSettings settings;
    const auto read = [&lookup](std::string_view name) -> std::optional<std::string> {
        auto value = lookup ? lookup(name) : std::nullopt;
        if (value && value->empty()) {
            return std::nullopt;
        }
        return value;
    };

    if (const auto mode = read(kEnvEngineMode)) {
        try {
            settings.mode = parse_engine_mode(*mode);
        } catch (const std::invalid_argument& ex) {
            settings.errors.emplace_back(ex.what());
        }
    }
    if (const auto model = read(kEnvModelPath)) {
        settings.modelPath = *model;
    }
    if (const auto vocab = read(kEnvVocabPath)) {
        settings.vocabPath = *vocab;
    }
    if (const auto labels = read(kEnvFinbertLabels)) {
        try {
            settings.labels = finbert::parse_label_order(*labels);
        } catch (const std::invalid_argument& ex) {
            settings.errors.emplace_back(ex.what());
        }
    }
    if (const auto threads = read(kEnvIntraOpThreads)) {
        try {
            settings.intraOpThreads = parse_intra_op_threads(*threads);
        } catch (const std::invalid_argument& ex) {
            settings.errors.emplace_back(ex.what());
        }
    }
    return settings;
}

EngineSettings load_engine_settings_from_environment() {
    return load_engine_settings([](std::string_view name) -> std::optional<std::string> {
        const std::string key(name);
        const char* value = std::getenv(key.c_str());
        if (value == nullptr) {
            return std::nullopt;
        }
        return std::string(value);
    });
}

}  // namespace sentix::engine
