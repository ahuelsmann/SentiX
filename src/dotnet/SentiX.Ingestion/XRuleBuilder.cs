using System.Text.RegularExpressions;
using SentiX.Domain;

namespace SentiX.Ingestion;

public static partial class XRuleBuilder
{
    public static string? Build(UniverseConfig universe)
    {
        var handles = GetHandles(universe);
        return handles.Count == 0
            ? null
            : $"({string.Join(" OR ", handles.Select(handle => $"from:{handle}"))}) -is:retweet -is:reply";
    }

    internal static IReadOnlyCollection<string> GetHandles(UniverseConfig universe)
    {
        var handles = universe.SocialAllowlist
            .Where(entry => entry.Source == EventSource.XAllowlist)
            .Select(entry => entry.Identifier.Trim().TrimStart('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (handles.Any(handle => !HandlePattern().IsMatch(handle)))
        {
            throw new InvalidDataException("An X allowlist identifier is not a valid handle.");
        }

        return handles;
    }

    [GeneratedRegex("\\A[A-Za-z0-9_]{1,15}\\z")]
    private static partial Regex HandlePattern();
}
