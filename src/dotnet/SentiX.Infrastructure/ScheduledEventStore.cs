using Microsoft.EntityFrameworkCore;

namespace SentiX.Infrastructure;

public sealed class ScheduledEventStore(IDbContextFactory<SentiXDbContext> dbFactory)
{
    public async Task AddNewAsync(IReadOnlyCollection<ScheduledEventRow> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The existing schema has no unique constraint for the calendar key. Serialize writes
        // while checking the natural key, including the nullable ticker on macro events.
        await LockCalendarAsync(db, cancellationToken);
        foreach (var row in events)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO scheduled_events (event_type, ticker, title, scheduled_at_utc, source)
                SELECT {row.EventType}, CAST({row.Ticker} AS text), {row.Title}, {row.ScheduledAtUtc}, {row.Source}
                WHERE NOT EXISTS (
                    SELECT 1 FROM scheduled_events
                    WHERE event_type = {row.EventType}
                        AND ticker IS NOT DISTINCT FROM CAST({row.Ticker} AS text)
                        AND title = {row.Title}
                        AND scheduled_at_utc = {row.ScheduledAtUtc}
                        AND source = {row.Source}
                )
                """, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Reconciles a successful Finnhub snapshot over a half-open UTC window.</summary>
    public async Task ReplaceFinnhubWindowAsync(
        DateTimeOffset fromDateUtc,
        DateTimeOffset toDateUtc,
        IReadOnlyCollection<ScheduledEventRow> events,
        bool includesEconomicCalendar,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(toDateUtc, fromDateUtc);
        fromDateUtc = fromDateUtc.ToUniversalTime();
        toDateUtc = toDateUtc.ToUniversalTime();

        if (events.Any(row => row.Source != "FINNHUB" || row.ScheduledAtUtc < fromDateUtc ||
                row.ScheduledAtUtc >= toDateUtc || (!includesEconomicCalendar && row.Ticker is null)))
        {
            throw new ArgumentException("Calendar rows must be within the Finnhub snapshot scope.", nameof(events));
        }

        var incoming = events.DistinctBy(CalendarKey).ToDictionary(CalendarKey);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockCalendarAsync(db, cancellationToken);

        var existing = await db.ScheduledEvents
            .AsNoTracking()
            .Where(row => row.Source == "FINNHUB" &&
                row.ScheduledAtUtc >= fromDateUtc && row.ScheduledAtUtc < toDateUtc &&
                (includesEconomicCalendar || row.Ticker != null))
            .OrderBy(row => row.Id)
            .ToArrayAsync(cancellationToken);

        var unchanged = new List<long>();
        var obsolete = new List<long>();
        foreach (var row in existing)
        {
            var destination = incoming.Remove(CalendarKey(row)) ? unchanged : obsolete;
            destination.Add(row.Id);
        }

        if (obsolete.Count > 0)
        {
            await db.ScheduledEvents.Where(row => obsolete.Contains(row.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        var syncedAtUtc = DateTimeOffset.UtcNow;
        if (unchanged.Count > 0)
        {
            await db.ScheduledEvents.Where(row => unchanged.Contains(row.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.SyncedAtUtc, syncedAtUtc), cancellationToken);
        }

        db.ScheduledEvents.AddRange(incoming.Values.Select(row => row with
        {
            Id = 0,
            ScheduledAtUtc = row.ScheduledAtUtc.ToUniversalTime(),
            SyncedAtUtc = syncedAtUtc
        }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static (string EventType, string? Ticker, string Title, DateTimeOffset At) CalendarKey(ScheduledEventRow row) =>
        (row.EventType, row.Ticker, row.Title, row.ScheduledAtUtc);

    private static Task LockCalendarAsync(SentiXDbContext db, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync("LOCK TABLE scheduled_events IN SHARE ROW EXCLUSIVE MODE", cancellationToken);
}
