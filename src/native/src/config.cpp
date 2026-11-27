#include "sentix/config.hpp"

#include <fstream>
#include <set>
#include <sstream>

#include <nlohmann/json.hpp>

#include "sentix/text_cleaner.hpp"

namespace sentix::config {
namespace {

using nlohmann::json;

const json& require(const json& obj, const char* key, json::value_t type, const char* context) {
    const auto it = obj.find(key);
    if (it == obj.end()) {
        throw ConfigError(std::string(context) + ": missing '" + key + "'");
    }
    const bool numberOk = type == json::value_t::number_float && it->is_number();
    const bool integerOk = type == json::value_t::number_integer && it->is_number_integer();
    if (!(numberOk || integerOk || it->type() == type)) {
        throw ConfigError(std::string(context) + ": '" + key + "' has the wrong JSON type");
    }
    return *it;
}

std::string require_symbol(const json& obj, const char* key, const char* context) {
    const std::string value{text::trim(require(obj, key, json::value_t::string, context).get<std::string>())};
    if (value.empty()) {
        throw ConfigError(std::string(context) + ": '" + key + "' must not be empty");
    }
    return value;
}

int require_positive_int(const json& obj, const char* key, const char* context) {
    const auto& node = require(obj, key, json::value_t::number_integer, context);
    const auto value = node.get<long long>();
    if (value <= 0 || value > 1'000'000) {
        throw ConfigError(std::string(context) + ": '" + key + "' must be a positive integer");
    }
    return static_cast<int>(value);
}

}  // namespace

UniverseConfig parse_universe_config(const json& document) {
    if (!document.is_object()) {
        throw ConfigError("universe: root must be a JSON object");
    }

    UniverseConfig cfg;
    cfg.version = require_positive_int(document, "version", "universe");
    cfg.maxEquitiesWsSymbols = require_positive_int(document, "maxEquitiesWsSymbols", "universe");
    cfg.benchmarkTicker = text::to_upper_ascii(require_symbol(document, "benchmarkTicker", "universe"));

    const auto& fastPath = require(document, "fastPath", json::value_t::object, "universe");
    cfg.minAbsCompositeScore = require(fastPath, "minAbsCompositeScore", json::value_t::number_float, "fastPath").get<double>();
    if (!(cfg.minAbsCompositeScore >= 0.0 && cfg.minAbsCompositeScore <= 1.0)) {
        throw ConfigError("fastPath: 'minAbsCompositeScore' must be within [0, 1]");
    }
    cfg.dedupWindowMinutes = require_positive_int(fastPath, "dedupWindowMinutes", "fastPath");

    const auto& etfs = require(document, "sectorEtfs", json::value_t::object, "universe");
    for (const auto& [sector, etf] : etfs.items()) {
        if (!etf.is_string() || text::trim(etf.get<std::string>()).empty() || text::trim(sector).empty()) {
            throw ConfigError("sectorEtfs: every entry must map a sector to a non-empty ETF ticker");
        }
        cfg.sectorEtfs.emplace(std::string(text::trim(sector)), text::to_upper_ascii(text::trim(etf.get<std::string>())));
    }

    const auto& watchlist = require(document, "watchlist", json::value_t::array, "universe");
    for (const auto& entry : watchlist) {
        if (!entry.is_object()) {
            throw ConfigError("watchlist: entries must be objects");
        }
        WatchlistEntry w;
        w.ticker = text::to_upper_ascii(require_symbol(entry, "ticker", "watchlist"));
        w.sector = require_symbol(entry, "sector", "watchlist");
        if (const auto aliases = entry.find("aliases"); aliases != entry.end()) {
            if (!aliases->is_array()) {
                throw ConfigError("watchlist: 'aliases' must be an array");
            }
            for (const auto& alias : *aliases) {
                if (!alias.is_string() || text::trim(alias.get<std::string>()).empty()) {
                    throw ConfigError("watchlist: aliases must be non-empty strings");
                }
                w.aliases.emplace_back(text::trim(alias.get<std::string>()));
            }
        }
        cfg.watchlist.push_back(std::move(w));
    }

    const auto& allowlist = require(document, "socialAllowlist", json::value_t::array, "universe");
    for (const auto& entry : allowlist) {
        if (!entry.is_object()) {
            throw ConfigError("socialAllowlist: entries must be objects");
        }
        AllowlistEntry a;
        a.source = require_symbol(entry, "source", "socialAllowlist");
        a.identifier = require_symbol(entry, "identifier", "socialAllowlist");
        if (const auto tier = entry.find("trustTier"); tier != entry.end() && tier->is_string()) {
            a.trustTier = tier->get<std::string>();
        }
        cfg.socialAllowlist.push_back(std::move(a));
    }

    // Plan limit (specs/001 §4): unique watchlist + sector ETFs + benchmark <= maxEquitiesWsSymbols.
    std::set<std::string> symbols{cfg.benchmarkTicker};
    for (const auto& w : cfg.watchlist) {
        symbols.insert(w.ticker);
    }
    for (const auto& [sector, etf] : cfg.sectorEtfs) {
        symbols.insert(etf);
    }
    if (symbols.size() > static_cast<std::size_t>(cfg.maxEquitiesWsSymbols)) {
        throw ConfigError("universe: " + std::to_string(symbols.size()) + " unique symbols exceed maxEquitiesWsSymbols");
    }

    return cfg;
}

UniverseConfig parse_universe_config_text(std::string_view jsonText) {
    json document;
    try {
        document = json::parse(jsonText.begin(), jsonText.end());
    } catch (const json::exception& ex) {
        throw ConfigError(std::string("universe: invalid JSON: ") + ex.what());
    }
    try {
        return parse_universe_config(document);
    } catch (const json::exception& ex) {
        throw ConfigError(std::string("universe: ") + ex.what());
    }
}

UniverseConfig load_universe_config(const std::filesystem::path& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) {
        throw ConfigError("universe: cannot open '" + path.string() + "'");
    }
    std::ostringstream buffer;
    buffer << in.rdbuf();
    return parse_universe_config_text(buffer.str());
}

}  // namespace sentix::config
