#include <gtest/gtest.h>

#include <array>
#include <cmath>
#include <limits>
#include <stdexcept>

#include "sentix/finbert_postprocess.hpp"

using sentix::finbert::kDefaultLabelOrder;
using sentix::finbert::Label;
using sentix::finbert::LabelOrder;

namespace {

void expect_invariants(const sentix::sentiment::SentimentScores& s) {
    EXPECT_NEAR(s.positive + s.negative + s.neutral, 1.0, 1e-12);
    EXPECT_NEAR(s.compositeScore, s.positive - s.negative, 1e-12);
    EXPECT_GE(s.compositeScore, -1.0);
    EXPECT_LE(s.compositeScore, 1.0);
    EXPECT_GE(s.positive, 0.0);
    EXPECT_GE(s.negative, 0.0);
    EXPECT_GE(s.neutral, 0.0);
}

}  // namespace

TEST(FinbertLabels, DefaultIsProsusOrder) {
    EXPECT_EQ(kDefaultLabelOrder, (LabelOrder{Label::Positive, Label::Negative, Label::Neutral}));
    EXPECT_EQ(sentix::finbert::parse_label_order(sentix::finbert::kDefaultLabelOrderText), kDefaultLabelOrder);
    EXPECT_EQ(sentix::finbert::format_label_order(kDefaultLabelOrder), "positive,negative,neutral");
    EXPECT_EQ(sentix::finbert::to_string(Label::Neutral), "neutral");
}

TEST(FinbertLabels, ParsesAnyPermutationCaseInsensitivelyWithWhitespace) {
    EXPECT_EQ(sentix::finbert::parse_label_order(" Neutral , POSITIVE,negative "),
              (LabelOrder{Label::Neutral, Label::Positive, Label::Negative}));
    EXPECT_EQ(sentix::finbert::parse_label_order("negative,neutral,positive"),
              (LabelOrder{Label::Negative, Label::Neutral, Label::Positive}));
    EXPECT_EQ(sentix::finbert::format_label_order(sentix::finbert::parse_label_order("negative,neutral,positive")),
              "negative,neutral,positive");
}

TEST(FinbertLabels, RejectsInvalidLists) {
    for (const char* invalid : {"", "positive", "positive,negative", "positive,negative,neutral,positive",
                                "positive,negative,neutral,", "positive,positive,neutral", "bullish,bearish,flat",
                                "positive;negative;neutral", ",positive,negative"}) {
        EXPECT_THROW(static_cast<void>(sentix::finbert::parse_label_order(invalid)), std::invalid_argument) << invalid;
    }
}

TEST(FinbertSoftmax, MatchesReferenceValues) {
    const auto p = sentix::finbert::stable_softmax({1.0F, 2.0F, 3.0F});
    const double e1 = std::exp(1.0);
    const double e2 = std::exp(2.0);
    const double e3 = std::exp(3.0);
    const double sum = e1 + e2 + e3;
    EXPECT_NEAR(p[0], e1 / sum, 1e-12);
    EXPECT_NEAR(p[1], e2 / sum, 1e-12);
    EXPECT_NEAR(p[2], e3 / sum, 1e-12);
    EXPECT_NEAR(p[0] + p[1] + p[2], 1.0, 1e-12);
}

TEST(FinbertSoftmax, EqualLogitsAreUniform) {
    const auto p = sentix::finbert::stable_softmax({0.0F, 0.0F, 0.0F});
    for (const double v : p) {
        EXPECT_NEAR(v, 1.0 / 3.0, 1e-12);
    }
}

TEST(FinbertSoftmax, IsShiftInvariantAndStableForHugeLogits) {
    const auto base = sentix::finbert::stable_softmax({0.5F, -1.0F, 2.0F});
    const auto shifted = sentix::finbert::stable_softmax({1000.5F, 999.0F, 1002.0F});
    for (std::size_t i = 0; i < base.size(); ++i) {
        EXPECT_NEAR(base[i], shifted[i], 1e-6);
    }

    const float big = std::numeric_limits<float>::max();
    const auto extreme = sentix::finbert::stable_softmax({big, -big, 0.0F});
    EXPECT_DOUBLE_EQ(extreme[0], 1.0);
    EXPECT_DOUBLE_EQ(extreme[1], 0.0);
    EXPECT_DOUBLE_EQ(extreme[2], 0.0);
    for (const double v : extreme) {
        EXPECT_TRUE(std::isfinite(v));
    }
}

TEST(FinbertSoftmax, RejectsNonFiniteLogits) {
    const float nan = std::numeric_limits<float>::quiet_NaN();
    const float inf = std::numeric_limits<float>::infinity();
    EXPECT_THROW(static_cast<void>(sentix::finbert::stable_softmax({nan, 0.0F, 0.0F})), std::domain_error);
    EXPECT_THROW(static_cast<void>(sentix::finbert::stable_softmax({0.0F, inf, 0.0F})), std::domain_error);
    EXPECT_THROW(static_cast<void>(sentix::finbert::stable_softmax({0.0F, 0.0F, -inf})), std::domain_error);
    EXPECT_THROW(static_cast<void>(sentix::finbert::scores_from_logits({nan, nan, nan}, kDefaultLabelOrder)), std::domain_error);
}

TEST(FinbertScores, DefaultMappingProducesCompositePositiveMinusNegative) {
    const std::array<float, 3> logits{3.0F, -1.0F, 0.5F};
    const auto probabilities = sentix::finbert::stable_softmax(logits);
    const auto s = sentix::finbert::scores_from_logits(logits, kDefaultLabelOrder);
    expect_invariants(s);
    EXPECT_NEAR(s.positive, probabilities[0], 1e-12);
    EXPECT_NEAR(s.negative, probabilities[1], 1e-12);
    EXPECT_NEAR(s.neutral, probabilities[2], 1e-12);
    EXPECT_NEAR(s.compositeScore, probabilities[0] - probabilities[1], 1e-12);
    EXPECT_GT(s.compositeScore, 0.55);
}

TEST(FinbertScores, CustomMappingRoutesIndicesToLabels) {
    // Index 0 = neutral, 1 = positive, 2 = negative.
    const LabelOrder order{Label::Neutral, Label::Positive, Label::Negative};
    const std::array<float, 3> logits{0.1F, -2.0F, 4.0F};
    const auto probabilities = sentix::finbert::stable_softmax(logits);
    const auto s = sentix::finbert::scores_from_logits(logits, order);
    expect_invariants(s);
    EXPECT_NEAR(s.neutral, probabilities[0], 1e-12);
    EXPECT_NEAR(s.positive, probabilities[1], 1e-12);
    EXPECT_NEAR(s.negative, probabilities[2], 1e-12);
    EXPECT_LT(s.compositeScore, -0.55);
}

TEST(FinbertScores, UniformLogitsAreNeutralComposite) {
    const auto s = sentix::finbert::scores_from_logits({7.0F, 7.0F, 7.0F}, kDefaultLabelOrder);
    expect_invariants(s);
    EXPECT_NEAR(s.compositeScore, 0.0, 1e-12);
}

TEST(FinbertScores, SaturatedLogitsStayWithinBounds) {
    const float big = std::numeric_limits<float>::max();
    const auto positive = sentix::finbert::scores_from_logits({big, -big, -big}, kDefaultLabelOrder);
    expect_invariants(positive);
    EXPECT_DOUBLE_EQ(positive.compositeScore, 1.0);
    const auto negative = sentix::finbert::scores_from_logits({-big, big, -big}, kDefaultLabelOrder);
    expect_invariants(negative);
    EXPECT_DOUBLE_EQ(negative.compositeScore, -1.0);
}
