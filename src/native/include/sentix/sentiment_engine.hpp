// SentiX native fast-path — sentiment engine abstraction (specs/002 §5 check 4).
// Phase 1: deterministic lexicon mock. Phase 2: ONNX-FinBERT INT8 behind the same interface.
#pragma once

#include <string_view>

namespace sentix::sentiment {

struct SentimentScores {
    double positive{0.0};
    double negative{0.0};
    double neutral{1.0};
    double compositeScore{0.0};  // positive - negative, in [-1, +1]
};

class ISentimentEngine {
public:
    ISentimentEngine() = default;
    ISentimentEngine(const ISentimentEngine&) = delete;
    ISentimentEngine& operator=(const ISentimentEngine&) = delete;
    ISentimentEngine(ISentimentEngine&&) = delete;
    ISentimentEngine& operator=(ISentimentEngine&&) = delete;
    virtual ~ISentimentEngine() = default;

    /// Scores already-cleaned text. Must satisfy positive + negative + neutral == 1 and composite == positive - negative.
    [[nodiscard]] virtual SentimentScores score(std::string_view text) = 0;
    [[nodiscard]] virtual bool is_ready() const noexcept = 0;
    [[nodiscard]] virtual std::string_view name() const noexcept = 0;
};

/// Deterministic Phase-1 scorer: counts finance-lexicon hits (whole words, case-insensitive) and maps
/// p positive / n negative hits to positive = p / (p + n + 0.5), negative = n / (p + n + 0.5),
/// neutral = 1 - positive - negative. No hits -> fully neutral.
class MockSentimentEngine final : public ISentimentEngine {
public:
    [[nodiscard]] SentimentScores score(std::string_view text) override;
    [[nodiscard]] bool is_ready() const noexcept override { return true; }
    [[nodiscard]] std::string_view name() const noexcept override { return "mock-lexicon-v1"; }
};

}  // namespace sentix::sentiment
