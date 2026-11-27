// SentiX native fast-path — SCORE_EVENT pipeline (specs/002 §4.3 + §5).
#pragma once

#include <chrono>
#include <cstdint>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#include <nlohmann/json.hpp>

#include "sentix/config.hpp"
#include "sentix/dedup_cache.hpp"
#include "sentix/sentiment_engine.hpp"
#include "sentix/watchlist_matcher.hpp"

namespace sentix::pipeline {

inline constexpr std::string_view kDropSourceNotAllowed = "SOURCE_NOT_ALLOWED";
inline constexpr std::string_view kDropDuplicate = "DUPLICATE";
inline constexpr std::string_view kDropNoWatchlistMatch = "NO_WATCHLIST_MATCH";
inline constexpr std::string_view kDropLowSentimentStrength = "LOW_SENTIMENT_STRENGTH";

struct MarketEvent {
    std::string eventId;
    std::string source;
    std::string author;
    std::string publishedAtUtc;
    std::string headline;
    std::string summary;
    std::string body;
    std::vector<std::string> sourceTickers;
};

class DeserializationError : public std::runtime_error {
public:
    using std::runtime_error::runtime_error;
};

/// Converts a SCORE_EVENT payload. `eventId` and `source` are required non-empty strings; the remaining
/// fields are optional (missing/null -> empty) but must have the right JSON type. Throws DeserializationError.
[[nodiscard]] MarketEvent parse_market_event(const nlohmann::json& payload);

struct ScoreResult {
    std::string eventId;
    bool isDuplicate{false};
    std::string dedupHash;
    std::vector<std::string> matchedTickers;
    std::vector<std::string> matchedSectorEtfs;
    sentiment::SentimentScores finbert{};
    bool passToDecisionSwarm{false};
    std::optional<std::string> dropReason;
    std::int64_t processingTimeUs{0};
    std::string effectiveText;  // diagnostic only, not serialized
};

/// SCORE_EVENT_RESULT payload (camelCase, `dropReason` null when passing).
[[nodiscard]] nlohmann::json to_json(const ScoreResult& result);

/// Deterministic evaluation in the exact spec order:
/// clean + summary fallback -> hash -> SOURCE_NOT_ALLOWED -> DUPLICATE -> NO_WATCHLIST_MATCH -> LOW_SENTIMENT_STRENGTH -> pass.
class ScoringPipeline {
public:
    ScoringPipeline(std::shared_ptr<const config::UniverseConfig> config, sentiment::ISentimentEngine& engine);

    [[nodiscard]] ScoreResult score(const MarketEvent& event, dedup::Clock::time_point now);

    /// Swaps the active universe (dedup memory is preserved, window follows the new config).
    void update_config(std::shared_ptr<const config::UniverseConfig> config);

    [[nodiscard]] const config::UniverseConfig& config() const noexcept { return *config_; }

    /// Check 1: source allowlist (+ X handle allowlist, leading '@' optional, case-insensitive).
    [[nodiscard]] static bool is_source_allowed(const config::UniverseConfig& config, std::string_view source, std::string_view author);

private:
    std::shared_ptr<const config::UniverseConfig> config_;
    sentiment::ISentimentEngine& engine_;
    matching::WatchlistMatcher matcher_;
    dedup::DedupCache dedup_;
};

}  // namespace sentix::pipeline
