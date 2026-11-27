using System.Text.Json;
using SentiX.Domain;

namespace SentiX.Ingestion;

public static class UniverseConfigLoader
{
    public static UniverseConfig Load(string path)
    {
        using var stream = File.OpenRead(path);
        var config = JsonSerializer.Deserialize<UniverseConfig>(stream, SentiXJson.Options)
            ?? throw new InvalidDataException("Universe configuration must be a JSON object.");
        UniverseConfigValidator.Validate(config);
        return config;
    }
}

public sealed record AlpacaOptions(string? ApiKey, string? SecretKey, bool PaperMode = true, bool AllowShorting = false)
{
    public bool HasCredentials => Credentials.IsConfigured(ApiKey) && Credentials.IsConfigured(SecretKey);
}

public sealed record XOptions(string? BearerToken, int DailyPostCap = XOptions.DefaultDailyPostCap)
{
    public const int DefaultDailyPostCap = 150;
    public bool HasCredentials => Credentials.IsConfigured(BearerToken);
}

public sealed record FinnhubOptions(string? ApiKey);

internal static class Credentials
{
    public static bool IsConfigured(string? value)
    {
        string? credential = value?.Trim();
        return !string.IsNullOrEmpty(credential) &&
            !credential.Contains("YOUR_", StringComparison.OrdinalIgnoreCase) &&
            !credential.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) &&
            !credential.StartsWith("change_me", StringComparison.OrdinalIgnoreCase);
    }
}
