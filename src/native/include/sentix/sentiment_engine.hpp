// SentiX native fast-path — sentiment engine abstraction (specs/002 §5 check 4).
// Phase 1: deterministic lexicon mock. Phase 2: ONNX-FinBERT INT8 behind the same interface
// (see onnx_finbert_engine.hpp and engine_factory.hpp, specs/002 §7).
#pragma once

#include <string>
#include <string_view>
#include <utility>

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

/// Fail-closed placeholder used when the configured engine could not be initialized (e.g. SENTIX_ENGINE_MODE=onnx
/// with a missing/corrupt model). Never ready, so the daemon reports DEGRADED and SCORE_EVENT -> MODEL_NOT_READY.
class UnavailableSentimentEngine final : public ISentimentEngine {
public:
    explicit UnavailableSentimentEngine(std::string reason) : reason_(std::move(reason)) {}

    /// Always throws std::logic_error: callers must check is_ready() first.
    [[nodiscard]] SentimentScores score(std::string_view text) override;
    [[nodiscard]] bool is_ready() const noexcept override { return false; }
    [[nodiscard]] std::string_view name() const noexcept override { return "unavailable"; }
    [[nodiscard]] const std::string& reason() const noexcept { return reason_; }

private:
    std::string reason_;
};

}  // namespace sentix::sentiment
