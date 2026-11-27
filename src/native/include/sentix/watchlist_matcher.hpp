// SentiX native fast-path — watchlist & sector-ETF mapping (specs/002 §5 check 3).
#pragma once

#include <string>
#include <string_view>
#include <vector>

#include "sentix/config.hpp"

namespace sentix::matching {

struct MatchResult {
    std::vector<std::string> matchedTickers;     // sorted, unique, upper-case watchlist equities
    std::vector<std::string> matchedSectorEtfs;  // sorted, unique, upper-case sector ETFs
};

/// Matching rules:
///  * `sourceTickers`: case-insensitive (optional leading '$'), against watchlist tickers and sector ETF tickers.
///  * Text tickers / ETF tickers: whole-word, upper-case as written (avoids false hits on ordinary words),
///    or case-insensitive when written as a cashtag (`$aapl`).
///  * Aliases: whole-word, case-insensitive (multi-word aliases supported, e.g. "JP Morgan").
///  * Every matched watchlist ticker contributes the ETF of its sector.
class WatchlistMatcher {
public:
    explicit WatchlistMatcher(const config::UniverseConfig& config);

    [[nodiscard]] MatchResult match(std::string_view effectiveText, const std::vector<std::string>& sourceTickers) const;

private:
    struct Equity {
        std::string ticker;  // upper-case
        std::string sectorEtf;  // upper-case or empty
        std::vector<std::string> aliasesLower;
    };

    std::vector<Equity> equities_;
    std::vector<std::string> etfs_;  // upper-case
};

/// True when `needle` occurs in `haystack` delimited by non-alphanumeric ASCII characters (or string ends).
[[nodiscard]] bool contains_whole_word(std::string_view haystack, std::string_view needle) noexcept;

}  // namespace sentix::matching
