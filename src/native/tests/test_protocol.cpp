#include <gtest/gtest.h>

#include <memory>
#include <regex>

#include <nlohmann/json.hpp>

#include "sentix/protocol.hpp"
#include "sentix/sentiment_engine.hpp"
#include "test_helpers.hpp"

using namespace std::chrono_literals;
using nlohmann::json;
using sentix::protocol::EngineStatus;
using sentix::protocol::RequestHandler;
using sentix::testing::envelope;
using sentix::testing::market_event;

namespace {

class NotReadyEngine final : public sentix::sentiment::ISentimentEngine {
public:
    sentix::sentiment::SentimentScores score(std::string_view /*text*/) override { return {}; }
    bool is_ready() const noexcept override { return false; }
    std::string_view name() const noexcept override { return "not-ready"; }
};

class ProtocolTest : public ::testing::Test {
protected:
    sentix::testing::TempDir dir;
    sentix::testing::FakeClocks fake;
    std::filesystem::path configPath = dir.write("universe.json", sentix::testing::read_file(sentix::testing::universe_json_path()));

    std::unique_ptr<RequestHandler> make_handler(bool load = true) {
        auto handler = std::make_unique<RequestHandler>(configPath, std::make_unique<sentix::sentiment::MockSentimentEngine>(), fake.clocks());
        if (load) {
            EXPECT_TRUE(handler->load_initial_config());
        }
        return handler;
    }

    static json call(RequestHandler& handler, const json& request) { return json::parse(handler.handle(request.dump())); }
};

void expect_envelope(const json& response, std::string_view type, std::string_view correlationId) {
    EXPECT_EQ(response.at("protocolVersion"), 1);
    EXPECT_EQ(response.at("type"), type);
    EXPECT_EQ(response.at("correlationId"), correlationId);
    EXPECT_TRUE(std::regex_match(response.at("timestampUtc").get<std::string>(),
                                 std::regex(R"(^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$)")));
    EXPECT_TRUE(response.at("payload").is_object());
}

void expect_error(const json& response, std::string_view code, bool retryable) {
    EXPECT_EQ(response.at("type"), "ERROR");
    EXPECT_EQ(response.at("payload").at("code"), code);
    EXPECT_EQ(response.at("payload").at("retryable"), retryable);
    EXPECT_FALSE(response.at("payload").at("message").get<std::string>().empty());
}

}  // namespace

TEST(ProtocolFormatting, FormatsUtcWithMilliseconds) {
    using namespace std::chrono;
    const system_clock::time_point tp = sys_days{year{2026} / 11 / 26} + 14h + 30min + 5s + 123ms;
    EXPECT_EQ(sentix::protocol::format_utc(tp), "2026-11-26T14:30:05.123Z");
    EXPECT_EQ(sentix::protocol::format_utc(system_clock::time_point{}), "1970-01-01T00:00:00.000Z");
}

TEST(ProtocolFormatting, StatusStrings) {
    EXPECT_EQ(sentix::protocol::to_string(EngineStatus::Starting), "STARTING");
    EXPECT_EQ(sentix::protocol::to_string(EngineStatus::Ready), "READY");
    EXPECT_EQ(sentix::protocol::to_string(EngineStatus::Degraded), "DEGRADED");
}

TEST_F(ProtocolTest, HealthCheckAckMatchesSpec) {
    auto handler = make_handler();
    fake.advance(120s);

    const auto response = call(*handler, envelope("HEALTH_CHECK"));

    expect_envelope(response, "HEALTH_CHECK_ACK", "550e8400-e29b-41d4-a716-446655440000");
    const auto& p = response.at("payload");
    EXPECT_EQ(p.at("status"), "READY");
    EXPECT_EQ(p.at("engineVersion"), "0.1.0");
    EXPECT_EQ(p.at("modelLoaded"), true);
    EXPECT_EQ(p.at("uptimeSeconds"), 120);
    EXPECT_EQ(p.at("processedEventsTotal"), 0);
    EXPECT_EQ(p.size(), 5U);
}

TEST_F(ProtocolTest, HealthReportsStartingBeforeConfigLoad) {
    auto handler = make_handler(false);
    EXPECT_EQ(handler->status(), EngineStatus::Starting);
    const auto p = call(*handler, envelope("HEALTH_CHECK")).at("payload");
    EXPECT_EQ(p.at("status"), "STARTING");
    EXPECT_EQ(p.at("modelLoaded"), false);
}

TEST_F(ProtocolTest, MissingConfigIsDegradedAndScoringFailsClosed) {
    configPath = dir.path() / "missing.json";
    auto handler = make_handler(false);
    EXPECT_FALSE(handler->load_initial_config());
    EXPECT_EQ(handler->status(), EngineStatus::Degraded);

    EXPECT_EQ(call(*handler, envelope("HEALTH_CHECK")).at("payload").at("status"), "DEGRADED");
    expect_error(call(*handler, envelope("SCORE_EVENT", market_event("e1", "Apple beats record"))), "MODEL_NOT_READY", true);
    expect_error(call(*handler, envelope("RELOAD_CONFIG")), "INTERNAL_ERROR", true);
    EXPECT_EQ(handler->status(), EngineStatus::Degraded);
}

