#include <gtest/gtest.h>

#include <memory>
#include <regex>

#include "sentix/config.hpp"
#include "sentix/hashing.hpp"
#include "sentix/scoring_pipeline.hpp"
#include "sentix/sentiment_engine.hpp"
#include "test_helpers.hpp"

using namespace std::chrono_literals;
using sentix::pipeline::MarketEvent;
using sentix::pipeline::ScoringPipeline;
using Strings = std::vector<std::string>;

namespace {

/// Records every call so tests can prove the engine is NOT consulted for early drops.
class CountingEngine final : public sentix::sentiment::ISentimentEngine {
public:
    explicit CountingEngine(double composite) : composite_(composite) {}
    sentix::sentiment::SentimentScores score(std::string_view /*text*/) override {
        ++calls;
        const double pos = composite_ > 0 ? composite_ : 0.0;
        const double neg = composite_ < 0 ? -composite_ : 0.0;
        return {pos, neg, 1.0 - pos - neg, pos - neg};
    }
    bool is_ready() const noexcept override { return true; }
    std::string_view name() const noexcept override { return "counting"; }
    int calls{0};

private:
    double composite_;
};

MarketEvent event(std::string id, std::string headline, std::string body = "", std::string summary = "",
                  std::string source = "ALPACA_BENZINGA", std::string author = "Benzinga", Strings tickers = {}) {
    return MarketEvent{std::move(id), std::move(source), std::move(author), "2026-11-26T14:30:00Z", std::move(headline),
                       std::move(summary), std::move(body), std::move(tickers)};
}

class ScoringPipelineTest : public ::testing::Test {
protected:
    std::shared_ptr<const sentix::config::UniverseConfig> cfg = std::make_shared<const sentix::config::UniverseConfig>(
        sentix::config::load_universe_config(sentix::testing::universe_json_path()));
    sentix::sentiment::MockSentimentEngine engine;
    ScoringPipeline pipeline{cfg, engine};
    sentix::dedup::Clock::time_point now = sentix::dedup::Clock::time_point{} + 1000h;
};

}  // namespace

TEST_F(ScoringPipelineTest, StrongWatchlistNewsPassesToDecisionSwarm) {
    const auto r = pipeline.score(event("evt_98a7f6", "Apple announces record AI hardware demand", "<p>Apple beats guidance.</p>"), now);

    EXPECT_EQ(r.eventId, "evt_98a7f6");
    EXPECT_FALSE(r.isDuplicate);
    EXPECT_TRUE(std::regex_match(r.dedupHash, std::regex("^sha256:[0-9a-f]{64}$")));
    EXPECT_EQ(r.dedupHash, sentix::hashing::dedup_hash("Apple announces record AI hardware demand Apple beats guidance."));
    EXPECT_EQ(r.matchedTickers, (Strings{"AAPL"}));
    EXPECT_EQ(r.matchedSectorEtfs, (Strings{"XLK"}));
    EXPECT_GE(r.finbert.compositeScore, 0.55);
    EXPECT_NEAR(r.finbert.positive + r.finbert.negative + r.finbert.neutral, 1.0, 1e-12);
    EXPECT_TRUE(r.passToDecisionSwarm);
    EXPECT_FALSE(r.dropReason.has_value());
    EXPECT_GE(r.processingTimeUs, 0);
}

TEST_F(ScoringPipelineTest, HtmlBodyIsStrippedBeforeHashing) {
    const auto html = pipeline.score(event("e1", "Nvidia jumps", "<div><b>Upgraded</b>&nbsp;to buy</div>"), now);
    EXPECT_EQ(html.effectiveText, "Nvidia jumps Upgraded to buy");
    EXPECT_EQ(html.dedupHash, sentix::hashing::dedup_hash("Nvidia jumps Upgraded to buy"));
}

TEST_F(ScoringPipelineTest, EmptyBodyFallsBackToSummary) {
    const auto r = pipeline.score(event("e1", "Tesla misses", "<p> </p>", "Deliveries plunge on weak demand"), now);
    EXPECT_EQ(r.effectiveText, "Tesla misses Deliveries plunge on weak demand");
    EXPECT_EQ(r.matchedTickers, (Strings{"TSLA"}));
    EXPECT_LE(r.finbert.compositeScore, -0.55);
    EXPECT_TRUE(r.passToDecisionSwarm);
}

