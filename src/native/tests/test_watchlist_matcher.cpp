#include <gtest/gtest.h>

#include "sentix/config.hpp"
#include "sentix/watchlist_matcher.hpp"
#include "test_helpers.hpp"

using sentix::matching::contains_whole_word;
using sentix::matching::WatchlistMatcher;
using Strings = std::vector<std::string>;

class WatchlistMatcherTest : public ::testing::Test {
protected:
    sentix::config::UniverseConfig cfg = sentix::config::load_universe_config(sentix::testing::universe_json_path());
    WatchlistMatcher matcher{cfg};
};

TEST(WholeWord, RespectsAlphanumericBoundaries) {
    EXPECT_TRUE(contains_whole_word("AMD rallies", "AMD"));
    EXPECT_TRUE(contains_whole_word("shares of AMD.", "AMD"));
    EXPECT_TRUE(contains_whole_word("($AMD)", "AMD"));
    EXPECT_FALSE(contains_whole_word("AMDOCS rallies", "AMD"));
    EXPECT_FALSE(contains_whole_word("XAMD", "AMD"));
    EXPECT_TRUE(contains_whole_word("XAMD then AMD", "AMD"));
    EXPECT_FALSE(contains_whole_word("AM", "AMD"));
    EXPECT_FALSE(contains_whole_word("anything", ""));
}

TEST_F(WatchlistMatcherTest, SourceTickersAreCaseInsensitiveAndMapToSectorEtf) {
    const auto r = matcher.match("No names in this text", {" aapl ", "$nvda", "GME", ""});
    EXPECT_EQ(r.matchedTickers, (Strings{"AAPL", "NVDA"}));
    EXPECT_EQ(r.matchedSectorEtfs, (Strings{"SMH", "XLK"}));
}

TEST_F(WatchlistMatcherTest, MatchesTickersAliasesAndMultiWordAliasesInText) {
    const auto r = matcher.match("Apple and JP Morgan rally while Lockheed Martin and XOM slip", {});
    EXPECT_EQ(r.matchedTickers, (Strings{"AAPL", "JPM", "LMT", "XOM"}));
    EXPECT_EQ(r.matchedSectorEtfs, (Strings{"ITA", "XLE", "XLF", "XLK"}));
}

TEST_F(WatchlistMatcherTest, AliasesAreCaseInsensitiveWholeWords) {
    EXPECT_EQ(matcher.match("TESLA deliveries beat", {}).matchedTickers, (Strings{"TSLA"}));
    EXPECT_EQ(matcher.match("exxonmobil expands", {}).matchedTickers, (Strings{"XOM"}));
    EXPECT_TRUE(matcher.match("Applesauce futures", {}).matchedTickers.empty());
    EXPECT_TRUE(matcher.match("Amazonian rainforest", {}).matchedTickers.empty());
}

TEST_F(WatchlistMatcherTest, PlainTickersAreCaseSensitiveButCashtagsAreNot) {
    EXPECT_TRUE(matcher.match("i will cvx later", {}).matchedTickers.empty());
    EXPECT_EQ(matcher.match("watch $cvx today", {}).matchedTickers, (Strings{"CVX"}));
    EXPECT_EQ(matcher.match("CVX beats", {}).matchedTickers, (Strings{"CVX"}));
}

TEST_F(WatchlistMatcherTest, SectorEtfsMatchDirectlyWithoutEquity) {
    const auto r = matcher.match("Energy ETF XLE jumps, $smh too", {"xlf"});
    EXPECT_TRUE(r.matchedTickers.empty());
    EXPECT_EQ(r.matchedSectorEtfs, (Strings{"SMH", "XLE", "XLF"}));
}

TEST_F(WatchlistMatcherTest, ResultsAreSortedAndUnique) {
    const auto r = matcher.match("Microsoft MSFT $msft Apple AAPL", {"MSFT", "aapl"});
    EXPECT_EQ(r.matchedTickers, (Strings{"AAPL", "MSFT"}));
    EXPECT_EQ(r.matchedSectorEtfs, (Strings{"XLK"}));
}

TEST_F(WatchlistMatcherTest, BenchmarkAndUnknownSymbolsDoNotMatch) {
    const auto r = matcher.match("SPY and QQQ drift", {"SPY", "QQQ"});
    EXPECT_TRUE(r.matchedTickers.empty());
    EXPECT_TRUE(r.matchedSectorEtfs.empty());
}

TEST(WatchlistMatcherStandalone, EquityWithUnknownSectorHasNoEtf) {
    sentix::config::UniverseConfig cfg;
    cfg.watchlist.push_back({"ACME", "UNKNOWN_SECTOR", {}});
    const WatchlistMatcher matcher(cfg);
    const auto r = matcher.match("ACME soars", {});
    EXPECT_EQ(r.matchedTickers, (Strings{"ACME"}));
    EXPECT_TRUE(r.matchedSectorEtfs.empty());
}
