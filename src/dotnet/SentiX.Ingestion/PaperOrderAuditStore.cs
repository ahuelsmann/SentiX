using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentiX.Execution;
using SentiX.Infrastructure;

namespace SentiX.Ingestion;

public enum OrderIntentReservationStatus
{
    ReservedForSubmit,
    ReconciledExistingReceipt,
    AlreadyFinalizedOrClaimed,
    FailedClosed
}

public sealed record RecentOrderAuditReadResult(
    bool IsSuccess,
    IReadOnlyList<OrderRow> Orders,
    string? ErrorReason = null);

public interface IPaperOrderAuditStore
{
    /// <summary>
    /// Reconciles all expired (<c>&gt;= 60s</c>) <c>PENDING_SUBMIT</c> order rows under a fenced CAS claim via
    /// <see cref="IAlpacaPaperBrokerClient.GetOrderByClientOrderIdAsync"/> before armed risk evaluation runs.
    /// </summary>
    Task ReconcileExpiredPendingOrdersAsync(
        IAlpacaPaperBrokerClient? brokerClient,
        TimeProvider clock,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns ALL unresolved <c>PENDING_SUBMIT</c> intents regardless of age, plus finalized orders
    /// updated on or after <paramref name="finalizedSinceUtc"/> for ticker cooldown checks.
    /// </summary>
    Task<RecentOrderAuditReadResult> GetRecentOrdersAsync(DateTimeOffset finalizedSinceUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically reserves a single-writer <c>PENDING_SUBMIT</c> intent, or safely reconciles an expired orphaned
    /// <c>PENDING_SUBMIT</c> via <paramref name="brokerClient"/> under a fenced compare-and-swap lease without blind resubmission.
    /// </summary>
    Task<OrderIntentReservationStatus> TryReserveOrderIntentAsync(
        OrderRow pendingIntentRow,
        IAlpacaPaperBrokerClient? brokerClient,
        TimeProvider clock,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finalizes an in-flight order row conditionally (<c>WHERE status = 'PENDING_SUBMIT'</c>) using <see cref="CancellationToken.None"/>
    /// with a bounded command timeout so an already-finalized receipt can never be overwritten.
    /// </summary>
    Task CompleteOrderIntentAsync(OrderRow completedRow);
}

public sealed class EfCorePaperOrderAuditStore(
    IDbContextFactory<SentiXDbContext> dbFactory,
    ILogger? logger = null) : IPaperOrderAuditStore
{
    public static readonly TimeSpan PendingSubmitLeaseDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PostSubmitCompletionTimeout = TimeSpan.FromSeconds(10);

    private readonly IDbContextFactory<SentiXDbContext> dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    private readonly ILogger log = logger ?? NullLogger.Instance;

    public async Task ReconcileExpiredPendingOrdersAsync(
        IAlpacaPaperBrokerClient? brokerClient,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        cancellationToken.ThrowIfCancellationRequested();

        if (brokerClient is null)
        {
            return;
        }

        try
        {
            var cutoffUtc = clock.GetUtcNow() - PendingSubmitLeaseDuration;
            OrderRow[] expiredRows;
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                expiredRows = await db.Orders.AsNoTracking()
                    .Where(o => o.Status == "PENDING_SUBMIT" && o.UpdatedAtUtc <= cutoffUtc)
                    .OrderBy(o => o.UpdatedAtUtc)
                    .Take(50)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var expired in expiredRows)
            {
                await TryClaimAndReconcileExpiredRowAsync(expired, brokerClient, clock, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning("Pre-evaluation PENDING_SUBMIT reconciliation encountered an error ({ErrorType}); leaving pending intents fail-closed.", ex.GetType().Name);
        }
    }

    public async Task<RecentOrderAuditReadResult> GetRecentOrdersAsync(
        DateTimeOffset finalizedSinceUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var rows = await db.Orders.AsNoTracking()
                .Where(o => o.Status == "PENDING_SUBMIT" || o.UpdatedAtUtc >= finalizedSinceUtc)
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
            log.LogWarning("Failed to read local orders for armed cooldown/pending check ({ErrorType}).", ex.GetType().Name);
            return new RecentOrderAuditReadResult(false, [], $"Local orders read failed ({ex.GetType().Name}).");
        }
    }

    public async Task<OrderIntentReservationStatus> TryReserveOrderIntentAsync(
        OrderRow pendingIntentRow,
        IAlpacaPaperBrokerClient? brokerClient,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingIntentRow);
        ArgumentNullException.ThrowIfNull(clock);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            OrderRow? existing;
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO orders (
                        client_order_id, alpaca_order_id, proposal_id, ticker, side, order_type, qty,
                        limit_price, stop_loss_price, take_profit_price, status, filled_avg_price,
                        submitted_at_utc, updated_at_utc)
                    VALUES (
                        {pendingIntentRow.ClientOrderId}, NULL, {pendingIntentRow.ProposalId}, {pendingIntentRow.Ticker},
                        {pendingIntentRow.Side}, {pendingIntentRow.OrderType}, {pendingIntentRow.Qty},
                        {pendingIntentRow.LimitPrice}, {pendingIntentRow.StopLossPrice}, {pendingIntentRow.TakeProfitPrice},
                        'PENDING_SUBMIT', NULL, {pendingIntentRow.SubmittedAtUtc}, {pendingIntentRow.UpdatedAtUtc})
                    ON CONFLICT (client_order_id) DO NOTHING
                    """, cancellationToken).ConfigureAwait(false);

                if (inserted == 1)
                {
                    return OrderIntentReservationStatus.ReservedForSubmit;
                }

                existing = await db.Orders.AsNoTracking()
                    .SingleOrDefaultAsync(o => o.ClientOrderId == pendingIntentRow.ClientOrderId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (existing is null)
            {
                return OrderIntentReservationStatus.FailedClosed;
            }

            if (!string.Equals(existing.Status, "PENDING_SUBMIT", StringComparison.Ordinal))
            {
                return OrderIntentReservationStatus.AlreadyFinalizedOrClaimed;
            }

            var nowUtc = clock.GetUtcNow();
            if (existing.UpdatedAtUtc > nowUtc - PendingSubmitLeaseDuration || brokerClient is null)
            {
                return OrderIntentReservationStatus.AlreadyFinalizedOrClaimed;
            }

            var reconciled = await TryClaimAndReconcileExpiredRowAsync(existing, brokerClient, clock, cancellationToken).ConfigureAwait(false);
            return reconciled == BrokerOrderLookupOutcome.Found
                ? OrderIntentReservationStatus.ReconciledExistingReceipt
                : OrderIntentReservationStatus.AlreadyFinalizedOrClaimed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(
                "Write-ahead PENDING_SUBMIT reservation failed for {ClientOrderId} ({ErrorType}); failing closed without broker call.",
                pendingIntentRow.ClientOrderId,
                ex.GetType().Name);
            return OrderIntentReservationStatus.FailedClosed;
        }
    }

    public async Task CompleteOrderIntentAsync(OrderRow completedRow)
    {
        ArgumentNullException.ThrowIfNull(completedRow);

        // Intentionally uses CancellationToken.None with a bounded timeout so host shutdown/request cancellation
        // cannot abort recording an accepted broker order receipt, and strictly guards WHERE status = 'PENDING_SUBMIT'
        // so an already-persisted receipt can NEVER be overwritten by a late duplicate/failure completion.
        using var completionCts = new CancellationTokenSource(PostSubmitCompletionTimeout);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(completionCts.Token).ConfigureAwait(false);
            db.Database.SetCommandTimeout(PostSubmitCompletionTimeout);

            await db.Orders
                .Where(o => o.ClientOrderId == completedRow.ClientOrderId && o.Status == "PENDING_SUBMIT")
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

    private async Task<BrokerOrderLookupOutcome> TryClaimAndReconcileExpiredRowAsync(
        OrderRow expired,
        IAlpacaPaperBrokerClient brokerClient,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var claimNow = clock.GetUtcNow();
        await using (var claimDb = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var claimed = await claimDb.Orders
                .Where(o =>
                    o.ClientOrderId == expired.ClientOrderId &&
                    o.Status == "PENDING_SUBMIT" &&
                    o.UpdatedAtUtc == expired.UpdatedAtUtc)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(o => o.UpdatedAtUtc, claimNow),
                    cancellationToken)
                .ConfigureAwait(false);

            if (claimed != 1)
            {
                return BrokerOrderLookupOutcome.AmbiguousFailure;
            }
        }

        var lookup = await brokerClient
            .GetOrderByClientOrderIdAsync(expired.ClientOrderId, cancellationToken)
            .ConfigureAwait(false);

        switch (lookup.Outcome)
        {
            case BrokerOrderLookupOutcome.Found when lookup.Receipt is not null:
            {
                var reconciledStatus = !string.IsNullOrWhiteSpace(lookup.Receipt.Status)
                    ? lookup.Receipt.Status.Trim().ToUpperInvariant()
                    : "SUBMITTED";
                await CompleteOrderIntentAsync(expired with
                {
                    AlpacaOrderId = lookup.Receipt.OrderId.Trim(),
                    Status = reconciledStatus,
                    FilledAvgPrice = lookup.Receipt.FilledAvgPrice,
                    SubmittedAtUtc = lookup.Receipt.SubmittedAtUtc ?? claimNow,
                    UpdatedAtUtc = clock.GetUtcNow()
                }).ConfigureAwait(false);
                return BrokerOrderLookupOutcome.Found;
            }

            case BrokerOrderLookupOutcome.NotFound:
                // Definitively never received by Alpaca (HTTP 404): clear stale reservation without blind resubmission.
                await CompleteOrderIntentAsync(expired with
                {
                    AlpacaOrderId = null,
                    Status = "FAILED_BROKER",
                    UpdatedAtUtc = clock.GetUtcNow()
                }).ConfigureAwait(false);
                return BrokerOrderLookupOutcome.NotFound;

            default:
                // Ambiguous lookup error: leave PENDING_SUBMIT intact fail-closed.
                return BrokerOrderLookupOutcome.AmbiguousFailure;
        }
    }
}
