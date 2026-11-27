#include "sentix/protocol.hpp"

#include <cstdio>
#include <ctime>
#include <exception>

namespace sentix::protocol {
namespace {

using nlohmann::json;

struct EnvelopeError {
    std::string_view code;
    std::string message;
};

}  // namespace

std::string_view to_string(EngineStatus status) noexcept {
    switch (status) {
        case EngineStatus::Ready:
            return "READY";
        case EngineStatus::Degraded:
            return "DEGRADED";
        case EngineStatus::Starting:
        default:
            return "STARTING";
    }
}

std::string format_utc(std::chrono::system_clock::time_point tp) {
    const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(tp.time_since_epoch()).count();
    auto seconds = static_cast<std::time_t>(ms / 1000);
    auto millis = ms % 1000;
    if (millis < 0) {
        millis += 1000;
        --seconds;
    }
    std::tm utc{};
    ::gmtime_r(&seconds, &utc);
    char buffer[32];
    const std::size_t n = std::strftime(buffer, sizeof(buffer), "%Y-%m-%dT%H:%M:%S", &utc);
    char out[48];
    std::snprintf(out, sizeof(out), "%.*s.%03dZ", static_cast<int>(n), buffer, static_cast<int>(millis));
    return out;
}

std::string make_envelope(std::string_view type, std::string_view correlationId, std::chrono::system_clock::time_point timestamp,
                          json payload) {
    const json envelope{
        {"protocolVersion", kProtocolVersion},
        {"correlationId", std::string(correlationId)},
        {"type", std::string(type)},
        {"timestampUtc", format_utc(timestamp)},
        {"payload", std::move(payload)},
    };
    return envelope.dump(-1, ' ', false, json::error_handler_t::replace);
}

std::string make_error(std::string_view code, std::string_view message, bool retryable, std::string_view correlationId,
                       std::chrono::system_clock::time_point timestamp) {
    return make_envelope(type::kError, correlationId, timestamp,
                         json{{"code", std::string(code)}, {"message", std::string(message)}, {"retryable", retryable}});
}

RequestHandler::RequestHandler(std::filesystem::path configPath, std::unique_ptr<sentiment::ISentimentEngine> engine, Clocks clocks)
    : configPath_(std::move(configPath)), engine_(std::move(engine)), clocks_(std::move(clocks)), startedAt_(clocks_.steadyNow()) {}

EngineStatus RequestHandler::status() const {
    const std::lock_guard lock(mutex_);
    return status_;
}

bool RequestHandler::load_initial_config() {
    const std::lock_guard lock(mutex_);
    return !try_load_config().has_value();
}

std::optional<std::string> RequestHandler::try_load_config() {
    try {
        auto cfg = std::make_shared<const config::UniverseConfig>(config::load_universe_config(configPath_));
        if (pipeline_) {
            pipeline_->update_config(std::move(cfg));
        } else {
            pipeline_ = std::make_unique<pipeline::ScoringPipeline>(std::move(cfg), *engine_);
        }
        status_ = engine_ && engine_->is_ready() ? EngineStatus::Ready : EngineStatus::Degraded;
        return std::nullopt;
    } catch (const std::exception& ex) {
        // Preserve the previous active config if one exists; otherwise fail closed.
        if (!pipeline_) {
            status_ = EngineStatus::Degraded;
        }
        return std::string(ex.what());
    }
}

std::string RequestHandler::frame_error(std::string_view code, std::string_view message) const {
    return make_error(code, message, false, "", clocks_.systemNow());
}

std::string RequestHandler::handle(std::string_view requestJson) {
    std::string correlationId;
    try {
        json request;
        try {
            request = json::parse(requestJson.begin(), requestJson.end());
        } catch (const json::exception& ex) {
            return make_error(error::kDeserializationError, std::string("invalid JSON: ") + ex.what(), false, "", clocks_.systemNow());
        }

        if (!request.is_object()) {
            return make_error(error::kDeserializationError, "envelope must be a JSON object", false, "", clocks_.systemNow());
        }
        if (const auto it = request.find("correlationId"); it != request.end() && it->is_string()) {
            correlationId = it->get<std::string>();
        }

        const auto version = request.find("protocolVersion");
        if (version == request.end() || !version->is_number()) {
            return make_error(error::kDeserializationError, "envelope.protocolVersion must be a number", false, correlationId,
                              clocks_.systemNow());
        }
        if (!version->is_number_integer() || version->get<long long>() != kProtocolVersion) {
            return make_error(error::kUnsupportedVersion, "only protocolVersion 1 is supported", false, correlationId, clocks_.systemNow());
        }

        const auto typeIt = request.find("type");
        if (typeIt == request.end() || !typeIt->is_string()) {
            return make_error(error::kDeserializationError, "envelope.type must be a string", false, correlationId, clocks_.systemNow());
        }
        if (const auto it = request.find("correlationId"); it == request.end() || !it->is_string()) {
            return make_error(error::kDeserializationError, "envelope.correlationId must be a string", false, "", clocks_.systemNow());
        }

        json payload = json::object();
        if (const auto it = request.find("payload"); it != request.end() && !it->is_null()) {
            if (!it->is_object()) {
                return make_error(error::kDeserializationError, "envelope.payload must be a JSON object", false, correlationId,
                                  clocks_.systemNow());
            }
            payload = *it;
        }

        const std::string requestType = typeIt->get<std::string>();
        if (requestType == type::kHealthCheck) {
            return handle_health(correlationId);
        }
        if (requestType == type::kReloadConfig) {
            return handle_reload(correlationId);
        }
        if (requestType == type::kScoreEvent) {
            return handle_score(correlationId, payload);
        }
        return make_error(error::kDeserializationError, "unsupported message type '" + requestType + "'", false, correlationId,
                          clocks_.systemNow());
    } catch (const std::exception& ex) {
        return make_error(error::kInternalError, ex.what(), true, correlationId, clocks_.systemNow());
    } catch (...) {
        return make_error(error::kInternalError, "unknown internal error", true, correlationId, clocks_.systemNow());
    }
}

std::string RequestHandler::handle_health(const std::string& correlationId) {
    const std::lock_guard lock(mutex_);
    const auto uptime = std::chrono::duration_cast<std::chrono::seconds>(clocks_.steadyNow() - startedAt_).count();
    const bool modelLoaded = engine_ && engine_->is_ready() && pipeline_ != nullptr;
    return make_envelope(type::kHealthCheckAck, correlationId, clocks_.systemNow(),
                         json{
                             {"status", std::string(to_string(status_))},
                             {"engineVersion", std::string(kEngineVersion)},
                             {"modelLoaded", modelLoaded},
                             {"uptimeSeconds", uptime < 0 ? 0 : static_cast<std::int64_t>(uptime)},
                             {"processedEventsTotal", processed_.load()},
                         });
}

std::string RequestHandler::handle_reload(const std::string& correlationId) {
    const std::lock_guard lock(mutex_);
    if (const auto failure = try_load_config()) {
        return make_error(error::kInternalError, "RELOAD_CONFIG failed: " + *failure, true, correlationId, clocks_.systemNow());
    }
    const auto& cfg = pipeline_->config();
    return make_envelope(type::kReloadConfigAck, correlationId, clocks_.systemNow(),
                         json{
                             {"status", "RELOADED"},
                             {"configVersion", cfg.version},
                             {"watchlistCount", cfg.watchlist.size()},
                             {"sectorEtfCount", cfg.sectorEtfs.size()},
                             {"socialAllowlistCount", cfg.socialAllowlist.size()},
                         });
}

std::string RequestHandler::handle_score(const std::string& correlationId, const json& payload) {
    pipeline::MarketEvent event;
    try {
        event = pipeline::parse_market_event(payload);
    } catch (const pipeline::DeserializationError& ex) {
        return make_error(error::kDeserializationError, ex.what(), false, correlationId, clocks_.systemNow());
    }

    const std::lock_guard lock(mutex_);
    if (status_ != EngineStatus::Ready || !pipeline_ || !engine_ || !engine_->is_ready()) {
        return make_error(error::kModelNotReady, "sentiment engine or universe config not ready", true, correlationId,
                          clocks_.systemNow());
    }
    const auto result = pipeline_->score(event, clocks_.steadyNow());
    processed_.fetch_add(1);
    return make_envelope(type::kScoreEventResult, correlationId, clocks_.systemNow(), pipeline::to_json(result));
}

}  // namespace sentix::protocol
