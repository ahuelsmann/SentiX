// Real ONNX Runtime end-to-end tests (compiled only with SENTIX_ENABLE_ONNX=ON). Tiny FinBERT-shaped graphs are
// generated in memory (onnx_test_model.hpp), written to a temp dir and run through Ort::Session.
#include <gtest/gtest.h>

#include <array>
#include <filesystem>
#include <memory>
#include <stdexcept>
#include <string>
#include <utility>

#include <nlohmann/json.hpp>

#include "onnx_test_model.hpp"
#include "sentix/engine_factory.hpp"
#include "sentix/finbert_postprocess.hpp"
#include "sentix/onnx_finbert_engine.hpp"
#include "sentix/protocol.hpp"
#include "test_helpers.hpp"

using nlohmann::json;
using sentix::finbert::Label;
using sentix::finbert::LabelOrder;
using sentix::sentiment::OnnxFinbertOptions;
using sentix::sentiment::OnnxFinbertSentimentEngine;
using sentix::testing::onnx::TinyFinbertSpec;
using sentix::testing::onnx::tiny_finbert_model;

namespace {

// [PAD]=0 [UNK]=1 [CLS]=2 [SEP]=3 apple=4 beat=5 ##s=6 record=7 demand=8
constexpr const char* kVocab = "[PAD]\n[UNK]\n[CLS]\n[SEP]\napple\nbeat\n##s\nrecord\ndemand\n";

class OnnxEngineTest : public ::testing::Test {
protected:
    sentix::testing::TempDir dir;
    std::filesystem::path vocab = dir.write("vocab.txt", kVocab);

    std::filesystem::path write_model(const TinyFinbertSpec& spec, const std::string& name = "finbert-int8.onnx") const {
        return dir.write(name, tiny_finbert_model(spec));
    }

    OnnxFinbertOptions options_for(const std::filesystem::path& model) const {
        OnnxFinbertOptions options;
        options.modelPath = model;
        options.vocabPath = vocab;
        return options;
    }

    /// Scores the tiny graph yields for `tokenCount` tokens: logits = tokenCount * weights (float math, as in ORT).
    static sentix::sentiment::SentimentScores expected(float tokenCount, const TinyFinbertSpec& spec, const LabelOrder& order) {
        const std::array<float, 3> logits{tokenCount * spec.weights[0], tokenCount * spec.weights[1], tokenCount * spec.weights[2]};
        return sentix::finbert::scores_from_logits(logits, order);
    }

    static void expect_scores_near(const sentix::sentiment::SentimentScores& actual, const sentix::sentiment::SentimentScores& want) {
        EXPECT_NEAR(actual.positive, want.positive, 1e-6);
        EXPECT_NEAR(actual.negative, want.negative, 1e-6);
        EXPECT_NEAR(actual.neutral, want.neutral, 1e-6);
        EXPECT_NEAR(actual.compositeScore, want.compositeScore, 1e-6);
        EXPECT_NEAR(actual.positive + actual.negative + actual.neutral, 1.0, 1e-12);
        EXPECT_NEAR(actual.compositeScore, actual.positive - actual.negative, 1e-12);
    }
};

}  // namespace

TEST(OnnxRuntimeLoader, LoadsBundledRuntime) {
    EXPECT_TRUE(sentix::engine::onnx_support_compiled());
    const auto failure = sentix::sentiment::ensure_onnxruntime_loaded();
    EXPECT_FALSE(failure.has_value()) << failure.value_or("");
}

TEST_F(OnnxEngineTest, RunsRealInferenceEndToEnd) {
    const TinyFinbertSpec spec;
    OnnxFinbertSentimentEngine engine(options_for(write_model(spec)));
    EXPECT_TRUE(engine.is_ready());
    EXPECT_EQ(engine.name(), "onnx-finbert-int8");
    EXPECT_FALSE(engine.uses_token_type_ids());

    // [CLS] apple beat ##s [SEP] -> 5 tokens -> logits {2.0, -1.0, 0.5}.
    const auto scores = engine.score("Apple beats");
    expect_scores_near(scores, expected(5.0F, spec, sentix::finbert::kDefaultLabelOrder));
    EXPECT_GT(scores.compositeScore, 0.55);

    // [CLS] [SEP] only -> 2 tokens. Unknown words still count as one [UNK] token each.
    expect_scores_near(engine.score(""), expected(2.0F, spec, sentix::finbert::kDefaultLabelOrder));
    expect_scores_near(engine.score("zzz qqq"), expected(4.0F, spec, sentix::finbert::kDefaultLabelOrder));
}

