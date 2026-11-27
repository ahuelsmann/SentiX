#include "sentix/sentiment_engine.hpp"

#include <array>
#include <stdexcept>
#include <string>

#include "sentix/text_cleaner.hpp"

namespace sentix::sentiment {
namespace {

constexpr std::array<std::string_view, 40> kPositive{
    "beat",      "beats",     "record",     "surge",      "surges",     "surged",     "soar",       "soars",
    "soared",    "jump",      "jumps",      "jumped",     "rally",      "rallies",    "gain",       "gains",
    "growth",    "strong",    "stronger",   "upgrade",    "upgraded",   "upgrades",   "raise",      "raises",
    "raised",    "outperform", "bullish",   "approval",   "approved",   "exceeds",    "exceeded",   "tops",
    "profit",    "profitable", "boost",     "boosts",     "expands",    "wins",       "breakthrough", "buyback",
};

constexpr std::array<std::string_view, 40> kNegative{
    "miss",      "misses",    "missed",     "plunge",     "plunges",    "plunged",    "drop",       "drops",
    "dropped",   "fall",      "falls",      "fell",       "decline",    "declines",   "declined",   "weak",
    "weaker",    "downgrade", "downgraded", "downgrades", "cut",        "cuts",       "loss",       "losses",
    "lawsuit",   "probe",     "investigation", "recall",  "bearish",    "warning",    "warns",      "bankruptcy",
    "fraud",     "layoffs",   "slump",      "slumps",     "halt",       "halted",     "sanctions",  "default",
};

template <std::size_t N>
bool contains(const std::array<std::string_view, N>& lexicon, std::string_view word) noexcept {
    for (const auto entry : lexicon) {
        if (entry == word) {
            return true;
        }
    }
    return false;
}

constexpr bool is_word_char(char c) noexcept { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'); }

}  // namespace

SentimentScores MockSentimentEngine::score(std::string_view text) {
    const std::string lowered = text::to_lower_ascii(text);
    unsigned positiveHits = 0;
    unsigned negativeHits = 0;

    std::size_t i = 0;
    while (i < lowered.size()) {
        while (i < lowered.size() && !is_word_char(lowered[i])) {
            ++i;
        }
        const std::size_t start = i;
        while (i < lowered.size() && is_word_char(lowered[i])) {
            ++i;
        }
        if (i > start) {
            const std::string_view word(lowered.data() + start, i - start);
            positiveHits += contains(kPositive, word) ? 1U : 0U;
            negativeHits += contains(kNegative, word) ? 1U : 0U;
        }
    }

    SentimentScores scores;
    if (positiveHits + negativeHits == 0U) {
        return scores;  // fully neutral
    }
    const double denominator = static_cast<double>(positiveHits + negativeHits) + 0.5;
    scores.positive = static_cast<double>(positiveHits) / denominator;
    scores.negative = static_cast<double>(negativeHits) / denominator;
    scores.neutral = 1.0 - scores.positive - scores.negative;
    scores.compositeScore = scores.positive - scores.negative;
    return scores;
}

SentimentScores UnavailableSentimentEngine::score(std::string_view /*text*/) {
    throw std::logic_error("sentiment engine unavailable: " + reason_);
}

}  // namespace sentix::sentiment