TEST_F(ScoringPipelineTest, SummaryFallbackAndBodyVariantDeduplicate) {
    EXPECT_FALSE(pipeline.score(event("e1", "Apple beats", "", "Record quarter"), now).isDuplicate);
    const auto second = pipeline.score(event("e2", "APPLE  beats", "<p>record quarter</p>", "other summary"), now + 1min);
    EXPECT_TRUE(second.isDuplicate);
    EXPECT_EQ(second.dropReason, "DUPLICATE");
}

TEST_F(ScoringPipelineTest, UnknownSourceIsNotAllowed) {
    CountingEngine counting(0.9);
    ScoringPipeline p(cfg, counting);
    const auto r = p.score(event("e1", "Apple beats", "", "", "REDDIT", "someone"), now);

    EXPECT_FALSE(r.passToDecisionSwarm);
    EXPECT_EQ(r.dropReason, "SOURCE_NOT_ALLOWED");
    EXPECT_TRUE(r.matchedTickers.empty());
    EXPECT_DOUBLE_EQ(r.finbert.neutral, 1.0);
    EXPECT_EQ(counting.calls, 0);
}

TEST_F(ScoringPipelineTest, XAllowlistRequiresAllowlistedHandle) {
    EXPECT_TRUE(ScoringPipeline::is_source_allowed(*cfg, "X_ALLOWLIST", "@WhiteHouse"));
    EXPECT_TRUE(ScoringPipeline::is_source_allowed(*cfg, "X_ALLOWLIST", "whitehouse"));
    EXPECT_TRUE(ScoringPipeline::is_source_allowed(*cfg, "X_ALLOWLIST", " @FEDERALRESERVE "));
    EXPECT_FALSE(ScoringPipeline::is_source_allowed(*cfg, "X_ALLOWLIST", "@elonmusk"));
    EXPECT_FALSE(ScoringPipeline::is_source_allowed(*cfg, "X_ALLOWLIST", "@"));
    EXPECT_FALSE(ScoringPipeline::is_source_allowed(*cfg, "X_ALLOWLIST", ""));
    EXPECT_FALSE(ScoringPipeline::is_source_allowed(*cfg, "X_ALLOWLIST", "@@WhiteHouse"));
    EXPECT_TRUE(ScoringPipeline::is_source_allowed(*cfg, "SEC_EDGAR", "anyone"));
    EXPECT_TRUE(ScoringPipeline::is_source_allowed(*cfg, "ALPACA_BENZINGA", "Benzinga"));
    EXPECT_FALSE(ScoringPipeline::is_source_allowed(*cfg, "alpaca_benzinga", "Benzinga"));
    EXPECT_FALSE(ScoringPipeline::is_source_allowed(*cfg, "", "Benzinga"));
}

TEST_F(ScoringPipelineTest, XPostFromNonAllowlistedHandleIsDropped) {
    const auto r = pipeline.score(event("x1", "Tesla soars, record gains", "", "", "X_ALLOWLIST", "@randomtrader"), now);
    EXPECT_EQ(r.dropReason, "SOURCE_NOT_ALLOWED");
}

TEST_F(ScoringPipelineTest, XPostFromAllowlistedHandlePassesWithSectorEtf) {
    const auto r = pipeline.score(event("x1", "Huge boost for Lockheed Martin: record defense contract wins approval", "", "",
                                        "X_ALLOWLIST", "@WhiteHouse"),
                                  now);
    EXPECT_EQ(r.matchedTickers, (Strings{"LMT"}));
    EXPECT_EQ(r.matchedSectorEtfs, (Strings{"ITA"}));
    EXPECT_TRUE(r.passToDecisionSwarm);
}

TEST_F(ScoringPipelineTest, SourceCheckPrecedesDedupAndDoesNotRecordHash) {
    const auto rejected = pipeline.score(event("e1", "Apple beats record", "", "", "UNKNOWN"), now);
    EXPECT_EQ(rejected.dropReason, "SOURCE_NOT_ALLOWED");
    EXPECT_FALSE(rejected.isDuplicate);

    const auto allowed = pipeline.score(event("e2", "Apple beats record"), now + 1s);
    EXPECT_FALSE(allowed.isDuplicate);
    EXPECT_TRUE(allowed.passToDecisionSwarm);

    const auto rejectedAgain = pipeline.score(event("e3", "Apple beats record", "", "", "UNKNOWN"), now + 2s);
    EXPECT_EQ(rejectedAgain.dropReason, "SOURCE_NOT_ALLOWED");  // source check wins over DUPLICATE
    EXPECT_FALSE(rejectedAgain.isDuplicate);
}

