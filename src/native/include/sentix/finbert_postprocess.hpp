// SentiX native fast-path — FinBERT logit post-processing (specs/002 §7).
// Numerically stable softmax + SENTIX_FINBERT_LABELS index mapping. Pure, deterministic, ONNX-independent.
#pragma once

#include <array>
#include <cstdint>
#include <string>
#include <string_view>

#include "sentix/sentiment_engine.hpp"

namespace sentix::finbert {

enum class Label : std::uint8_t { Positive, Negative, Neutral };

/// Logit index -> label (e.g. ProsusAI/finbert: index 0 = positive, 1 = negative, 2 = neutral).
using LabelOrder = std::array<Label, 3>;

inline constexpr LabelOrder kDefaultLabelOrder{Label::Positive, Label::Negative, Label::Neutral};
inline constexpr std::string_view kDefaultLabelOrderText = "positive,negative,neutral";

[[nodiscard]] std::string_view to_string(Label label) noexcept;

/// Parses a comma-separated permutation of "positive", "negative", "neutral" (case-insensitive, ASCII whitespace
/// around entries ignored). Throws std::invalid_argument on wrong count, unknown or duplicate labels.
[[nodiscard]] LabelOrder parse_label_order(std::string_view csv);

/// Inverse of parse_label_order, e.g. "positive,negative,neutral".
[[nodiscard]] std::string format_label_order(const LabelOrder& order);

/// Max-shifted softmax over 3 logits. Throws std::domain_error if any logit is NaN or infinite.
[[nodiscard]] std::array<double, 3> stable_softmax(const std::array<float, 3>& logits);

/// softmax -> label mapping -> SentimentScores with positive + negative + neutral == 1 and
/// compositeScore = positive - negative in [-1, +1]. Throws std::domain_error on non-finite logits.
[[nodiscard]] sentiment::SentimentScores scores_from_logits(const std::array<float, 3>& logits, const LabelOrder& order);

}  // namespace sentix::finbert
