# Spec 008 — Phase 4 Alpaca Paper Execution Engine (`SentiX.Execution`, `T-13`)

> Single Source of Truth for Phase 4 (`T-13`) Alpaca Paper Execution. Extends [`001-masterplan.md`](./001-masterplan.md), [`004-risk-guardrails.md`](./004-risk-guardrails.md), [`005-database-schema.md`](./005-database-schema.md), and [`007-phase3-decision-swarm.md`](./007-phase3-decision-swarm.md).

## 1. Non-Negotiable Safety Invariants (Fail-Closed)

1. **Strict Paper-Only Endpoint (`https://paper-api.alpaca.markets`):**
   - `SentiX.Execution` MUST ONLY dispatch orders to `https://paper-api.alpaca.markets/v2/orders`.
   - Any non-paper or unsafe URL — including `https://api.alpaca.markets` (live trading), `http://paper-api.alpaca.markets` (non-TLS), loopback/arbitrary hosts, relative paths, non-default ports, userinfo (`@`), query strings (`?`), fragments (`#`), or non-root base paths — MUST be rejected **both** during `AlpacaPaperOptions` validation **and** immediately prior to every `HttpClient` dispatch in `AlpacaPaperBrokerClient`.
   - `AllowAutoRedirect` MUST be disabled on managed `HttpClient` handlers, and `3xx` responses MUST fail closed without following redirects.
2. **Safe Default `TradingEnabled = false`:**
   - `AlpacaPaperOptions.TradingEnabled` MUST default to `false`.
   - When `TradingEnabled == false`, `PaperExecutionEngine` (and `AlpacaPaperBrokerClient` as defense-in-depth) MUST fail closed with `ExecutionRejectionCode.TradingDisabled` and perform **zero** broker HTTP calls.
3. **Kill-Switch & Persisted `TradingState` Enforcement:**
   - `PaperExecutionEngine` queries `ITradingStateProvider` (backed in production by PostgreSQL `system_state`) before any order submission.
   - Only `TradingState.Active` permits execution. Any halted state (`HaltedManual`, `HaltedDailyLoss`, `HaltedSystemError`), unknown/empty persisted string, or state-read failure/exception MUST fail closed (`KillSwitchActive` or `TradingStateUnreadable`) with zero broker HTTP calls.
   - `RiskDecision.EffectiveTradingState` MUST also equal `TradingState.Active`.
4. **Hard-Risk Approval Gate (`SentiX.Risk` Untouched):**
   - `SentiX.Risk` remains strictly zero-dependency, 0 % AI, and untouched.
   - `PaperExecutionEngine` accepts `SentiX.Risk.TradeProposal` and `SentiX.Risk.RiskDecision` and rejects any decision where `Approved == false`, `Action == TradeAction.Hold`, or `CalculatedQuantity < 1`.
5. **Long-Only & Whole-Share Bracket Invariants:**
   - `AlpacaPaperOptions.AllowShorting` MUST default to `false`; enabling shorting fails closed (`ShortingNotAllowed`) in Phase 4.
   - `TradeAction.Sell` is rejected (`ShortingNotAllowed`) unless `IsClosingLongPosition == true` and `OpenLongQuantity >= Quantity`.
   - `Quantity` MUST be a positive whole integer (`>= 1`).
   - Bracket entry orders (`TradeAction.Buy` / `ExecutionOrderClass.Bracket`) MUST specify positive `EntryPrice` (and `LimitPrice` when `OrderType == Limit`), `StopLossPrice`, and `TakeProfitPrice` satisfying:
     - `StopLossPrice > 0m && StopLossPrice < EntryPrice`
     - `TakeProfitPrice > EntryPrice`
     - `RiskDecision.ClampedStopLossPct > 0m` and `RiskDecision.ClampedTakeProfitPct > 0m` (when clamped percentages are supplied).
