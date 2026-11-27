// SentiX native fast-path — JSON envelope & request dispatch (specs/002 §3-§4).
#pragma once

#include <atomic>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>

#include <nlohmann/json.hpp>

#include "sentix/config.hpp"
#include "sentix/scoring_pipeline.hpp"
#include "sentix/sentiment_engine.hpp"

namespace sentix::protocol {

inline constexpr int kProtocolVersion = 1;
inline constexpr std::string_view kEngineVersion = SENTIX_ENGINE_VERSION;

namespace type {
inline constexpr std::string_view kHealthCheck = "HEALTH_CHECK";
inline constexpr std::string_view kHealthCheckAck = "HEALTH_CHECK_ACK";
inline constexpr std::string_view kReloadConfig = "RELOAD_CONFIG";
inline constexpr std::string_view kReloadConfigAck = "RELOAD_CONFIG_ACK";
inline constexpr std::string_view kScoreEvent = "SCORE_EVENT";
inline constexpr std::string_view kScoreEventResult = "SCORE_EVENT_RESULT";
inline constexpr std::string_view kError = "ERROR";
}  // namespace type

namespace error {
inline constexpr std::string_view kInvalidFrame = "INVALID_FRAME";
inline constexpr std::string_view kPayloadTooLarge = "PAYLOAD_TOO_LARGE";
inline constexpr std::string_view kDeserializationError = "DESERIALIZATION_ERROR";
inline constexpr std::string_view kUnsupportedVersion = "UNSUPPORTED_VERSION";
inline constexpr std::string_view kModelNotReady = "MODEL_NOT_READY";
inline constexpr std::string_view kInferenceTimeout = "INFERENCE_TIMEOUT";
inline constexpr std::string_view kInternalError = "INTERNAL_ERROR";
}  // namespace error

enum class EngineStatus { Starting, Ready, Degraded };

[[nodiscard]] std::string_view to_string(EngineStatus status) noexcept;

/// ISO-8601 UTC with millisecond precision, e.g. "2026-11-26T14:30:05.123Z".
[[nodiscard]] std::string format_utc(std::chrono::system_clock::time_point tp);

/// Serializes a response envelope.
[[nodiscard]] std::string make_envelope(std::string_view type, std::string_view correlationId,
                                        std::chrono::system_clock::time_point timestamp, nlohmann::json payload);

/// Serializes an ERROR envelope.
[[nodiscard]] std::string make_error(std::string_view code, std::string_view message, bool retryable,
                                     std::string_view correlationId, std::chrono::system_clock::time_point timestamp);

struct Clocks {
    std::function<std::chrono::steady_clock::time_point()> steadyNow = [] { return std::chrono::steady_clock::now(); };
    std::function<std::chrono::system_clock::time_point()> systemNow = [] { return std::chrono::system_clock::now(); };
};

/// Thread-safe request dispatcher owning config, engine, dedup state and counters.
class RequestHandler {
public:
    RequestHandler(std::filesystem::path configPath, std::unique_ptr<sentiment::ISentimentEngine> engine, Clocks clocks = {});

    /// Loads the configured universe at startup. READY on success, DEGRADED otherwise (fail-closed).
    bool load_initial_config();

    /// Handles one request frame payload and returns the response envelope JSON (never throws).
    [[nodiscard]] std::string handle(std::string_view requestJson);

    /// ERROR envelope for transport-level failures (no correlation id available).
    [[nodiscard]] std::string frame_error(std::string_view code, std::string_view message) const;

    [[nodiscard]] EngineStatus status() const;
    [[nodiscard]] std::uint64_t processed_events_total() const noexcept { return processed_.load(); }

private:
    [[nodiscard]] std::string handle_health(const std::string& correlationId);
    [[nodiscard]] std::string handle_reload(const std::string& correlationId);
    [[nodiscard]] std::string handle_score(const std::string& correlationId, const nlohmann::json& payload);
    [[nodiscard]] std::optional<std::string> try_load_config();  // returns error message on failure

    std::filesystem::path configPath_;
    std::unique_ptr<sentiment::ISentimentEngine> engine_;
    Clocks clocks_;
    std::chrono::steady_clock::time_point startedAt_;

    mutable std::mutex mutex_;
    EngineStatus status_{EngineStatus::Starting};
    std::unique_ptr<pipeline::ScoringPipeline> pipeline_;
    std::atomic<std::uint64_t> processed_{0};
};

}  // namespace sentix::protocol
