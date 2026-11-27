#include "sentix/watchlist_matcher.hpp"

#include <set>

#include "sentix/text_cleaner.hpp"

namespace sentix::matching {
namespace {

constexpr bool is_alnum(char c) noexcept {
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
}

bool contains_cashtag_ci(std::string_view lowerText, std::string_view lowerTicker) {
    const std::string tag = "$" + std::string(lowerTicker);
    return contains_whole_word(lowerText, tag);
}

}  // namespace

bool contains_whole_word(std::string_view haystack, std::string_view needle) noexcept {
    if (needle.empty() || needle.size() > haystack.size()) {
        return false;
    }
    std::size_t pos = haystack.find(needle);
    while (pos != std::string_view::npos) {
        const bool leftOk = pos == 0 || !is_alnum(haystack[pos - 1]);
        const std::size_t end = pos + needle.size();
        const bool rightOk = end == haystack.size() || !is_alnum(haystack[end]);
        if (leftOk && rightOk) {
            return true;
        }
        pos = haystack.find(needle, pos + 1);
    }
    return false;
}

WatchlistMatcher::WatchlistMatcher(const config::UniverseConfig& config) {
    std::set<std::string> etfs;
    for (const auto& [sector, etf] : config.sectorEtfs) {
        etfs.insert(text::to_upper_ascii(etf));
    }
    etfs_.assign(etfs.begin(), etfs.end());

    for (const auto& entry : config.watchlist) {
        Equity equity;
        equity.ticker = text::to_upper_ascii(entry.ticker);
        if (const auto it = config.sectorEtfs.find(entry.sector); it != config.sectorEtfs.end()) {
            equity.sectorEtf = text::to_upper_ascii(it->second);
        }
        for (const auto& alias : entry.aliases) {
            equity.aliasesLower.push_back(text::to_lower_ascii(alias));
        }
        equities_.push_back(std::move(equity));
    }
}

MatchResult WatchlistMatcher::match(std::string_view effectiveText, const std::vector<std::string>& sourceTickers) const {
    std::set<std::string> tickers;
    std::set<std::string> sectorEtfs;

    std::set<std::string> normalizedSource;
    for (const auto& raw : sourceTickers) {
        std::string_view t = text::trim(raw);
        if (!t.empty() && t.front() == '$') {
            t.remove_prefix(1);
        }
        if (!t.empty()) {
            normalizedSource.insert(text::to_upper_ascii(t));
        }
    }

    const std::string lowerText = text::to_lower_ascii(effectiveText);

    for (const auto& equity : equities_) {
        bool hit = normalizedSource.contains(equity.ticker) || contains_whole_word(effectiveText, equity.ticker) ||
                   contains_cashtag_ci(lowerText, text::to_lower_ascii(equity.ticker));
        for (std::size_t i = 0; !hit && i < equity.aliasesLower.size(); ++i) {
            hit = contains_whole_word(lowerText, equity.aliasesLower[i]);
        }
        if (hit) {
            tickers.insert(equity.ticker);
            if (!equity.sectorEtf.empty()) {
                sectorEtfs.insert(equity.sectorEtf);
            }
        }
    }

    for (const auto& etf : etfs_) {
        if (normalizedSource.contains(etf) || contains_whole_word(effectiveText, etf) ||
            contains_cashtag_ci(lowerText, text::to_lower_ascii(etf))) {
            sectorEtfs.insert(etf);
        }
    }

    return MatchResult{{tickers.begin(), tickers.end()}, {sectorEtfs.begin(), sectorEtfs.end()}};
}

}  // namespace sentix::matching