TEST_F(OnnxEngineTest, IsDeterministicAcrossCalls) {
    OnnxFinbertSentimentEngine engine(options_for(write_model({})));
    const auto a = engine.score("Apple beats record demand");
    const auto b = engine.score("Apple beats record demand");
    EXPECT_DOUBLE_EQ(a.positive, b.positive);
    EXPECT_DOUBLE_EQ(a.negative, b.negative);
    EXPECT_DOUBLE_EQ(a.neutral, b.neutral);
    EXPECT_DOUBLE_EQ(a.compositeScore, b.compositeScore);
}

TEST_F(OnnxEngineTest, AppliesConfiguredLabelOrder) {
    TinyFinbertSpec spec;
    spec.weights = {0.1F, 0.4F, -0.2F};  // index 0 = neutral, 1 = positive, 2 = negative
    auto options = options_for(write_model(spec));
    options.labels = LabelOrder{Label::Neutral, Label::Positive, Label::Negative};
    OnnxFinbertSentimentEngine engine(options);

    const auto scores = engine.score("Apple beats");
    expect_scores_near(scores, expected(5.0F, spec, options.labels));
    EXPECT_GT(scores.positive, scores.negative);
    EXPECT_GT(scores.positive, scores.neutral);
}

TEST_F(OnnxEngineTest, FeedsTokenTypeIdsWhenGraphDeclaresThem) {
    TinyFinbertSpec spec;
    spec.withTokenTypeIds = true;
    OnnxFinbertSentimentEngine engine(options_for(write_model(spec)));
    EXPECT_TRUE(engine.uses_token_type_ids());
    // token_type_ids are all zero, so attention_mask + token_type_ids still sums to the token count.
    expect_scores_near(engine.score("Apple beats"), expected(5.0F, spec, sentix::finbert::kDefaultLabelOrder));
}

TEST_F(OnnxEngineTest, AcceptsDynamicClassDimensionWithThreeLogits) {
    TinyFinbertSpec spec;
    spec.dynamicClassDim = true;
    OnnxFinbertSentimentEngine engine(options_for(write_model(spec)));
    expect_scores_near(engine.score("Apple"), expected(3.0F, spec, sentix::finbert::kDefaultLabelOrder));
}

TEST_F(OnnxEngineTest, TruncatesToMaxSequenceLength) {
    const TinyFinbertSpec spec;
    auto options = options_for(write_model(spec));
    options.maxSequenceLength = 4;
    OnnxFinbertSentimentEngine engine(options);
    expect_scores_near(engine.score("Apple beats record demand apple apple"), expected(4.0F, spec, sentix::finbert::kDefaultLabelOrder));

    std::string longText;
    for (int i = 0; i < 1000; ++i) {
        longText += "apple ";
    }
    OnnxFinbertSentimentEngine full(options_for(write_model(spec, "full.onnx")));
    expect_scores_near(full.score(longText), expected(512.0F, spec, sentix::finbert::kDefaultLabelOrder));
}

TEST_F(OnnxEngineTest, HonoursIntraOpThreads) {
    auto options = options_for(write_model({}));
    options.intraOpThreads = 2;
    OnnxFinbertSentimentEngine engine(options);
    EXPECT_TRUE(engine.is_ready());
}

TEST_F(OnnxEngineTest, RejectsInvalidModelsAndVocab) {
    // Corrupt protobuf.
    EXPECT_ANY_THROW(OnnxFinbertSentimentEngine(options_for(dir.write("corrupt.onnx", "this is not an onnx model"))));
    // Missing model / missing vocab / directory as model.
    EXPECT_ANY_THROW(OnnxFinbertSentimentEngine(options_for(dir.path() / "missing.onnx")));
    EXPECT_ANY_THROW(OnnxFinbertSentimentEngine(options_for(dir.path())));
    {
        auto options = options_for(write_model({}));
        options.vocabPath = dir.path() / "missing.txt";
        EXPECT_ANY_THROW(OnnxFinbertSentimentEngine{options});
    }
    {
        auto options = options_for(write_model({}));
        options.vocabPath = dir.write("bad_vocab.txt", "apple\nbeat\n");
        EXPECT_ANY_THROW(OnnxFinbertSentimentEngine{options});
    }
    // Unsupported graph input name (no input_ids).
    {
        TinyFinbertSpec spec;
        spec.firstInputName = "pixel_values";
        EXPECT_ANY_THROW(OnnxFinbertSentimentEngine(options_for(write_model(spec, "wrong_input.onnx"))));
    }
    // input_ids with the wrong element type.
    {
        TinyFinbertSpec spec;
        spec.firstInputType = sentix::testing::onnx::kFloat;
        EXPECT_ANY_THROW(OnnxFinbertSentimentEngine(options_for(write_model(spec, "float_ids.onnx"))));
    }
    // Statically declared 2-class output.
    {
        TinyFinbertSpec spec;
        spec.weights = {0.4F, -0.2F};
        EXPECT_ANY_THROW(OnnxFinbertSentimentEngine(options_for(write_model(spec, "two_classes.onnx"))));
    }
    // Dynamic class dim that really yields 2 logits: caught by the warm-up inference.
    {
        TinyFinbertSpec spec;
        spec.weights = {0.4F, -0.2F};
        spec.dynamicClassDim = true;
        EXPECT_ANY_THROW(OnnxFinbertSentimentEngine(options_for(write_model(spec, "two_dynamic.onnx"))));
    }
    // Out-of-range options.
    {
        auto options = options_for(write_model({}));
        options.maxSequenceLength = 513;
        EXPECT_ANY_THROW(OnnxFinbertSentimentEngine{options});
        options.maxSequenceLength = 1;
        EXPECT_ANY_THROW(OnnxFinbertSentimentEngine{options});
        options.maxSequenceLength = 512;
        options.intraOpThreads = 0;
        EXPECT_ANY_THROW(OnnxFinbertSentimentEngine{options});
    }
}

