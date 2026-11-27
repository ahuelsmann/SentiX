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
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE scheduled_events IN SHARE ROW EXCLUSIVE MODE", cancellationToken);
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
}
