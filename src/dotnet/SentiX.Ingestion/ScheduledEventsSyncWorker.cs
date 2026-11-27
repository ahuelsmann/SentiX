using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SentiX.Domain;
using SentiX.Infrastructure;

namespace SentiX.Ingestion;

public sealed class ScheduledEventsSyncWorker(
    FinnhubOptions options,
    UniverseConfig universe,
    IMarketEventPipeline pipeline,
    ScheduledEventStore store,
    HttpClient http,
    TimeProvider clock,
    ILogger<ScheduledEventsSyncWorker> logger) : BackgroundService
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeOnly SyncTime = new(6, 0);
    private const string BaseUrl = "https://finnhub.io/api/v1/calendar/";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey) || !pipeline.IsConfigured)
        {
            logger.LogInformation("Calendar sync skipped because Finnhub or PostgreSQL is unconfigured.");
            return;
        }

        var retrySeconds = 30;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await pipeline.EnsureReadyAsync(stoppingToken);
                var snapshot = await FetchSnapshotAsync(stoppingToken);
                await store.ReplaceFinnhubWindowAsync(
                    snapshot.FromDateUtc,
                    snapshot.ToDateUtc,
                    snapshot.Events,
                    snapshot.IncludesEconomicCalendar,
                    stoppingToken);
                var now = clock.GetUtcNow();
                delay = NextSyncUtc(now) - now;
                retrySeconds = 30;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or
                NpgsqlException or DbUpdateException or TimeoutException or OperationCanceledException)
            {
                logger.LogWarning("Calendar sync failed ({ErrorType}). Retrying in {Seconds}s.",
                    ex.GetType().Name, retrySeconds);
                delay = TimeSpan.FromSeconds(retrySeconds);
                retrySeconds = Math.Min(retrySeconds * 2, 300);
            }

            await Task.Delay(delay, clock, stoppingToken);
        }
    }

    internal static DateTimeOffset NextSyncUtc(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Eastern).DateTime);
        var next = ToUtc(today, SyncTime);
        return next > now ? next : ToUtc(today.AddDays(1), SyncTime);
    }

    internal sealed record CalendarSyncSnapshot(
        DateTimeOffset FromDateUtc,
        DateTimeOffset ToDateUtc,
        IReadOnlyCollection<ScheduledEventRow> Events,
        bool IncludesEconomicCalendar);

    internal async Task<IReadOnlyCollection<ScheduledEventRow>> FetchAsync(CancellationToken token) =>
        (await FetchSnapshotAsync(token)).Events;

    internal async Task<CalendarSyncSnapshot> FetchSnapshotAsync(CancellationToken token)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Eastern).DateTime);
        var end = today.AddDays(13);
        var fromDateUtc = ToUtc(today, TimeOnly.MinValue);
        var toDateUtc = ToUtc(end.AddDays(1), TimeOnly.MinValue);
        // Retrieve one calendar window and keep only configured names. This avoids
        // repeating the same endpoint request for each watchlist symbol.
        string range = FormattableString.Invariant($"from={today:yyyy-MM-dd}&to={end:yyyy-MM-dd}");
        var watched = universe.Watchlist.Select(entry => entry.Ticker.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var events = new HashSet<ScheduledEventRow>();
        var (earnings, _) = await ReadCalendarStatusAsync<EarningsCalendar>($"earnings?{range}", optional: false, token);
        foreach (var item in earnings?.EarningsCalendarEntries ?? [])
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Symbol) || !watched.Contains(item.Symbol) ||
                !DateOnly.TryParseExact(item.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out var date) || date < today || date > end)
            {
                continue;
            }

            var symbol = item.Symbol.ToUpperInvariant();
            // The earnings calendar supplies a session hint, not a release time.
            // Store market open/close boundaries and use Eastern midnight for an unknown session.
            var time = item.Hour switch
            {
                "bmo" => new TimeOnly(9, 30),
                "amc" => new TimeOnly(16, 0),
                "dmh" => new TimeOnly(12, 0),
                _ => TimeOnly.MinValue
            };
            events.Add(new ScheduledEventRow(0, "EARNINGS", symbol, $"{symbol} Earnings", ToUtc(date, time), "FINNHUB", default));
        }

        var (macro, macroAvailable) = await ReadCalendarStatusAsync<EconomicCalendar>($"economic?{range}", optional: true, token);
        foreach (var item in macro?.EconomicCalendarEntries ?? [])
        {
            if (item is null || !string.Equals(item.Country, "US", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(item.Event) || !DateTimeOffset.TryParse(item.Time,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                continue;
            }

            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, Eastern).DateTime);
            if (date >= today && date <= end)
            {
                var title = item.Event.Trim();
                events.Add(new ScheduledEventRow(0, ClassifyMacro(title), null, title, timestamp.ToUniversalTime(),
                    "FINNHUB", default));
            }
        }

        bool includesEconomicCalendar = macroAvailable && macro is not null;
        return new CalendarSyncSnapshot(fromDateUtc, toDateUtc, events, includesEconomicCalendar);
    }

    private async Task<(T? Payload, bool Available)> ReadCalendarStatusAsync<T>(
        string endpoint,
        bool optional,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + endpoint);
        request.Headers.Add("X-Finnhub-Token", options.ApiKey!.Trim());
        using var response = await http.SendAsync(request, token);
        if (optional && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            logger.LogInformation("Finnhub economic calendar is unavailable to this account.");
            return (default, false);
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<T>(cancellationToken: token);
        return (payload, payload is not null);
    }

    private static string ClassifyMacro(string title)
    {
        if (title.Contains("FOMC", StringComparison.OrdinalIgnoreCase))
        {
            return "FOMC";
        }

        if (title.Contains("CPI", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Consumer Price Index", StringComparison.OrdinalIgnoreCase))
        {
            return "CPI";
        }

        return title.Contains("Nonfarm Payroll", StringComparison.OrdinalIgnoreCase) ? "NFP" : "MACRO";
    }

    private static DateTimeOffset ToUtc(DateOnly date, TimeOnly time) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time), Eastern), TimeSpan.Zero);

    private sealed record EarningsCalendar(
        [property: System.Text.Json.Serialization.JsonPropertyName("earningsCalendar")] EarningsItem?[]? EarningsCalendarEntries);
    private sealed record EarningsItem(string? Symbol, string? Date, string? Hour);
    private sealed record EconomicCalendar(
        [property: System.Text.Json.Serialization.JsonPropertyName("economicCalendar")] EconomicItem?[]? EconomicCalendarEntries);
    private sealed record EconomicItem(string? Country, string? Event, string? Time);
}