// ---------------------------------------------------------------- factory + RequestHandler with a real ONNX graph

TEST_F(OnnxEngineTest, AutoModeWithValidFilesSelectsOnnxEngine) {
    sentix::engine::EngineSettings settings;
    settings.mode = sentix::engine::EngineMode::Auto;
    settings.modelPath = write_model({});
    settings.vocabPath = vocab;
    const auto selection = sentix::engine::create_sentiment_engine(settings);
    ASSERT_TRUE(selection.ready()) << selection.description;
    EXPECT_EQ(selection.engine->name(), "onnx-finbert-int8");
    EXPECT_NE(selection.description.find("labels positive,negative,neutral"), std::string::npos) << selection.description;
}

TEST_F(OnnxEngineTest, OnnxModeWithValidFilesSelectsOnnxEngine) {
    sentix::engine::EngineSettings settings;
    settings.mode = sentix::engine::EngineMode::Onnx;
    settings.modelPath = write_model({});
    settings.vocabPath = vocab;
    const auto selection = sentix::engine::create_sentiment_engine(settings);
    ASSERT_TRUE(selection.ready()) << selection.description;
    EXPECT_EQ(selection.engine->name(), "onnx-finbert-int8");
}

TEST_F(OnnxEngineTest, AutoModeWithStructurallyInvalidGraphFailsClosed) {
    TinyFinbertSpec spec;
    spec.firstInputName = "pixel_values";
    sentix::engine::EngineSettings settings;
    settings.mode = sentix::engine::EngineMode::Auto;
    settings.modelPath = write_model(spec);
    settings.vocabPath = vocab;
    const auto selection = sentix::engine::create_sentiment_engine(settings);
    EXPECT_FALSE(selection.ready());
    EXPECT_EQ(selection.engine->name(), "unavailable");
    EXPECT_NE(selection.description.find("pixel_values"), std::string::npos) << selection.description;
}

TEST_F(OnnxEngineTest, RequestHandlerServesOnnxScoresAndReportsReady) {
    sentix::testing::FakeClocks fake;
    const auto configPath = dir.write("universe.json", sentix::testing::read_file(sentix::testing::universe_json_path()));

    sentix::engine::EngineSettings settings;
    settings.mode = sentix::engine::EngineMode::Onnx;
    settings.modelPath = write_model({});
    settings.vocabPath = vocab;
    auto selection = sentix::engine::create_sentiment_engine(settings);
    ASSERT_TRUE(selection.ready()) << selection.description;

    sentix::protocol::RequestHandler handler(configPath, std::move(selection.engine), fake.clocks());
    ASSERT_TRUE(handler.load_initial_config());

    const auto health = json::parse(handler.handle(sentix::testing::envelope("HEALTH_CHECK").dump()));
    EXPECT_EQ(health.at("payload").at("status"), "READY");
    EXPECT_EQ(health.at("payload").at("modelLoaded"), true);

    const auto response = json::parse(handler.handle(
        sentix::testing::envelope("SCORE_EVENT", sentix::testing::market_event("evt_onnx", "Apple beats record demand", "", "",
                                                                                "ALPACA_BENZINGA", "Benzinga", json::array({"AAPL"})))
            .dump()));
    ASSERT_EQ(response.at("type"), "SCORE_EVENT_RESULT") << response.dump();
    const auto& finbert = response.at("payload").at("finbert");
    const double positive = finbert.at("positive").get<double>();
    const double negative = finbert.at("negative").get<double>();
    const double neutral = finbert.at("neutral").get<double>();
    EXPECT_NEAR(positive + negative + neutral, 1.0, 1e-9);
    EXPECT_NEAR(finbert.at("compositeScore").get<double>(), positive - negative, 1e-9);
    EXPECT_GT(finbert.at("compositeScore").get<double>(), 0.55);
    EXPECT_EQ(response.at("payload").at("passToDecisionSwarm"), true);
    EXPECT_EQ(handler.processed_events_total(), 1U);
}
