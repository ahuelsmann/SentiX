// Engine mode selection & fail-closed semantics (specs/002 §7). Independent of whether ONNX Runtime is compiled in:
// none of these cases may ever reach a successful ONNX session.
#include <gtest/gtest.h>

#include <filesystem>
#include <memory>
#include <stdexcept>
#include <string>
#include <utility>

#include <nlohmann/json.hpp>

#include "sentix/engine_factory.hpp"
#include "sentix/protocol.hpp"
#include "test_helpers.hpp"

using nlohmann::json;
using sentix::engine::EngineMode;
using sentix::engine::EngineSettings;
using sentix::testing::envelope;
using sentix::testing::market_event;

namespace {

constexpr const char* kVocab = "[PAD]\n[UNK]\n[CLS]\n[SEP]\napple\n";

EngineSettings settings_for(EngineMode mode, const std::filesystem::path& model, const std::filesystem::path& vocab) {
    EngineSettings settings;
    settings.mode = mode;
    settings.modelPath = model;
    settings.vocabPath = vocab;
    return settings;
}

void expect_unavailable(const sentix::engine::EngineSelection& selection) {
    ASSERT_NE(selection.engine, nullptr);
    EXPECT_FALSE(selection.ready());
    EXPECT_FALSE(selection.engine->is_ready());
    EXPECT_EQ(selection.engine->name(), "unavailable");
    EXPECT_NE(selection.description.find("UNAVAILABLE"), std::string::npos) << selection.description;
}

void expect_mock(const sentix::engine::EngineSelection& selection) {
    ASSERT_NE(selection.engine, nullptr);
    EXPECT_TRUE(selection.ready());
    EXPECT_EQ(selection.engine->name(), "mock-lexicon-v1");
}

}  // namespace

TEST(EngineFactory, MockModeAlwaysUsesMockEvenWithoutModelFiles) {
    sentix::testing::TempDir dir;
    expect_mock(sentix::engine::create_sentiment_engine(settings_for(EngineMode::Mock, dir.path() / "none.onnx", dir.path() / "none.txt")));
}

TEST(EngineFactory, AutoWithoutModelFilesFallsBackToMock) {
    sentix::testing::TempDir dir;
    const auto selection =
        sentix::engine::create_sentiment_engine(settings_for(EngineMode::Auto, dir.path() / "none.onnx", dir.path() / "none.txt"));
    expect_mock(selection);
    EXPECT_NE(selection.description.find("auto"), std::string::npos);
}

TEST(EngineFactory, AutoWithCorruptModelFailsClosedInsteadOfMock) {
    sentix::testing::TempDir dir;
    const auto model = dir.write("finbert-int8.onnx", "definitely not an ONNX protobuf");
    const auto vocab = dir.write("vocab.txt", kVocab);
    const auto selection = sentix::engine::create_sentiment_engine(settings_for(EngineMode::Auto, model, vocab));
    expect_unavailable(selection);
    EXPECT_NE(selection.engine->name(), "mock-lexicon-v1");
}

TEST(EngineFactory, AutoWithCorruptVocabFailsClosed) {
    sentix::testing::TempDir dir;
    const auto model = dir.write("finbert-int8.onnx", "not a model");
    const auto vocab = dir.write("vocab.txt", "no special tokens here\n");
    expect_unavailable(sentix::engine::create_sentiment_engine(settings_for(EngineMode::Auto, model, vocab)));
}

TEST(EngineFactory, AutoWithOnlyOneFilePresentFailsClosed) {
    sentix::testing::TempDir dir;
    const auto model = dir.write("finbert-int8.onnx", "x");
    const auto vocab = dir.write("vocab.txt", kVocab);

    const auto onlyModel = sentix::engine::create_sentiment_engine(settings_for(EngineMode::Auto, model, dir.path() / "none.txt"));
    expect_unavailable(onlyModel);
    EXPECT_NE(onlyModel.description.find("only the model"), std::string::npos) << onlyModel.description;

    const auto onlyVocab = sentix::engine::create_sentiment_engine(settings_for(EngineMode::Auto, dir.path() / "none.onnx", vocab));
    expect_unavailable(onlyVocab);
    EXPECT_NE(onlyVocab.description.find("only the vocab"), std::string::npos) << onlyVocab.description;
}

