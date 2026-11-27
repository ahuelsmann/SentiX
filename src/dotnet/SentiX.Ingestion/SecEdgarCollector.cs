using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SentiX.Domain;

namespace SentiX.Ingestion;

public sealed record SecOptions(string? UserAgent, int PollIntervalSeconds = 30);

public sealed partial class SecEdgarCollector(
    SecOptions options,
    UniverseConfig universe,
    IMarketEventPipeline pipeline,
    HttpClient http,
    TimeProvider clock,
    ILogger<SecEdgarCollector> logger) : BackgroundService
{
    public const string FeedUrl =
        "https://www.sec.gov/cgi-bin/browse-edgar?action=getcurrent&type=8-K&owner=include&count=40&output=atom";
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var agent = options.UserAgent?.Trim();
        if (string.IsNullOrEmpty(agent) || agent.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
            !pipeline.IsConfigured)
        {
            logger.LogInformation("SEC EDGAR ingestion skipped because the user agent or PostgreSQL is unconfigured.");
            return;
        }

        if (agent.Any(c => c < ' ' || c > '~') ||
            !agent.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part => part.Contains('@')))
        {
            logger.LogWarning("SEC EDGAR ingestion requires a User-Agent with a contact email.");
            return;
        }

        var pollDelay = TimeSpan.FromSeconds(Math.Clamp(options.PollIntervalSeconds, 1, 86_400));
        var retrySeconds = 1;
        var processed = new HashSet<string>(StringComparer.Ordinal);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = pollDelay;
            try
            {
                await pipeline.EnsureReadyAsync(stoppingToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
                request.Headers.TryAddWithoutValidation("User-Agent", agent);
                using var response = await http.SendAsync(request, stoppingToken);

                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    delay = RespectRetryAfter(response.Headers.RetryAfter, TimeSpan.FromSeconds(retrySeconds));
                    retrySeconds = Math.Min(retrySeconds * 2, 300);
                    logger.LogWarning("SEC EDGAR returned HTTP {Status}. Retrying in {Seconds}s.",
                        (int)response.StatusCode, delay.TotalSeconds);
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    using var stream = await response.Content.ReadAsStreamAsync(stoppingToken);
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings
                    {
                        Async = true,
                        DtdProcessing = DtdProcessing.Prohibit,
                        MaxCharactersInDocument = 4 * 1024 * 1024
                    });
                    var feed = await XDocument.LoadAsync(reader, LoadOptions.None, stoppingToken);
                    var current = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in feed.Root?.Elements(Atom + "entry") ?? [])
                    {
                        if (TryNormalize(item, universe) is not { } filing || !current.Add(filing.Event.EventId))
                        {
                            continue;
                        }

                        if (!processed.Contains(filing.Event.EventId))
                        {
                            await pipeline.ProcessAsync(filing.Event, filing.RawPayload, stoppingToken);
                            processed.Add(filing.Event.EventId);
                        }
                    }

                    processed.IntersectWith(current);
                    retrySeconds = 1;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or
                NpgsqlException or DbUpdateException or TimeoutException or OperationCanceledException)
            {
                logger.LogWarning("SEC EDGAR polling failed ({ErrorType}).", ex.GetType().Name);
                delay = TimeSpan.FromSeconds(retrySeconds);
                retrySeconds = Math.Min(retrySeconds * 2, 300);
            }

            await Task.Delay(delay < RequestSpacing ? RequestSpacing : delay, clock, stoppingToken);
        }
    }

    private TimeSpan RespectRetryAfter(RetryConditionHeaderValue? retryAfter, TimeSpan backoff)
    {
        var requested = retryAfter?.Delta ?? retryAfter?.Date - clock.GetUtcNow() ?? TimeSpan.Zero;
        var delay = requested > backoff ? requested : backoff;
        return delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay;
    }

    private static (MarketEvent Event, string RawPayload)? TryNormalize(XElement item, UniverseConfig universe)
    {
        var title = CleanText((string?)item.Element(Atom + "title"));
        string? form = item.Elements(Atom + "category").Select(e => (string?)e.Attribute("term"))
            .FirstOrDefault(term => !string.IsNullOrWhiteSpace(term));
        var titleMatch = FilingTitle().Match(title);
        form ??= titleMatch.Success ? titleMatch.Groups["form"].Value : null;
        if (form is not ("8-K" or "8-K/A"))
        {
            return null;
        }

        string id = (string?)item.Element(Atom + "id") ?? "";
        string link = (string?)item.Element(Atom + "link")?.Attribute("href") ?? "";
        var accession = Accession().Match(id);
        if (!accession.Success)
        {
            accession = Accession().Match(link);
        }

        var published = (string?)item.Element(Atom + "published") ?? (string?)item.Element(Atom + "updated");
        if (!accession.Success || title.Length == 0 || !DateTimeOffset.TryParse(published,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            return null;
        }

        var company = titleMatch.Groups["company"].Value;
        var cik = titleMatch.Groups["cik"].Value;
        var symbols = universe.Watchlist.Where(entry =>
                entry.Ticker.Trim().Equals(company, StringComparison.OrdinalIgnoreCase) ||
                entry.Aliases.Any(alias => MatchesCompany(company, alias)) ||
                Regex.IsMatch(company, $@"\({Regex.Escape(entry.Ticker.Trim())}\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(entry => entry.Ticker.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (symbols.Length == 0)
        {
            return null;
        }

        var summary = CleanText((string?)item.Element(Atom + "summary"));
        var marketEvent = new MarketEvent($"sec-edgar-{accession.Value}", EventSource.SecEdgar, "SEC EDGAR",
            timestamp.ToUniversalTime(), title, summary, summary, symbols);
        return (marketEvent, JsonSerializer.Serialize(new
        {
            accession = accession.Value, cik, company, form, link, title, summary
        }, SentiXJson.Options));
    }

    private static bool MatchesCompany(string company, string alias)
    {
        var normalizedAlias = CompanyWords(alias);
        return normalizedAlias.Length > 0 &&
            (" " + CompanyWords(company) + " ").Contains(" " + normalizedAlias + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static string CompanyWords(string value) => NonCompanyWord().Replace(value, " ").Trim();

    private static string CleanText(string? value) =>
        WhiteSpace().Replace(HtmlTag().Replace(WebUtility.HtmlDecode(value ?? ""), " "), " ").Trim();

    [GeneratedRegex(@"\b\d{10}-\d{2}-\d{6}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Accession();

    [GeneratedRegex(@"^(?<form>8-K(?:/A)?)\s*-\s*(?<company>.+?)(?:\s+\((?<cik>\d{1,10})\))?(?:\s+\((?:Filer|Reporting|Subject)\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex FilingTitle();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonCompanyWord();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpace();
}