TEST_F(ScoringPipelineTest, DuplicateWithin180MinutesIsDropped_AfterWindowPassesAgain) {
    EXPECT_TRUE(pipeline.score(event("e1", "Apple beats record"), now).passToDecisionSwarm);

    const auto dup = pipeline.score(event("e2", "Apple beats record"), now + 179min);
    EXPECT_TRUE(dup.isDuplicate);
    EXPECT_EQ(dup.dropReason, "DUPLICATE");
    EXPECT_FALSE(dup.passToDecisionSwarm);
    EXPECT_TRUE(dup.matchedTickers.empty());

    const auto fresh = pipeline.score(event("e3", "Apple beats record"), now + 180min);
    EXPECT_FALSE(fresh.isDuplicate);
    EXPECT_TRUE(fresh.passToDecisionSwarm);
}

TEST_F(ScoringPipelineTest, NoWatchlistMatchIsDropped_AndHashIsStillRecorded) {
    CountingEngine counting(0.9);
    ScoringPipeline p(cfg, counting);
    const auto r = p.score(event("e1", "Unrelated company soars on record profit"), now);
    EXPECT_EQ(r.dropReason, "NO_WATCHLIST_MATCH");
    EXPECT_FALSE(r.passToDecisionSwarm);
    EXPECT_EQ(counting.calls, 0);

    EXPECT_EQ(p.score(event("e2", "Unrelated company soars on record profit"), now + 1min).dropReason, "DUPLICATE");
}

TEST_F(ScoringPipelineTest, SourceTickersAloneProduceAMatch) {
    const auto r = pipeline.score(event("e1", "Company soars on record profit", "", "", "ALPACA_BENZINGA", "Benzinga", {"amd"}), now);
    EXPECT_EQ(r.matchedTickers, (Strings{"AMD"}));
    EXPECT_EQ(r.matchedSectorEtfs, (Strings{"SMH"}));
    EXPECT_TRUE(r.passToDecisionSwarm);
}

TEST_F(ScoringPipelineTest, SectorEtfOnlyMatchPasses) {
    const auto r = pipeline.score(event("e1", "Energy stocks surge as XLE rallies"), now);
    EXPECT_TRUE(r.matchedTickers.empty());
    EXPECT_EQ(r.matchedSectorEtfs, (Strings{"XLE"}));
    EXPECT_TRUE(r.passToDecisionSwarm);
}

TEST_F(ScoringPipelineTest, WeakSentimentIsDroppedWithScoresReported) {
    const auto r = pipeline.score(event("e1", "Microsoft schedules developer conference"), now);
    EXPECT_EQ(r.matchedTickers, (Strings{"MSFT"}));
    EXPECT_EQ(r.dropReason, "LOW_SENTIMENT_STRENGTH");
    EXPECT_FALSE(r.passToDecisionSwarm);
    EXPECT_DOUBLE_EQ(r.finbert.neutral, 1.0);
}

TEST_F(ScoringPipelineTest, MinAbsCompositeScoreBoundaryIsInclusive) {
    CountingEngine exact(-0.55);
    ScoringPipeline atThreshold(cfg, exact);
    EXPECT_TRUE(atThreshold.score(event("e1", "AAPL update"), now).passToDecisionSwarm);

    CountingEngine below(0.5499);
    ScoringPipeline belowThreshold(cfg, below);
    EXPECT_EQ(belowThreshold.score(event("e1", "AAPL update"), now).dropReason, "LOW_SENTIMENT_STRENGTH");
}

TEST_F(ScoringPipelineTest, UpdateConfigSwapsUniverseAndKeepsDedupMemory) {
    EXPECT_TRUE(pipeline.score(event("e1", "Apple beats record"), now).passToDecisionSwarm);

    auto smaller = *cfg;
    smaller.watchlist = {{"ACME", "TECH", {"Acme Corp"}}};
    smaller.dedupWindowMinutes = 5;
    pipeline.update_config(std::make_shared<const sentix::config::UniverseConfig>(smaller));

    EXPECT_EQ(pipeline.config().dedupWindowMinutes, 5);
    EXPECT_EQ(pipeline.score(event("e2", "Apple beats record"), now + 1min).dropReason, "DUPLICATE");
    EXPECT_EQ(pipeline.score(event("e3", "Apple beats record"), now + 6min).dropReason, "NO_WATCHLIST_MATCH");
    EXPECT_TRUE(pipeline.score(event("e4", "Acme Corp beats estimates"), now + 7min).passToDecisionSwarm);
}

