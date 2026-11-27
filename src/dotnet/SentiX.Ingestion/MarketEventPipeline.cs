using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentiX.Domain;
using SentiX.Infrastructure;
using SentiX.NativeBridge;

namespace SentiX.Ingestion;

public interface IMarketEventPipeline
{
    bool IsConfigured { get; }
    Task EnsureReadyAsync(CancellationToken token);
    Task<int> CountXEventsAsync(DateTimeOffset start, CancellationToken token);
    Task ProcessAsync(MarketEvent marketEvent, string rawPayload, CancellationToken token);
}

public sealed class MarketEventPipeline(
    IDbContextFactory<SentiXDbContext> dbFactory,
    NativeBridgeClient native,
    ILogger<MarketEventPipeline> logger,
    bool isConfigured) : IMarketEventPipeline, IDisposable
{
    private readonly SemaphoreSlim migration = new(1, 1);
    private volatile bool migrated;

    public bool IsConfigured { get; } = isConfigured;

    // Apply the versioned schema before either collector opens its stream.
    public async Task EnsureReadyAsync(CancellationToken token)
    {
        if (migrated)
        {
            return;
        }

        await migration.WaitAsync(token);
        try
        {
            if (!migrated)
            {
                await using var context = await dbFactory.CreateDbContextAsync(token);
                await context.Database.MigrateAsync(token);
                migrated = true;
            }
        }
        finally
        {
            migration.Release();
        }
    }

    public async Task<int> CountXEventsAsync(DateTimeOffset start, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        return await db.MarketEvents.CountAsync(
            row => row.Source == "X_ALLOWLIST" && row.IngestedAtUtc >= start && row.IngestedAtUtc < start.AddDays(1),
            token);
    }

    public async Task ProcessAsync(MarketEvent marketEvent, string rawPayload, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        string source = JsonSerializer.SerializeToElement(marketEvent.Source, SentiXJson.Options).GetString()!;
        var timestamp = marketEvent.PublishedAtUtc.ToUniversalTime();
        // Preserve the canonical raw event on feed replay. Native deduplication evaluates each receipt.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO market_events
                (event_id, source, author, published_at_utc, headline, summary, body, source_tickers, raw_payload)
            VALUES ({marketEvent.EventId}, {source}, {marketEvent.Author}, {timestamp}, {marketEvent.Headline},
                {marketEvent.Summary}, {marketEvent.Body}, {marketEvent.SourceTickers}, CAST({rawPayload} AS jsonb))
            ON CONFLICT (event_id) DO NOTHING
            """, token);

        var attempt = await native.ScoreEventAsync(marketEvent, token);
        var score = attempt.Value;
        string? reason;
        if (attempt.IsFailClosed)
        {
            logger.LogWarning("Native scoring failed closed for {EventId}: {Failure} ({ErrorCode}).",
                marketEvent.EventId, attempt.Failure, attempt.ErrorCode);
            // The audit schema requires numeric fields. An empty hash and FAIL_CLOSED identify an unscored attempt.
            reason = "FAIL_CLOSED";
        }
        else
        {
            reason = score?.DropReason is { } drop
                ? JsonSerializer.SerializeToElement(drop, SentiXJson.Options).GetString()
                : null;
        }

        db.FastPathResults.Add(new FastPathResultRow(
            0, marketEvent.EventId, score?.DedupHash ?? "", score?.IsDuplicate ?? false, score?.MatchedTickers ?? [],
            score?.MatchedSectorEtfs ?? [], score?.Finbert.Positive ?? 0, score?.Finbert.Negative ?? 0,
            score?.Finbert.Neutral ?? 1, score?.Finbert.CompositeScore ?? 0, attempt.PassToDecisionSwarm,
            reason, score?.ProcessingTimeUs ?? 0, default));
        await db.SaveChangesAsync(token);
    }

    public void Dispose() => migration.Dispose();
}
