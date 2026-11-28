using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentiX.Infrastructure;

namespace SentiX.Ingestion;

public enum OrderIntentDisposition
{
    ProceedToSubmit,
    AlreadyFinalizedSkip,
    FailedClosed
}

public sealed record RecentOrderAuditReadResult(
    bool IsSuccess,
    IReadOnlyList<OrderRow> Orders,
    string? ErrorReason = null);

public interface IPaperOrderAuditStore
{
    Task<RecentOrderAuditReadResult> GetRecentOrdersAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);
    Task<OrderIntentDisposition> TryBeginOrderIntentAsync(OrderRow pendingIntentRow, CancellationToken cancellationToken);
    Task CompleteOrderIntentAsync(OrderRow completedRow);
}

public sealed class EfCorePaperOrderAuditStore(
    IDbContextFactory<SentiXDbContext> dbFactory,
    ILogger? logger = null) : IPaperOrderAuditStore
{
    private static readonly TimeSpan PostSubmitCompletionTimeout = TimeSpan.FromSeconds(10);
    private readonly IDbContextFactory<SentiXDbContext> dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    private readonly ILogger log = logger ?? NullLogger.Instance;

    public async Task<RecentOrderAuditReadResult> GetRecentOrdersAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var rows = await db.Orders.AsNoTracking()
                .Where(o => o.UpdatedAtUtc >= sinceUtc)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            return new RecentOrderAuditReadResult(true, rows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning("Failed to read recent local orders for armed cooldown/pending check ({ErrorType}).", ex.GetType().Name);
            return new RecentOrderAuditReadResult(false, [], $"Local orders read failed ({ex.GetType().Name}).");
        }
    }

    public async Task<OrderIntentDisposition> TryBeginOrderIntentAsync(
        OrderRow pendingIntentRow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingIntentRow);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existing = await db.Orders.AsNoTracking()
                .SingleOrDefaultAsync(o => o.ClientOrderId == pendingIntentRow.ClientOrderId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                if (string.Equals(existing.Status, "PENDING_SUBMIT", StringComparison.OrdinalIgnoreCase))
                {
                    await db.Orders
                        .Where(o => o.ClientOrderId == pendingIntentRow.ClientOrderId && o.Status == "PENDING_SUBMIT")
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(o => o.UpdatedAtUtc, pendingIntentRow.UpdatedAtUtc),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return OrderIntentDisposition.ProceedToSubmit;
                }

                return OrderIntentDisposition.AlreadyFinalizedSkip;
            }

            db.Orders.Add(pendingIntentRow);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return OrderIntentDisposition.ProceedToSubmit;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DbUpdateException)
        {
            // Concurrent worker may have inserted the same client_order_id; inspect persisted status fail-closed.
            try
            {
                await using var retryDb = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var existing = await retryDb.Orders.AsNoTracking()
                    .SingleOrDefaultAsync(o => o.ClientOrderId == pendingIntentRow.ClientOrderId, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is not null && !string.Equals(existing.Status, "PENDING_SUBMIT", StringComparison.OrdinalIgnoreCase))
                {
                    return OrderIntentDisposition.AlreadyFinalizedSkip;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Fall through to fail-closed below.
            }

            log.LogWarning(
                "Write-ahead PENDING_SUBMIT insert conflicted for {ClientOrderId}; failing closed without broker call.",
                pendingIntentRow.ClientOrderId);
            return OrderIntentDisposition.FailedClosed;
        }
        catch (Exception ex)
        {
            log.LogWarning(
                "Write-ahead PENDING_SUBMIT insert failed for {ClientOrderId} ({ErrorType}); failing closed without broker call.",
                pendingIntentRow.ClientOrderId,
                ex.GetType().Name);
            return OrderIntentDisposition.FailedClosed;
        }
    }

    public async Task CompleteOrderIntentAsync(OrderRow completedRow)
    {
        ArgumentNullException.ThrowIfNull(completedRow);

        // Intentionally uses CancellationToken.None with a bounded timeout so host shutdown/request cancellation
        // cannot abort recording an accepted broker order receipt.
        using var completionCts = new CancellationTokenSource(PostSubmitCompletionTimeout);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(completionCts.Token).ConfigureAwait(false);
            db.Database.SetCommandTimeout(PostSubmitCompletionTimeout);

            var updated = await db.Orders
                .Where(o => o.ClientOrderId == completedRow.ClientOrderId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(o => o.AlpacaOrderId, completedRow.AlpacaOrderId)
                    .SetProperty(o => o.Qty, completedRow.Qty)
                    .SetProperty(o => o.LimitPrice, completedRow.LimitPrice)
                    .SetProperty(o => o.StopLossPrice, completedRow.StopLossPrice)
                    .SetProperty(o => o.TakeProfitPrice, completedRow.TakeProfitPrice)
                    .SetProperty(o => o.Status, completedRow.Status)
                    .SetProperty(o => o.FilledAvgPrice, completedRow.FilledAvgPrice)
                    .SetProperty(o => o.SubmittedAtUtc, completedRow.SubmittedAtUtc)
                    .SetProperty(o => o.UpdatedAtUtc, completedRow.UpdatedAtUtc),
                    completionCts.Token)
                .ConfigureAwait(false);

            if (updated == 0)
            {
                db.Orders.Add(completedRow);
                await db.SaveChangesAsync(completionCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            log.LogError(
                "Post-submit order update failed for ProposalId={ProposalId}, ClientOrderId={ClientOrderId}, FinalStatus={Status}, AlpacaOrderId={AlpacaOrderId} ({ErrorType}). Durable PENDING_SUBMIT row remains in orders.",
                completedRow.ProposalId,
                completedRow.ClientOrderId,
                completedRow.Status,
                completedRow.AlpacaOrderId ?? "none",
                ex.GetType().Name);
        }
    }
}
