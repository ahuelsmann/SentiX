using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentiX.Execution;
using SentiX.Infrastructure;
using SentiX.Risk;

namespace SentiX.Ingestion;

public enum TradingStateWriteStatus
{
    Updated,
    CannotResumeNonManualHalt,
    DatabaseNotConfigured,
    Unavailable
}

public sealed record TradingStateWriteResult(
    TradingStateWriteStatus Status,
    string CurrentState,
    string UpdatedBy,
    DateTimeOffset UpdatedAtUtc,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public interface ITradingStateStore : ITradingStateProvider
{
    bool IsConfigured { get; }
    Task<TradingStateReadResult> ReadTradingStateAsync(CancellationToken cancellationToken);
    Task<TradingStateWriteResult> HaltManualAsync(string updatedBy, CancellationToken cancellationToken);
    Task<TradingStateWriteResult> ResumeManualAsync(string updatedBy, CancellationToken cancellationToken);
}

public static class PersistedTradingStateFormat
{
    public const int MaxUpdatedByLength = 64;
    public const string DefaultActor = "OPERATOR_UI";

    public static string ToDatabaseValue(TradingState state) => state switch
    {
        TradingState.Active => "ACTIVE",
        TradingState.HaltedManual => "HALTED_MANUAL",
        TradingState.HaltedDailyLoss => "HALTED_DAILY_LOSS",
        TradingState.HaltedSystemError => "HALTED_SYSTEM_ERROR",
        _ => "HALTED_SYSTEM_ERROR"
    };

    public static string SanitizeActor(string? updatedBy)
    {
        if (string.IsNullOrWhiteSpace(updatedBy))
        {
            return DefaultActor;
        }

        Span<char> buffer = stackalloc char[Math.Min(updatedBy.Length, MaxUpdatedByLength)];
        var count = 0;
        foreach (var ch in updatedBy.AsSpan().Trim())
        {
            if (char.IsControl(ch))
            {
                continue;
            }

            buffer[count++] = ch;
            if (count == MaxUpdatedByLength)
            {
                break;
            }
        }

        return count == 0 ? DefaultActor : new string(buffer[..count]);
    }
}

public sealed class UnconfiguredTradingStateStore(TimeProvider? timeProvider = null) : ITradingStateStore
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public bool IsConfigured => false;

    public Task<TradingStateReadResult> GetCurrentStateAsync(CancellationToken cancellationToken) =>
        ReadTradingStateAsync(cancellationToken);

    public Task<TradingStateReadResult> ReadTradingStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TradingStateReadResult(
            false,
            TradingState.HaltedSystemError,
            "PostgreSQL connection is not configured."));
    }

    public Task<TradingStateWriteResult> HaltManualAsync(string updatedBy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TradingStateWriteResult(
            TradingStateWriteStatus.DatabaseNotConfigured,
            "UNAVAILABLE",
            PersistedTradingStateFormat.SanitizeActor(updatedBy),
            clock.GetUtcNow(),
            "DatabaseNotConfigured",
            "PostgreSQL connection is not configured."));
    }

    public Task<TradingStateWriteResult> ResumeManualAsync(string updatedBy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TradingStateWriteResult(
            TradingStateWriteStatus.DatabaseNotConfigured,
            "UNAVAILABLE",
            PersistedTradingStateFormat.SanitizeActor(updatedBy),
            clock.GetUtcNow(),
            "DatabaseNotConfigured",
            "PostgreSQL connection is not configured."));
    }
}

public sealed class EfCoreTradingStateStore(
    IDbContextFactory<SentiXDbContext> dbFactory,
    TimeProvider? timeProvider = null,
    ILogger<EfCoreTradingStateStore>? logger = null) : ITradingStateStore
{
    private readonly IDbContextFactory<SentiXDbContext> dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ILogger log = (ILogger?)logger ?? NullLogger.Instance;

    public bool IsConfigured => true;

    public Task<TradingStateReadResult> GetCurrentStateAsync(CancellationToken cancellationToken) =>
        ReadTradingStateAsync(cancellationToken);

    public async Task<TradingStateReadResult> ReadTradingStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.SystemState.AsNoTracking()
                .Where(s => s.Id == 1)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                return new TradingStateReadResult(
                    false,
                    TradingState.HaltedSystemError,
                    "Persisted system_state row (id=1) is missing.");
            }

            return SystemStateParser.Parse(row.TradingState);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning("Failed to read persisted system_state ({ErrorType}). Failing closed.", ex.GetType().Name);
            return new TradingStateReadResult(
                false,
                TradingState.HaltedSystemError,
                $"Persisted trading state read failed ({ex.GetType().Name}).");
        }
    }

    public async Task<TradingStateWriteResult> HaltManualAsync(string updatedBy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var actor = PersistedTradingStateFormat.SanitizeActor(updatedBy);
        var now = clock.GetUtcNow();
        const string reason = "Manual operator kill-switch engaged.";

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO system_state (id, trading_state, reason, updated_by, updated_at_utc)
                VALUES (1, 'HALTED_MANUAL', {reason}, {actor}, {now})
                ON CONFLICT (id) DO UPDATE
                SET trading_state = 'HALTED_MANUAL',
                    reason = EXCLUDED.reason,
                    updated_by = EXCLUDED.updated_by,
                    updated_at_utc = EXCLUDED.updated_at_utc
                """, cancellationToken).ConfigureAwait(false);

            return new TradingStateWriteResult(
                TradingStateWriteStatus.Updated,
                "HALTED_MANUAL",
                actor,
                now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning("Failed to persist manual kill-switch halt ({ErrorType}).", ex.GetType().Name);
            return new TradingStateWriteResult(
                TradingStateWriteStatus.Unavailable,
                "UNAVAILABLE",
                actor,
                now,
                "DatabaseUnavailable",
                $"Failed to persist manual halt ({ex.GetType().Name}).");
        }
    }

    public async Task<TradingStateWriteResult> ResumeManualAsync(string updatedBy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var actor = PersistedTradingStateFormat.SanitizeActor(updatedBy);
        var now = clock.GetUtcNow();

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE system_state
                SET trading_state = 'ACTIVE',
                    reason = NULL,
                    updated_by = {actor},
                    updated_at_utc = {now}
                WHERE id = 1 AND trading_state IN ('HALTED_MANUAL', 'ACTIVE')
                """, cancellationToken).ConfigureAwait(false);

            if (updated == 1)
            {
                return new TradingStateWriteResult(
                    TradingStateWriteStatus.Updated,
                    "ACTIVE",
                    actor,
                    now);
            }

            var existing = await db.SystemState.AsNoTracking()
                .Where(s => s.Id == 1)
                .Select(s => s.TradingState)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var parsed = SystemStateParser.Parse(existing);
            var currentCanonical = parsed.Success
                ? PersistedTradingStateFormat.ToDatabaseValue(parsed.State)
                : "UNAVAILABLE";

            return new TradingStateWriteResult(
                TradingStateWriteStatus.CannotResumeNonManualHalt,
                currentCanonical,
                actor,
                now,
                "CannotResumeNonManualHalt",
                $"Manual UI resume is only permitted from HALTED_MANUAL (current: {currentCanonical}).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning("Failed to persist manual resume ({ErrorType}).", ex.GetType().Name);
            return new TradingStateWriteResult(
                TradingStateWriteStatus.Unavailable,
                "UNAVAILABLE",
                actor,
                now,
                "DatabaseUnavailable",
                $"Failed to persist manual resume ({ex.GetType().Name}).");
        }
    }
}