TEST_F(ProtocolTest, EngineNotReadyIsDegradedAndFailsClosed) {
    RequestHandler handler(configPath, std::make_unique<NotReadyEngine>(), fake.clocks());
    EXPECT_TRUE(handler.load_initial_config());
    EXPECT_EQ(handler.status(), EngineStatus::Degraded);
    EXPECT_EQ(call(handler, envelope("HEALTH_CHECK")).at("payload").at("modelLoaded"), false);
    expect_error(call(handler, envelope("SCORE_EVENT", market_event("e1", "Apple beats record"))), "MODEL_NOT_READY", true);
}

TEST_F(ProtocolTest, ReloadConfigAckReportsCounts) {
    auto handler = make_handler();
    const auto response = call(*handler, envelope("RELOAD_CONFIG", json::object(), "corr-reload"));

    expect_envelope(response, "RELOAD_CONFIG_ACK", "corr-reload");
    const auto& p = response.at("payload");
    EXPECT_EQ(p.at("status"), "RELOADED");
    EXPECT_EQ(p.at("configVersion"), 1);
    EXPECT_EQ(p.at("watchlistCount"), 10);
    EXPECT_EQ(p.at("sectorEtfCount"), 8);
    EXPECT_EQ(p.at("socialAllowlistCount"), 7);
}

TEST_F(ProtocolTest, ReloadConfigPicksUpChangedFile) {
    auto handler = make_handler();
    EXPECT_EQ(call(*handler, envelope("SCORE_EVENT", market_event("e1", "Acme Corp beats record"))).at("payload").at("dropReason"),
              "NO_WATCHLIST_MATCH");

    auto doc = json::parse(sentix::testing::read_file(configPath));
    doc["version"] = 2;
    doc["watchlist"].push_back({{"ticker", "ACME"}, {"sector", "INDUSTRIALS"}, {"aliases", {"Acme Corp"}}});
    dir.write("universe.json", doc.dump());

    const auto ack = call(*handler, envelope("RELOAD_CONFIG")).at("payload");
    EXPECT_EQ(ack.at("configVersion"), 2);
    EXPECT_EQ(ack.at("watchlistCount"), 11);

    const auto p = call(*handler, envelope("SCORE_EVENT", market_event("e2", "Acme Corp soars on record profit"))).at("payload");
    EXPECT_EQ(p.at("matchedTickers"), json::array({"ACME"}));
    EXPECT_EQ(p.at("matchedSectorEtfs"), json::array({"XLI"}));
    EXPECT_EQ(p.at("passToDecisionSwarm"), true);
}

TEST_F(ProtocolTest, ReloadFailurePreservesPreviousConfig) {
    auto handler = make_handler();
    dir.write("universe.json", "{ broken json");

    expect_error(call(*handler, envelope("RELOAD_CONFIG")), "INTERNAL_ERROR", true);
    EXPECT_EQ(handler->status(), EngineStatus::Ready);
    EXPECT_EQ(call(*handler, envelope("SCORE_EVENT", market_event("e1", "Apple beats record"))).at("payload").at("passToDecisionSwarm"),
              true);
}

TEST_F(ProtocolTest, ReloadRecoversFromDegraded) {
    const auto realPath = configPath;
    configPath = dir.path() / "late.json";
    auto handler = make_handler(false);
    EXPECT_FALSE(handler->load_initial_config());
    EXPECT_EQ(handler->status(), EngineStatus::Degraded);

    std::filesystem::copy_file(realPath, configPath);
    EXPECT_EQ(call(*handler, envelope("RELOAD_CONFIG")).at("type"), "RELOAD_CONFIG_ACK");
    EXPECT_EQ(handler->status(), EngineStatus::Ready);
}

