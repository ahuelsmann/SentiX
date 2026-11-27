// SentiX native fast-path — config/universe.json model (HANDOFF §11).
#pragma once

#include <filesystem>
#include <map>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#include <nlohmann/json_fwd.hpp>

namespace sentix::config {

struct WatchlistEntry {
    std::string ticker;
    std::string sector;
    std::vector<std::string> aliases;
};

struct AllowlistEntry {
    std::string source;
    std::string identifier;
    std::string trustTier;
};

struct UniverseConfig {
    int version{1};
    int maxEquitiesWsSymbols{30};
    double minAbsCompositeScore{0.55};
    int dedupWindowMinutes{180};
    std::string benchmarkTicker{"SPY"};
    std::map<std::string, std::string> sectorEtfs;  // sector -> ETF ticker
    std::vector<WatchlistEntry> watchlist;
    std::vector<AllowlistEntry> socialAllowlist;
};

inline constexpr std::string_view kDefaultConfigPath = "/config/universe.json";
inline constexpr std::string_view kXAllowlistSource = "X_ALLOWLIST";

class ConfigError : public std::runtime_error {
public:
    using std::runtime_error::runtime_error;
};

/// Validates and converts a parsed universe document. Throws ConfigError.
[[nodiscard]] UniverseConfig parse_universe_config(const nlohmann::json& document);

/// Parses JSON text. Throws ConfigError (also for JSON syntax errors).
[[nodiscard]] UniverseConfig parse_universe_config_text(std::string_view jsonText);

/// Reads and parses a file. Throws ConfigError when missing/unreadable/invalid.
[[nodiscard]] UniverseConfig load_universe_config(const std::filesystem::path& path);

}  // namespace sentix::config
