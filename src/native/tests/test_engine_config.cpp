#include <gtest/gtest.h>

#include <map>
#include <optional>
#include <stdexcept>
#include <string>

#include "sentix/engine_config.hpp"

using sentix::engine::EngineMode;
using sentix::engine::EngineSettings;
using sentix::finbert::Label;
using sentix::finbert::LabelOrder;

namespace {

EngineSettings load(const std::map<std::string, std::string>& env) {
    return sentix::engine::load_engine_settings([&env](std::string_view name) -> std::optional<std::string> {
        const auto it = env.find(std::string(name));
        if (it == env.end()) {
            return std::nullopt;
        }
        return it->second;
    });
}

}  // namespace

TEST(EngineConfig, DefaultsMatchSpec) {
    const auto settings = load({});
    EXPECT_TRUE(settings.valid());
    EXPECT_EQ(settings.mode, EngineMode::Auto);
    EXPECT_EQ(settings.modelPath, std::filesystem::path("/models/finbert-int8.onnx"));
    EXPECT_EQ(settings.vocabPath, std::filesystem::path("/models/vocab.txt"));
    EXPECT_EQ(settings.labels, (LabelOrder{Label::Positive, Label::Negative, Label::Neutral}));
    EXPECT_EQ(settings.intraOpThreads, 1);
}

TEST(EngineConfig, NullLookupYieldsDefaults) {
    const auto settings = sentix::engine::load_engine_settings(nullptr);
    EXPECT_TRUE(settings.valid());
    EXPECT_EQ(settings.mode, EngineMode::Auto);
}

TEST(EngineConfig, ReadsAllVariables) {
    const auto settings = load({
        {"SENTIX_ENGINE_MODE", "onnx"},
        {"SENTIX_MODEL_PATH", "/opt/m/model.onnx"},
        {"SENTIX_VOCAB_PATH", "/opt/m/vocab.txt"},
        {"SENTIX_FINBERT_LABELS", "neutral,positive,negative"},
        {"SENTIX_ONNX_INTRA_OP_THREADS", "2"},
    });
    EXPECT_TRUE(settings.valid());
    EXPECT_EQ(settings.mode, EngineMode::Onnx);
    EXPECT_EQ(settings.modelPath, std::filesystem::path("/opt/m/model.onnx"));
    EXPECT_EQ(settings.vocabPath, std::filesystem::path("/opt/m/vocab.txt"));
    EXPECT_EQ(settings.labels, (LabelOrder{Label::Neutral, Label::Positive, Label::Negative}));
    EXPECT_EQ(settings.intraOpThreads, 2);
}

TEST(EngineConfig, EmptyValuesAreTreatedAsUnset) {
    const auto settings = load({
        {"SENTIX_ENGINE_MODE", ""},
        {"SENTIX_MODEL_PATH", ""},
        {"SENTIX_VOCAB_PATH", ""},
        {"SENTIX_FINBERT_LABELS", ""},
        {"SENTIX_ONNX_INTRA_OP_THREADS", ""},
    });
    EXPECT_TRUE(settings.valid());
    EXPECT_EQ(settings.mode, EngineMode::Auto);
    EXPECT_EQ(settings.modelPath, std::filesystem::path("/models/finbert-int8.onnx"));
    EXPECT_EQ(settings.intraOpThreads, 1);
}

TEST(EngineConfig, ParsesModesCaseInsensitively) {
    EXPECT_EQ(sentix::engine::parse_engine_mode("auto"), EngineMode::Auto);
    EXPECT_EQ(sentix::engine::parse_engine_mode(" ONNX "), EngineMode::Onnx);
    EXPECT_EQ(sentix::engine::parse_engine_mode("Mock"), EngineMode::Mock);
    EXPECT_EQ(sentix::engine::to_string(EngineMode::Auto), "auto");
    EXPECT_EQ(sentix::engine::to_string(EngineMode::Onnx), "onnx");
    EXPECT_EQ(sentix::engine::to_string(EngineMode::Mock), "mock");
    EXPECT_THROW(static_cast<void>(sentix::engine::parse_engine_mode("gpu")), std::invalid_argument);
    EXPECT_THROW(static_cast<void>(sentix::engine::parse_engine_mode("")), std::invalid_argument);
}

TEST(EngineConfig, ParsesThreadCounts) {
    EXPECT_EQ(sentix::engine::parse_intra_op_threads("1"), 1);
    EXPECT_EQ(sentix::engine::parse_intra_op_threads(" 4 "), 4);
    EXPECT_EQ(sentix::engine::parse_intra_op_threads("64"), 64);
    for (const char* invalid : {"0", "-1", "65", "two", "2x", "", "1.5", "99999999999999999999"}) {
        EXPECT_THROW(static_cast<void>(sentix::engine::parse_intra_op_threads(invalid)), std::invalid_argument) << invalid;
    }
}

TEST(EngineConfig, InvalidValuesAreCollectedNotThrown) {
    const auto settings = load({
        {"SENTIX_ENGINE_MODE", "turbo"},
        {"SENTIX_FINBERT_LABELS", "positive,negative"},
        {"SENTIX_ONNX_INTRA_OP_THREADS", "0"},
    });
    EXPECT_FALSE(settings.valid());
    ASSERT_EQ(settings.errors.size(), 3U);
    EXPECT_NE(settings.errors[0].find("SENTIX_ENGINE_MODE"), std::string::npos);
    EXPECT_NE(settings.errors[1].find("SENTIX_FINBERT_LABELS"), std::string::npos);
    EXPECT_NE(settings.errors[2].find("SENTIX_ONNX_INTRA_OP_THREADS"), std::string::npos);
}