TEST_F(ProtocolTest, ScoreEventResultAndProcessedCounter) {
    auto handler = make_handler();
    const auto response = call(*handler, envelope("SCORE_EVENT",
                                                  market_event("evt_98a7f6", "Apple announces record AI hardware demand",
                                                               "<p>Apple beats guidance on strong demand.</p>", "", "ALPACA_BENZINGA",
                                                               "Benzinga", {"AAPL"}),
                                                  "corr-score"));

    expect_envelope(response, "SCORE_EVENT_RESULT", "corr-score");
    const auto& p = response.at("payload");
    EXPECT_EQ(p.at("eventId"), "evt_98a7f6");
    EXPECT_EQ(p.at("isDuplicate"), false);
    EXPECT_TRUE(std::regex_match(p.at("dedupHash").get<std::string>(), std::regex("^sha256:[0-9a-f]{64}$")));
    EXPECT_EQ(p.at("matchedTickers"), json::array({"AAPL"}));
    EXPECT_EQ(p.at("matchedSectorEtfs"), json::array({"XLK"}));
    EXPECT_EQ(p.at("passToDecisionSwarm"), true);
    EXPECT_TRUE(p.at("dropReason").is_null());
    EXPECT_GE(p.at("processingTimeUs").get<long long>(), 0);
    const auto& f = p.at("finbert");
    EXPECT_NEAR(f.at("positive").get<double>() + f.at("negative").get<double>() + f.at("neutral").get<double>(), 1.0, 1e-12);

    // Duplicate within the window.
    fake.advance(10min);
    const auto dup = call(*handler, envelope("SCORE_EVENT",
                                             market_event("evt_dup", "Apple announces record AI hardware demand",
                                                          "<p>Apple beats guidance on strong demand.</p>")))
                         .at("payload");
    EXPECT_EQ(dup.at("isDuplicate"), true);
    EXPECT_EQ(dup.at("dropReason"), "DUPLICATE");

    // After the 180 min window the same text passes again (window is driven by the injected clock).
    fake.advance(171min);
    EXPECT_EQ(call(*handler, envelope("SCORE_EVENT",
                                      market_event("evt_late", "Apple announces record AI hardware demand",
                                                   "<p>Apple beats guidance on strong demand.</p>")))
                  .at("payload")
                  .at("passToDecisionSwarm"),
              true);

    EXPECT_EQ(handler->processed_events_total(), 3U);
    EXPECT_EQ(call(*handler, envelope("HEALTH_CHECK")).at("payload").at("processedEventsTotal"), 3);
}

TEST_F(ProtocolTest, InvalidJsonIsDeserializationError) {
    auto handler = make_handler();
    const auto response = json::parse(handler->handle("{ not json"));
    expect_error(response, "DESERIALIZATION_ERROR", false);
    EXPECT_EQ(response.at("correlationId"), "");
    expect_error(json::parse(handler->handle("[1,2,3]")), "DESERIALIZATION_ERROR", false);
}

TEST_F(ProtocolTest, UnsupportedProtocolVersion) {
    auto handler = make_handler();
    for (const json& version : {json(2), json(0), json(-1), json(1.5)}) {
        auto request = envelope("HEALTH_CHECK");
        request["protocolVersion"] = version;
        const auto response = call(*handler, request);
        expect_error(response, "UNSUPPORTED_VERSION", false);
        EXPECT_EQ(response.at("correlationId"), "550e8400-e29b-41d4-a716-446655440000");
    }
}

TEST_F(ProtocolTest, MalformedEnvelopesAreDeserializationErrors) {
    auto handler = make_handler();

    auto noVersion = envelope("HEALTH_CHECK");
    noVersion.erase("protocolVersion");
    expect_error(call(*handler, noVersion), "DESERIALIZATION_ERROR", false);

    auto versionString = envelope("HEALTH_CHECK");
    versionString["protocolVersion"] = "1";
    expect_error(call(*handler, versionString), "DESERIALIZATION_ERROR", false);

    auto noType = envelope("HEALTH_CHECK");
    noType.erase("type");
    expect_error(call(*handler, noType), "DESERIALIZATION_ERROR", false);

    auto noCorrelation = envelope("HEALTH_CHECK");
    noCorrelation.erase("correlationId");
    expect_error(call(*handler, noCorrelation), "DESERIALIZATION_ERROR", false);

    auto payloadArray = envelope("HEALTH_CHECK");
    payloadArray["payload"] = json::array();
    expect_error(call(*handler, payloadArray), "DESERIALIZATION_ERROR", false);

    expect_error(call(*handler, envelope("SELF_DESTRUCT")), "DESERIALIZATION_ERROR", false);
    expect_error(call(*handler, envelope("SCORE_EVENT", json{{"eventId", "e"}})), "DESERIALIZATION_ERROR", false);
    expect_error(call(*handler, envelope("SCORE_EVENT")), "DESERIALIZATION_ERROR", false);
}

TEST_F(ProtocolTest, NullOrMissingPayloadIsTreatedAsEmptyObject) {
    auto handler = make_handler();
    auto nullPayload = envelope("HEALTH_CHECK");
    nullPayload["payload"] = nullptr;
    EXPECT_EQ(call(*handler, nullPayload).at("type"), "HEALTH_CHECK_ACK");

    auto missingPayload = envelope("RELOAD_CONFIG");
    missingPayload.erase("payload");
    EXPECT_EQ(call(*handler, missingPayload).at("type"), "RELOAD_CONFIG_ACK");
}

TEST_F(ProtocolTest, FrameErrorEnvelope) {
    auto handler = make_handler();
    const auto response = json::parse(handler->frame_error("PAYLOAD_TOO_LARGE", "too big"));
    expect_envelope(response, "ERROR", "");
    expect_error(response, "PAYLOAD_TOO_LARGE", false);
}

TEST_F(ProtocolTest, InvalidUtf8InTextDoesNotBreakResponses) {
    auto handler = make_handler();
    auto request = envelope("SCORE_EVENT", market_event("e1", "Apple beats record"));
    std::string raw = request.dump();
    // Insert an invalid UTF-8 byte inside the headline string; the JSON parser rejects it cleanly.
    raw.insert(raw.find("Apple"), "\xFF");
    expect_error(json::parse(handler->handle(raw)), "DESERIALIZATION_ERROR", false);
}