TEST(EngineFactory, AutoTreatsDanglingSymlinkAsPresent) {
    sentix::testing::TempDir dir;
    const auto vocab = dir.write("vocab.txt", kVocab);
    const auto link = dir.path() / "finbert-int8.onnx";
    std::filesystem::create_symlink(dir.path() / "gone.onnx", link);
    EXPECT_TRUE(sentix::engine::path_present(link));
    EXPECT_FALSE(sentix::engine::path_present(dir.path() / "gone.onnx"));
    expect_unavailable(sentix::engine::create_sentiment_engine(settings_for(EngineMode::Auto, link, vocab)));
}

TEST(EngineFactory, OnnxModeWithMissingFilesFailsClosed) {
    sentix::testing::TempDir dir;
    expect_unavailable(
        sentix::engine::create_sentiment_engine(settings_for(EngineMode::Onnx, dir.path() / "none.onnx", dir.path() / "none.txt")));

    const auto vocab = dir.write("vocab.txt", kVocab);
    expect_unavailable(sentix::engine::create_sentiment_engine(settings_for(EngineMode::Onnx, dir.path() / "none.onnx", vocab)));

    const auto model = dir.write("finbert-int8.onnx", "x");
    expect_unavailable(sentix::engine::create_sentiment_engine(settings_for(EngineMode::Onnx, model, dir.path() / "none.txt")));
}

TEST(EngineFactory, OnnxModeWithCorruptModelFailsClosed) {
    sentix::testing::TempDir dir;
    const auto model = dir.write("finbert-int8.onnx", std::string("\x08\x07\x12\xff\xff\xff", 6));
    const auto vocab = dir.write("vocab.txt", kVocab);
    expect_unavailable(sentix::engine::create_sentiment_engine(settings_for(EngineMode::Onnx, model, vocab)));
}

TEST(EngineFactory, InvalidSettingsFailClosedInEveryMode) {
    sentix::testing::TempDir dir;
    for (const auto mode : {EngineMode::Auto, EngineMode::Onnx, EngineMode::Mock}) {
        auto settings = settings_for(mode, dir.path() / "none.onnx", dir.path() / "none.txt");
        settings.errors.emplace_back("SENTIX_ENGINE_MODE: unsupported value 'turbo'");
        const auto selection = sentix::engine::create_sentiment_engine(settings);
        expect_unavailable(selection);
        EXPECT_NE(selection.description.find("turbo"), std::string::npos);
    }
}

TEST(EngineFactory, UnavailableEngineRefusesToScore) {
    sentix::sentiment::UnavailableSentimentEngine engine("no model");
    EXPECT_FALSE(engine.is_ready());
    EXPECT_EQ(engine.reason(), "no model");
    EXPECT_THROW(static_cast<void>(engine.score("Apple beats")), std::logic_error);
}

#if !SENTIX_HAS_ONNX
TEST(EngineFactory, BuildWithoutOnnxFailsClosedWhenModelIsPresent) {
    EXPECT_FALSE(sentix::engine::onnx_support_compiled());
    sentix::testing::TempDir dir;
    const auto model = dir.write("finbert-int8.onnx", "x");
    const auto vocab = dir.write("vocab.txt", kVocab);
    const auto selection = sentix::engine::create_sentiment_engine(settings_for(EngineMode::Auto, model, vocab));
    expect_unavailable(selection);
    EXPECT_NE(selection.description.find("SENTIX_ENABLE_ONNX=OFF"), std::string::npos);
}
#endif

