#include <gtest/gtest.h>

#include <cctype>

#include <nlohmann/json.hpp>

#include "sentix/config.hpp"
#include "test_helpers.hpp"

using namespace sentix::config;
using nlohmann::json;

namespace {

json minimal_universe() {
    return json::parse(R"({
      "version": 1,
      "maxEquitiesWsSymbols": 30,
      "fastPath": { "minAbsCompositeScore": 0.55, "dedupWindowMinutes": 180 },
      "benchmarkTicker": "SPY",
      "sectorEtfs": { "TECH": "XLK" },
      "watchlist": [ { "ticker": "AAPL", "sector": "TECH", "aliases": ["Apple"] } ],
      "socialAllowlist": [ { "source": "ALPACA_BENZINGA", "identifier": "NEWSWIRE", "trustTier": "PRIMARY" } ]
    })");
}

}  // namespace

TEST(Config, LoadsRepositoryUniverseJson) {
    const auto cfg = load_universe_config(sentix::testing::universe_json_path());

    EXPECT_EQ(cfg.version, 1);
    EXPECT_EQ(cfg.maxEquitiesWsSymbols, 30);
    EXPECT_DOUBLE_EQ(cfg.minAbsCompositeScore, 0.55);
    EXPECT_EQ(cfg.dedupWindowMinutes, 180);
    EXPECT_EQ(cfg.benchmarkTicker, "SPY");
    EXPECT_EQ(cfg.watchlist.size(), 10U);
    EXPECT_EQ(cfg.sectorEtfs.size(), 8U);
    EXPECT_EQ(cfg.socialAllowlist.size(), 7U);
    EXPECT_EQ(cfg.sectorEtfs.at("SEMICONDUCTORS"), "SMH");
    EXPECT_EQ(cfg.watchlist[8].ticker, "JPM");
    EXPECT_EQ(cfg.watchlist[8].aliases, (std::vector<std::string>{"JPMorgan", "JP Morgan", "JPMorgan Chase", "JPMorgan Chase & Co"}));
    EXPECT_EQ(cfg.socialAllowlist[2].source, "X_ALLOWLIST");
    EXPECT_EQ(cfg.socialAllowlist[2].identifier, "realDonaldTrump");
    EXPECT_EQ(cfg.socialAllowlist[2].trustTier, "VERIFIED_SOCIAL");
}

TEST(Config, ParsesMinimalDocumentAndNormalizesTickers) {
    auto doc = minimal_universe();
    doc["watchlist"][0]["ticker"] = " aapl ";
    doc["sectorEtfs"]["TECH"] = "xlk";
    const auto cfg = parse_universe_config(doc);
    EXPECT_EQ(cfg.watchlist.at(0).ticker, "AAPL");
    EXPECT_EQ(cfg.sectorEtfs.at("TECH"), "XLK");
}

TEST(Config, AliasesAndTrustTierAreOptional) {
    auto doc = minimal_universe();
    doc["watchlist"][0].erase("aliases");
    doc["socialAllowlist"][0].erase("trustTier");
    const auto cfg = parse_universe_config(doc);
    EXPECT_TRUE(cfg.watchlist.at(0).aliases.empty());
    EXPECT_TRUE(cfg.socialAllowlist.at(0).trustTier.empty());
}

TEST(Config, MissingFileThrows) {
    EXPECT_THROW((void)load_universe_config("/nonexistent/sentix/universe.json"), ConfigError);
}

TEST(Config, InvalidJsonThrows) {
    EXPECT_THROW((void)parse_universe_config_text("{ not json"), ConfigError);
    EXPECT_THROW((void)parse_universe_config_text("[]"), ConfigError);
}

struct InvalidCase {
    const char* name;
    void (*mutate)(json&);
};

// Readable parameter names in gtest/ctest output.
void PrintTo(const InvalidCase& c, std::ostream* os) { *os << c.name; }

class ConfigValidation : public ::testing::TestWithParam<InvalidCase> {};

TEST_P(ConfigValidation, RejectsInvalidDocument) {
    auto doc = minimal_universe();
    GetParam().mutate(doc);
    EXPECT_THROW((void)parse_universe_config(doc), ConfigError) << GetParam().name;
    EXPECT_THROW((void)parse_universe_config_text(doc.dump()), ConfigError) << GetParam().name;
}

INSTANTIATE_TEST_SUITE_P(
    Config, ConfigValidation,
    ::testing::Values(
        InvalidCase{"missing version", [](json& d) { d.erase("version"); }},
        InvalidCase{"version string", [](json& d) { d["version"] = "1"; }},
        InvalidCase{"version zero", [](json& d) { d["version"] = 0; }},
        InvalidCase{"version float", [](json& d) { d["version"] = 1.5; }},
        InvalidCase{"missing fastPath", [](json& d) { d.erase("fastPath"); }},
        InvalidCase{"score out of range", [](json& d) { d["fastPath"]["minAbsCompositeScore"] = 1.5; }},
        InvalidCase{"score negative", [](json& d) { d["fastPath"]["minAbsCompositeScore"] = -0.1; }},
        InvalidCase{"score string", [](json& d) { d["fastPath"]["minAbsCompositeScore"] = "0.55"; }},
        InvalidCase{"window negative", [](json& d) { d["fastPath"]["dedupWindowMinutes"] = -5; }},
        InvalidCase{"empty benchmark", [](json& d) { d["benchmarkTicker"] = "  "; }},
        InvalidCase{"sectorEtfs array", [](json& d) { d["sectorEtfs"] = json::array(); }},
        InvalidCase{"sector etf number", [](json& d) { d["sectorEtfs"]["TECH"] = 1; }},
        InvalidCase{"sector etf empty", [](json& d) { d["sectorEtfs"]["TECH"] = ""; }},
        InvalidCase{"watchlist object", [](json& d) { d["watchlist"] = json::object(); }},
        InvalidCase{"watchlist entry string", [](json& d) { d["watchlist"][0] = "AAPL"; }},
        InvalidCase{"watchlist missing ticker", [](json& d) { d["watchlist"][0].erase("ticker"); }},
        InvalidCase{"watchlist empty sector", [](json& d) { d["watchlist"][0]["sector"] = ""; }},
        InvalidCase{"aliases string", [](json& d) { d["watchlist"][0]["aliases"] = "Apple"; }},
        InvalidCase{"alias empty", [](json& d) { d["watchlist"][0]["aliases"] = json::array({""}); }},
        InvalidCase{"alias number", [](json& d) { d["watchlist"][0]["aliases"] = json::array({1}); }},
        InvalidCase{"allowlist object", [](json& d) { d["socialAllowlist"] = json::object(); }},
        InvalidCase{"allowlist entry number", [](json& d) { d["socialAllowlist"][0] = 1; }},
        InvalidCase{"allowlist missing source", [](json& d) { d["socialAllowlist"][0].erase("source"); }},
        InvalidCase{"too many ws symbols", [](json& d) { d["maxEquitiesWsSymbols"] = 2; }}),
    [](const ::testing::TestParamInfo<InvalidCase>& paramInfo) {
        std::string name = paramInfo.param.name;
        for (char& c : name) {
            if (!std::isalnum(static_cast<unsigned char>(c))) {
                c = '_';
            }
        }
        return name;
    });