TEST(ScoreResultJson, SerializesSpecShapeWithNullDropReason) {
    sentix::pipeline::ScoreResult r;
    r.eventId = "evt_98a7f6";
    r.dedupHash = "sha256:abc";
    r.matchedTickers = {"AAPL"};
    r.matchedSectorEtfs = {"XLK"};
    r.finbert = {0.89, 0.03, 0.08, 0.86};
    r.passToDecisionSwarm = true;
    r.processingTimeUs = 18450;

    const auto j = sentix::pipeline::to_json(r);
    EXPECT_EQ(j.at("eventId"), "evt_98a7f6");
    EXPECT_EQ(j.at("isDuplicate"), false);
    EXPECT_EQ(j.at("dedupHash"), "sha256:abc");
    EXPECT_EQ(j.at("matchedTickers"), nlohmann::json::array({"AAPL"}));
    EXPECT_EQ(j.at("matchedSectorEtfs"), nlohmann::json::array({"XLK"}));
    EXPECT_DOUBLE_EQ(j.at("finbert").at("positive").get<double>(), 0.89);
    EXPECT_DOUBLE_EQ(j.at("finbert").at("compositeScore").get<double>(), 0.86);
    EXPECT_EQ(j.at("passToDecisionSwarm"), true);
    EXPECT_TRUE(j.at("dropReason").is_null());
    EXPECT_EQ(j.at("processingTimeUs"), 18450);
    EXPECT_EQ(j.size(), 9U);

    r.dropReason = "DUPLICATE";
    EXPECT_EQ(sentix::pipeline::to_json(r).at("dropReason"), "DUPLICATE");
}

TEST(MarketEventParsing, ParsesFullPayload) {
    const auto e = sentix::pipeline::parse_market_event(
        sentix::testing::market_event("evt_1", "Head", "<p>Body</p>", "Sum", "ALPACA_BENZINGA", "Benzinga", {"AAPL", "msft"}));
    EXPECT_EQ(e.eventId, "evt_1");
    EXPECT_EQ(e.source, "ALPACA_BENZINGA");
    EXPECT_EQ(e.author, "Benzinga");
    EXPECT_EQ(e.publishedAtUtc, "2026-11-26T14:30:00Z");
    EXPECT_EQ(e.headline, "Head");
    EXPECT_EQ(e.summary, "Sum");
    EXPECT_EQ(e.body, "<p>Body</p>");
    EXPECT_EQ(e.sourceTickers, (Strings{"AAPL", "msft"}));
}

TEST(MarketEventParsing, OptionalFieldsMayBeMissingOrNull) {
    const auto e = sentix::pipeline::parse_market_event(
        nlohmann::json{{"eventId", "e"}, {"source", "SEC_EDGAR"}, {"summary", nullptr}, {"sourceTickers", nullptr}});
    EXPECT_TRUE(e.headline.empty());
    EXPECT_TRUE(e.summary.empty());
    EXPECT_TRUE(e.body.empty());
    EXPECT_TRUE(e.sourceTickers.empty());
}

TEST(MarketEventParsing, RejectsInvalidPayloads) {
    using sentix::pipeline::DeserializationError;
    using nlohmann::json;
    EXPECT_THROW((void)sentix::pipeline::parse_market_event(json::array()), DeserializationError);
    EXPECT_THROW((void)sentix::pipeline::parse_market_event(json{{"source", "SEC_EDGAR"}}), DeserializationError);
    EXPECT_THROW((void)sentix::pipeline::parse_market_event(json{{"eventId", " "}, {"source", "SEC_EDGAR"}}), DeserializationError);
    EXPECT_THROW((void)sentix::pipeline::parse_market_event(json{{"eventId", 5}, {"source", "SEC_EDGAR"}}), DeserializationError);
    EXPECT_THROW((void)sentix::pipeline::parse_market_event(json{{"eventId", "e"}}), DeserializationError);
    EXPECT_THROW((void)sentix::pipeline::parse_market_event(json{{"eventId", "e"}, {"source", "S"}, {"body", 1}}), DeserializationError);
    EXPECT_THROW((void)sentix::pipeline::parse_market_event(json{{"eventId", "e"}, {"source", "S"}, {"sourceTickers", "AAPL"}}),
                 DeserializationError);
    EXPECT_THROW((void)sentix::pipeline::parse_market_event(json{{"eventId", "e"}, {"source", "S"}, {"sourceTickers", {1}}}),
                 DeserializationError);
}
