namespace SentiX.Domain;

public static class UniverseConfigValidator
{
    public const int EquitiesWsLimit = 30;

    public static void Validate(UniverseConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Version != 1 ||
            config.FastPath is null ||
                !double.IsFinite(config.FastPath.MinAbsCompositeScore) ||
                config.FastPath.MinAbsCompositeScore is < 0 or > 1 ||
                config.FastPath.DedupWindowMinutes <= 0)
        {
            throw new InvalidDataException("Expected version 1 and valid fast-path settings.");
        }

        if (config.MaxEquitiesWsSymbols is < 1 or > EquitiesWsLimit)
        {
            throw new InvalidDataException($"maxEquitiesWsSymbols must be between 1 and {EquitiesWsLimit}.");
        }

        if (string.IsNullOrWhiteSpace(config.BenchmarkTicker) ||
            config.SectorEtfs is null || config.SectorEtfs.Values.Any(string.IsNullOrWhiteSpace) ||
            config.Watchlist is null || config.Watchlist.Any(entry => entry is null ||
                string.IsNullOrWhiteSpace(entry.Ticker) || string.IsNullOrWhiteSpace(entry.Sector) ||
                entry.Aliases is null) ||
            config.SocialAllowlist is null || config.SocialAllowlist.Any(entry => entry is null ||
                string.IsNullOrWhiteSpace(entry.Identifier)))
        {
            throw new InvalidDataException("Universe entries must have non-empty identifiers and tickers.");
        }

        int symbolCount = config.EquitiesSymbols.Count;
        if (symbolCount > config.MaxEquitiesWsSymbols)
        {
            throw new InvalidDataException(
                $"Universe has {symbolCount} unique equities symbols. The configured limit is {config.MaxEquitiesWsSymbols}.");
        }
    }
}