6. **Deterministic Idempotency (`client_order_id`):**
   - Every order uses the Spec 004 deterministic `client_order_id`:
     `SX-{etDate:yyyyMMdd}-{UPPER_TICKER}-{BUY|SELL}-{sha256LowerHex8}` (`<= 48` chars, generated via `SentiX.Risk.ClientOrderIdGenerator`).
   - `PaperExecutionEngine` always recomputes the canonical `client_order_id` from `(Proposal.EventId, Proposal.ProposalId, Ticker, Action, EvaluatedAtUtc)` and requires `RiskDecision.ClientOrderId` to be non-empty and match the canonical ID (`StringComparison.Ordinal`). If `RiskDecision.ClientOrderId` is null, blank, or contradicts the canonical ID for that proposal and ET trading date, execution MUST fail closed (`InvalidClientOrderId`) with zero broker calls.
   - Re-submitting the same `(EventId, ProposalId, Ticker, Action)` on the same `America/New_York` trading date produces the identical `client_order_id`.
7. **Broker Failure & Duplicate Handling (Non-Throwing):**
   - HTTP `409 Conflict` and `422 Unprocessable Entity` duplicate `client_order_id` responses map cleanly to `ExecutionOutcomeStatus.DuplicateOrder` (`IsDuplicate = true`, `IsRetryable = false`).
   - HTTP `429 Too Many Requests`, `5xx`, timeouts, network/stream errors, oversized bodies (`> 256 KiB`), empty/null JSON, malformed JSON, or missing/mismatched receipt fields (`id`, `client_order_id`, `symbol`, `status`) fail closed cleanly into structured `PaperExecutionResult` / `BrokerSubmissionResult` outcomes without throwing unhandled exceptions.
   - `FailureReason` MUST contain only structured HTTP status codes and static allowlisted Alpaca numeric error codes — never arbitrary strings, slugs, or free-form text from broker JSON.
8. **No `alpaca` CLI in Hot-Path:**
   - All order submissions use `HttpClient` against Alpaca Paper REST API v2; invoking the `alpaca` CLI in runtime code is forbidden.

## 2. Public Contract (`namespace SentiX.Execution`)

- **`AlpacaPaperOptions`:** Configuration & strict `https://paper-api.alpaca.markets` validator (`TradingEnabled = false`, `PaperMode = true`, `AllowShorting = false`, `BaseUrl`, `ApiKey`, `SecretKey`, `RequestTimeout`, `FromConfiguration`, `TryResolvePaperBaseUri`, `TryValidate`). `ToString()` redacts credentials.
- **`ExecutionModels.cs`:**
  - `ExecutionOrderType` (`Limit`, `Market`), `ExecutionTimeInForce` (`Day`, `Gtc`), `ExecutionOrderClass` (`Bracket`, `Simple`)
  - `ExecutionOutcomeStatus` (`Submitted`, `RejectedPreFlight`, `DuplicateOrder`, `FailedBroker`)
  - `ExecutionRejectionCode` (granular non-throwing failure codes for every pre-flight and broker guard)
  - `ITradingStateProvider`, `TradingStateReadResult`, `StaticTradingStateProvider`, `SystemStateParser`
  - `PaperOrderExecutionRequest` (including `FromRiskDecision(...)` bracket price derivation using `MidpointRounding.AwayFromZero`)
  - `AlpacaBracketOrderRequest`, `AlpacaOrderReceipt`, `BrokerSubmissionResult`, `PaperExecutionResult`
- **`IAlpacaPaperBrokerClient` & `AlpacaPaperBrokerClient`:**
  - `Task<BrokerSubmissionResult> SubmitOrderAsync(AlpacaBracketOrderRequest order, CancellationToken cancellationToken)`
  - Uses injected `HttpClient`, enforces `https://paper-api.alpaca.markets/v2/orders` before every `SendAsync`, sets `APCA-API-KEY-ID` / `APCA-API-SECRET-KEY`, enforces `MaxResponseBytes` (`256 KiB`), and maps all HTTP/JSON/network outcomes deterministically.
- **`PaperExecutionEngine`:**
  - `Task<PaperExecutionResult> ExecuteAsync(PaperOrderExecutionRequest request, CancellationToken cancellationToken)`
  - Enforces all pre-flight safety invariants in deterministic order before calling `IAlpacaPaperBrokerClient`.
- **`ExecutionServiceCollectionExtensions`:**
  - `AddSentiXPaperExecution(IConfiguration)` registers `AlpacaPaperOptions`, `ITradingStateProvider` (default fail-closed `HaltedSystemError` unless a persisted provider is registered), `IAlpacaPaperBrokerClient` with `AllowAutoRedirect = false`, and `IPaperExecutionEngine` / `PaperExecutionEngine`.
