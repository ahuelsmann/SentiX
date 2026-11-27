#include "sentix/scoring_pipeline.hpp"

#include <cmath>

#include "sentix/hashing.hpp"
#include "sentix/text_cleaner.hpp"

namespace sentix::pipeline {
namespace {

using nlohmann::json;

std::string required_string(const json& payload, const char* key) {
    const auto it = payload.find(key);
    if (it == payload.end() || !it->is_string()) {
        throw DeserializationError(std::string("MarketEvent.") + key + " must be a string");
    }
    std::string value = it->get<std::string>();
    if (text::trim(value).empty()) {
        throw DeserializationError(std::string("MarketEvent.") + key + " must not be empty");
    }
    return value;
}

std::string optional_string(const json& payload, const char* key) {
    const auto it = payload.find(key);
    if (it == payload.end() || it->is_null()) {
        return {};
    }
    if (!it->is_string()) {
        throw DeserializationError(std::string("MarketEvent.") + key + " must be a string");
    }
    return it->get<std::string>();
}

}  // namespace

MarketEvent parse_market_event(const json& payload) {
    if (!payload.is_object()) {
        throw DeserializationError("SCORE_EVENT payload must be a JSON object");
    }
    MarketEvent event;
    event.eventId = required_string(payload, "eventId");
    event.source = required_string(payload, "source");
    event.author = optional_string(payload, "author");
    event.publishedAtUtc = optional_string(payload, "publishedAtUtc");
    event.headline = optional_string(payload, "headline");
    event.summary = optional_string(payload, "summary");
    event.body = optional_string(payload, "body");

    if (const auto it = payload.find("sourceTickers"); it != payload.end() && !it->is_null()) {
        if (!it->is_array()) {
            throw DeserializationError("MarketEvent.sourceTickers must be an array of strings");
        }
        for (const auto& ticker : *it) {
            if (!ticker.is_string()) {
                throw DeserializationError("MarketEvent.sourceTickers must be an array of strings");
            }
            event.sourceTickers.push_back(ticker.get<std::string>());
        }
    }
    return event;
}

json to_json(const ScoreResult& result) {
    return json{
        {"eventId", result.eventId},
        {"isDuplicate", result.isDuplicate},
        {"dedupHash", result.dedupHash},
        {"matchedTickers", result.matchedTickers},
        {"matchedSectorEtfs", result.matchedSectorEtfs},
        {"finbert",
         {
             {"positive", result.finbert.positive},
             {"negative", result.finbert.negative},
             {"neutral", result.finbert.neutral},
             {"compositeScore", result.finbert.compositeScore},
         }},
        {"passToDecisionSwarm", result.passToDecisionSwarm},
        {"dropReason", result.dropReason ? json(*result.dropReason) : json(nullptr)},
        {"processingTimeUs", result.processingTimeUs},
    };
}

ScoringPipeline::ScoringPipeline(std::shared_ptr<const config::UniverseConfig> config, sentiment::ISentimentEngine& engine)
    : config_(std::move(config)),
      engine_(engine),
      matcher_(*config_),
      dedup_(std::chrono::minutes(config_->dedupWindowMinutes)) {}

void ScoringPipeline::update_config(std::shared_ptr<const config::UniverseConfig> config) {
    config_ = std::move(config);
    matcher_ = matching::WatchlistMatcher(*config_);
    dedup_.set_window(std::chrono::minutes(config_->dedupWindowMinutes));
}

bool ScoringPipeline::is_source_allowed(const config::UniverseConfig& config, std::string_view source, std::string_view author) {
    const std::string_view src = text::trim(source);
    if (src == config::kXAllowlistSource) {
        std::string_view handle = text::trim(author);
        if (!handle.empty() && handle.front() == '@') {
            handle.remove_prefix(1);
        }
        if (handle.empty()) {
            return false;
        }
        for (const auto& entry : config.socialAllowlist) {
            if (entry.source == config::kXAllowlistSource && text::iequals(entry.identifier, handle)) {
                return true;
            }
        }
        return false;
    }
    for (const auto& entry : config.socialAllowlist) {
        if (entry.source == src) {
            return true;
        }
    }
    return false;
}

ScoreResult ScoringPipeline::score(const MarketEvent& event, dedup::Clock::time_point now) {
    const auto started = std::chrono::steady_clock::now();
    ScoreResult result;
    result.eventId = event.eventId;

    // Steps 1-3: clean, summary fallback, dedup hash.
    result.effectiveText = text::build_effective_text(event.headline, event.summary, event.body);
    result.dedupHash = hashing::dedup_hash(result.effectiveText);

    const auto finish = [&](std::optional<std::string_view> dropReason) {
        result.passToDecisionSwarm = !dropReason.has_value();
        if (dropReason) {
            result.dropReason = std::string(*dropReason);
        }
        result.processingTimeUs =
            std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - started).count();
        return result;
    };

    // Check 1 — allowlist.
    if (!is_source_allowed(*config_, event.source, event.author)) {
        return finish(kDropSourceNotAllowed);
    }

    // Check 2 — rolling-window dedup (records the hash on non-duplicate evaluation).
    if (dedup_.check_and_record(result.dedupHash, now)) {
        result.isDuplicate = true;
        return finish(kDropDuplicate);
    }

    // Check 3 — watchlist & sector ETF mapping.
    auto matches = matcher_.match(result.effectiveText, event.sourceTickers);
    result.matchedTickers = std::move(matches.matchedTickers);
    result.matchedSectorEtfs = std::move(matches.matchedSectorEtfs);
    if (result.matchedTickers.empty() && result.matchedSectorEtfs.empty()) {
        return finish(kDropNoWatchlistMatch);
    }

    // Check 4 — sentiment strength.
    result.finbert = engine_.score(result.effectiveText);
    if (!(std::fabs(result.finbert.compositeScore) >= config_->minAbsCompositeScore)) {
        return finish(kDropLowSentimentStrength);
    }

    return finish(std::nullopt);
}

}  // namespace sentix::pipeline
