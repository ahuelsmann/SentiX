#include <gtest/gtest.h>

#include <cmath>

#include "sentix/sentiment_engine.hpp"

using sentix::sentiment::MockSentimentEngine;
using sentix::sentiment::SentimentScores;

namespace {

void expect_invariants(const SentimentScores& s) {
    EXPECT_NEAR(s.positive + s.negative + s.neutral, 1.0, 1e-12);
    EXPECT_NEAR(s.compositeScore, s.positive - s.negative, 1e-12);
    EXPECT_GE(s.compositeScore, -1.0);
    EXPECT_LE(s.compositeScore, 1.0);
    EXPECT_GE(s.positive, 0.0);
    EXPECT_GE(s.negative, 0.0);
    EXPECT_GE(s.neutral, 0.0);
}

}  // namespace

TEST(MockSentimentEngine, IsReadyAndNamed) {
    MockSentimentEngine engine;
    EXPECT_TRUE(engine.is_ready());
    EXPECT_EQ(engine.name(), "mock-lexicon-v1");
}

TEST(MockSentimentEngine, NoLexiconHitsIsFullyNeutral) {
    MockSentimentEngine engine;
    const auto s = engine.score("Apple schedules its annual shareholder meeting");
    EXPECT_DOUBLE_EQ(s.positive, 0.0);
    EXPECT_DOUBLE_EQ(s.negative, 0.0);
    EXPECT_DOUBLE_EQ(s.neutral, 1.0);
    EXPECT_DOUBLE_EQ(s.compositeScore, 0.0);
    expect_invariants(engine.score(""));
}

TEST(MockSentimentEngine, PositiveNewsScoresStronglyPositive) {
    MockSentimentEngine engine;
    const auto s = engine.score("Apple BEATS estimates on record demand, shares surge");
    expect_invariants(s);
    EXPECT_NEAR(s.positive, 3.0 / 3.5, 1e-12);
    EXPECT_GE(s.compositeScore, 0.55);
}

TEST(MockSentimentEngine, NegativeNewsScoresStronglyNegative) {
    MockSentimentEngine engine;
    const auto s = engine.score("Tesla misses guidance; recall and probe widen losses");
    expect_invariants(s);
    EXPECT_LE(s.compositeScore, -0.55);
}

TEST(MockSentimentEngine, MixedNewsIsWeak) {
    MockSentimentEngine engine;
    const auto s = engine.score("Revenue beat but margins missed");
    expect_invariants(s);
    EXPECT_NEAR(s.compositeScore, 0.0, 1e-12);
    EXPECT_LT(std::fabs(s.compositeScore), 0.55);
}

TEST(MockSentimentEngine, MatchesWholeWordsOnly) {
    MockSentimentEngine engine;
    EXPECT_DOUBLE_EQ(engine.score("beatles recordings").compositeScore, 0.0);
    EXPECT_GT(engine.score("beat.").compositeScore, 0.0);
}

TEST(MockSentimentEngine, IsDeterministic) {
    MockSentimentEngine engine;
    const auto a = engine.score("Nvidia upgraded, stock jumps");
    const auto b = engine.score("Nvidia upgraded, stock jumps");
    EXPECT_DOUBLE_EQ(a.positive, b.positive);
    EXPECT_DOUBLE_EQ(a.negative, b.negative);
    EXPECT_DOUBLE_EQ(a.neutral, b.neutral);
    EXPECT_DOUBLE_EQ(a.compositeScore, b.compositeScore);
}
