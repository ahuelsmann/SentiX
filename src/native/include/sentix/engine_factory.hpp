// SentiX native fast-path — fail-closed sentiment engine selection (specs/002 §7 "Mode Semantics").
//
//   mock : MockSentimentEngine.
//   onnx : OnnxFinbertSentimentEngine; any missing/unreadable/corrupt model or vocab -> UnavailableSentimentEngine.
//   auto : model AND vocab present on disk -> exactly like "onnx" (never silently downgraded to mock);
//          both absent -> MockSentimentEngine; only one present -> UnavailableSentimentEngine.
//   Invalid SENTIX_* settings always yield UnavailableSentimentEngine.
//
// An UnavailableSentimentEngine is never ready, so RequestHandler reports HEALTH_CHECK_ACK status DEGRADED with
// modelLoaded=false and answers SCORE_EVENT with ERROR MODEL_NOT_READY (zero trades).
#pragma once

#include <filesystem>
#include <memory>
#include <string>

#include "sentix/engine_config.hpp"
#include "sentix/sentiment_engine.hpp"

namespace sentix::engine {

/// True when this binary was compiled with SENTIX_ENABLE_ONNX=ON.
[[nodiscard]] bool onnx_support_compiled() noexcept;

/// True when `path` exists on disk (including dangling symlinks). Paths that cannot be probed (e.g. EACCES) also
/// count as present, so the factory fails closed instead of falling back to mock.
[[nodiscard]] bool path_present(const std::filesystem::path& path) noexcept;

struct EngineSelection {
    std::unique_ptr<sentiment::ISentimentEngine> engine;  // never null
    std::string description;                              // single-line, human-readable (for startup logs)

    [[nodiscard]] bool ready() const noexcept { return engine && engine->is_ready(); }
};

/// Selects and initializes the engine. Never throws; failures produce a non-ready UnavailableSentimentEngine.
/// `onnxRuntimeLibrary` overrides the libonnxruntime.so search (empty = default search).
[[nodiscard]] EngineSelection create_sentiment_engine(const EngineSettings& settings,
                                                      const std::filesystem::path& onnxRuntimeLibrary = {});

}  // namespace sentix::engine
