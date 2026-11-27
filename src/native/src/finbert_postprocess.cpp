#include "sentix/finbert_postprocess.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <stdexcept>

#include "sentix/text_cleaner.hpp"

namespace sentix::finbert {

std::string_view to_string(Label label) noexcept {
    switch (label) {
        case Label::Positive:
            return "positive";
        case Label::Negative:
            return "negative";
        case Label::Neutral:
        default:
            return "neutral";
    }
}

LabelOrder parse_label_order(std::string_view csv) {
    LabelOrder order = kDefaultLabelOrder;
    std::array<bool, 3> seen{false, false, false};
    std::size_t count = 0;

    std::size_t start = 0;
    while (true) {
        const std::size_t comma = csv.find(',', start);
        const std::string_view rawEntry = csv.substr(start, comma == std::string_view::npos ? std::string_view::npos : comma - start);
        const std::string entry = text::to_lower_ascii(text::trim(rawEntry));

        Label label{};
        if (entry == "positive") {
            label = Label::Positive;
        } else if (entry == "negative") {
            label = Label::Negative;
        } else if (entry == "neutral") {
            label = Label::Neutral;
        } else {
            throw std::invalid_argument("SENTIX_FINBERT_LABELS: unknown label '" + entry +
                                        "' (expected a permutation of positive,negative,neutral)");
        }
        if (count >= order.size()) {
            throw std::invalid_argument("SENTIX_FINBERT_LABELS: expected exactly 3 labels");
        }
        const auto slot = static_cast<std::size_t>(label);
        if (seen[slot]) {
            throw std::invalid_argument("SENTIX_FINBERT_LABELS: duplicate label '" + entry + "'");
        }
        seen[slot] = true;
        order[count] = label;
        ++count;

        if (comma == std::string_view::npos) {
            break;
        }
        start = comma + 1;
    }

    if (count != order.size()) {
        throw std::invalid_argument("SENTIX_FINBERT_LABELS: expected exactly 3 labels");
    }
    return order;
}

std::string format_label_order(const LabelOrder& order) {
    std::string out;
    for (std::size_t i = 0; i < order.size(); ++i) {
        if (i > 0) {
            out += ',';
        }
        out += to_string(order[i]);
    }
    return out;
}

std::array<double, 3> stable_softmax(const std::array<float, 3>& logits) {
    for (const float logit : logits) {
        if (!std::isfinite(logit)) {
            throw std::domain_error("FinBERT logits must be finite");
        }
    }
    const double maxLogit = static_cast<double>(*std::max_element(logits.begin(), logits.end()));

    std::array<double, 3> probabilities{};
    double sum = 0.0;
    for (std::size_t i = 0; i < logits.size(); ++i) {
        probabilities[i] = std::exp(static_cast<double>(logits[i]) - maxLogit);  // in (0, 1], max term == 1
        sum += probabilities[i];
    }
    for (double& p : probabilities) {
        p /= sum;  // sum >= 1, never zero
    }
    return probabilities;
}

sentiment::SentimentScores scores_from_logits(const std::array<float, 3>& logits, const LabelOrder& order) {
    const auto probabilities = stable_softmax(logits);

    double positive = 0.0;
    double negative = 0.0;
    for (std::size_t i = 0; i < order.size(); ++i) {
        if (order[i] == Label::Positive) {
            positive = probabilities[i];
        } else if (order[i] == Label::Negative) {
            negative = probabilities[i];
        }
    }

    sentiment::SentimentScores scores;
    scores.positive = positive;
    scores.negative = negative;
    scores.neutral = std::clamp(1.0 - positive - negative, 0.0, 1.0);
    scores.compositeScore = std::clamp(positive - negative, -1.0, 1.0);
    return scores;
}

}  // namespace sentix::finbert
