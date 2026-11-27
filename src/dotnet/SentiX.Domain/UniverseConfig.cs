using System.Text.Json.Serialization;

namespace SentiX.Domain;

public sealed record UniverseConfig(
    int Version,
    int MaxEquitiesWsSymbols,
    FastPathConfig FastPath,
    string BenchmarkTicker,
    IReadOnlyDictionary<string, string> SectorEtfs,
    IReadOnlyList<WatchlistEntry> Watchlist,
    IReadOnlyList<SocialAllowlistEntry> SocialAllowlist)
{
    [JsonIgnore]
    public IReadOnlyList<string> EquitiesSymbols => Watchlist
        .Select(entry => entry.Ticker)
        .Concat(SectorEtfs.Values)
        .Append(BenchmarkTicker)
        .Select(ticker => ticker.Trim().ToUpperInvariant())
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

public sealed record FastPathConfig(double MinAbsCompositeScore, int DedupWindowMinutes);
public sealed record WatchlistEntry(string Ticker, string Sector, IReadOnlyList<string> Aliases, string? Cik = null);
public sealed record SocialAllowlistEntry(EventSource Source, string Identifier, TrustTier TrustTier);