// ---------------------------------------------------------------- RequestHandler integration (HEALTH / SCORE_EVENT)

class FailClosedHandlerTest : public ::testing::Test {
protected:
    sentix::testing::TempDir dir;
    sentix::testing::FakeClocks fake;
    std::filesystem::path configPath = dir.write("universe.json", sentix::testing::read_file(sentix::testing::universe_json_path()));

    static json call(sentix::protocol::RequestHandler& handler, const json& request) {
        return json::parse(handler.handle(request.dump()));
    }
};

TEST_F(FailClosedHandlerTest, OnnxModeWithMissingModelReportsDegradedAndModelNotReady) {
    auto selection =
        sentix::engine::create_sentiment_engine(settings_for(EngineMode::Onnx, dir.path() / "none.onnx", dir.path() / "none.txt"));
    ASSERT_FALSE(selection.ready());

    sentix::protocol::RequestHandler handler(configPath, std::move(selection.engine), fake.clocks());
    EXPECT_TRUE(handler.load_initial_config());  // universe is valid; the engine is what keeps us DEGRADED
    EXPECT_EQ(handler.status(), sentix::protocol::EngineStatus::Degraded);

    const auto health = call(handler, envelope("HEALTH_CHECK"));
    EXPECT_EQ(health.at("type"), "HEALTH_CHECK_ACK");
    EXPECT_EQ(health.at("payload").at("status"), "DEGRADED");
    EXPECT_EQ(health.at("payload").at("modelLoaded"), false);

    const auto score = call(handler, envelope("SCORE_EVENT", market_event("evt_1", "Apple beats on record AI demand", "", "",
                                                                          "ALPACA_BENZINGA", "Benzinga", json::array({"AAPL"}))));
    EXPECT_EQ(score.at("type"), "ERROR");
    EXPECT_EQ(score.at("payload").at("code"), "MODEL_NOT_READY");
    EXPECT_EQ(handler.processed_events_total(), 0U);

    // RELOAD_CONFIG must not resurrect a broken engine.
    const auto reload = call(handler, envelope("RELOAD_CONFIG"));
    EXPECT_EQ(reload.at("type"), "RELOAD_CONFIG_ACK");
    EXPECT_EQ(call(handler, envelope("HEALTH_CHECK")).at("payload").at("status"), "DEGRADED");
}

TEST_F(FailClosedHandlerTest, AutoModeWithoutModelServesMockReady) {
    auto selection =
        sentix::engine::create_sentiment_engine(settings_for(EngineMode::Auto, dir.path() / "none.onnx", dir.path() / "none.txt"));
    sentix::protocol::RequestHandler handler(configPath, std::move(selection.engine), fake.clocks());
    ASSERT_TRUE(handler.load_initial_config());

    const auto health = call(handler, envelope("HEALTH_CHECK"));
    EXPECT_EQ(health.at("payload").at("status"), "READY");
    EXPECT_EQ(health.at("payload").at("modelLoaded"), true);
}

TEST_F(FailClosedHandlerTest, AutoModeWithCorruptModelReportsDegraded) {
    const auto model = dir.write("finbert-int8.onnx", "garbage");
    const auto vocab = dir.write("vocab.txt", kVocab);
    auto selection = sentix::engine::create_sentiment_engine(settings_for(EngineMode::Auto, model, vocab));
    sentix::protocol::RequestHandler handler(configPath, std::move(selection.engine), fake.clocks());
    EXPECT_TRUE(handler.load_initial_config());

    const auto health = call(handler, envelope("HEALTH_CHECK"));
    EXPECT_EQ(health.at("payload").at("status"), "DEGRADED");
    EXPECT_EQ(health.at("payload").at("modelLoaded"), false);
    EXPECT_EQ(call(handler, envelope("SCORE_EVENT", market_event("evt_2", "Apple beats"))).at("payload").at("code"), "MODEL_NOT_READY");
}
