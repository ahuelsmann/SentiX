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

## 3. Host Wiring, Persisted Kill-Switch & Cockpit Contract (`T-14`)

### 3.1 Persisted Kill-Switch Store (`ITradingStateStore` / `EfCoreTradingStateStore`)
- `ITradingStateStore` extends `ITradingStateProvider` (`GetCurrentStateAsync` / `ReadTradingStateAsync`) and adds operator transition operations (`HaltManualAsync`, `ResumeManualAsync`) backed by PostgreSQL `system_state` (`id = 1`, Spec 005).
- **Fail-Closed State Reads:**
  - Queries `system_state` (`id == 1`, `AsNoTracking()`).
  - Valid persisted strings (`"ACTIVE"`, `"HALTED_MANUAL"`, `"HALTED_DAILY_LOSS"`, `"HALTED_SYSTEM_ERROR"`) return `TradingStateReadResult(Success: true, State: ...)`.
  - Missing row (`id = 1`), empty/unknown string, unconfigured database (`UnconfiguredTradingStateStore`), or database exception returns `TradingStateReadResult(Success: false, State: TradingState.HaltedSystemError, ErrorReason: ...)` without throwing unhandled exceptions.
- **Operator State Transitions:**
  - `HaltManualAsync(updatedBy, ct)`: Sanitizes `updatedBy` (trimmed, non-empty, `<= 64` chars) and atomically upserts `system_state (id = 1)` to `trading_state = 'HALTED_MANUAL'`, `updated_at_utc = clock.GetUtcNow()`. Emergency manual halt is permitted from **any** state whenever PostgreSQL is reachable.
  - `ResumeManualAsync(updatedBy, ct)`: Atomically transitions `system_state (id = 1)` to `'ACTIVE'` **only** when `trading_state` is `'HALTED_MANUAL'` (or idempotently `'ACTIVE'`). If `trading_state` is `'HALTED_DAILY_LOSS'`, `'HALTED_SYSTEM_ERROR'`, missing, or unknown, it fails closed with `CannotResumeNonManualHalt` (`409 Conflict`) so an operator UI click can never bypass a daily-loss circuit breaker or system-error halt.

### 3.2 Startup Database Readiness (`DatabaseStartupInitializer`)
- `SentiX.Host` registers a startup `IHostedService` ahead of market collectors that invokes `IMarketEventPipeline.EnsureReadyAsync` when `ConnectionStrings:Default` is configured.
- Applies EF Core migrations and seeds `system_state (id = 1, 'ACTIVE')` on fresh PostgreSQL 17 deployments (`docker compose up`) before `/api/overview` or collector workers run, while catching and logging transient startup connectivity errors without crashing the host process.

### 3.3 `MarketEventPipeline` Phase 4 Order Execution & `orders` Persistence
- `SentiX.Risk` remains 100 % untouched (`0` changes).
- After `decision_debates` and `risk_evaluations` are committed in `MarketEventPipeline`:
  - When `AlpacaPaperOptions.TradingEnabled == false` (default Shadow Mode), zero calls are made to `IPaperExecutionEngine` and zero rows are written to `orders`.
  - When `AlpacaPaperOptions.TradingEnabled == true`, `riskDecision.Approved == true`, `proposal.Action != HOLD`, and `riskDecision.ClientOrderId` is non-empty, `MarketEventPipeline` constructs `PaperOrderExecutionRequest.FromRiskDecision(...)` using the exact `TradeProposal` and quote `entryPrice` evaluated by `HardRiskManager`.
  - If `orders` does not already contain `ClientOrderId`, `IPaperExecutionEngine.ExecuteAsync` is invoked and the resulting `OrderRow` is persisted idempotently (`SUBMITTED` / broker status, `DUPLICATE`, `REJECTED_PREFLIGHT`, or `FAILED_BROKER`) in an isolated post-debate unit of work so unexpected persistence errors never roll back or corrupt the committed debate/risk audit trail.

### 3.4 Loopback HTTP Telemetry, Orders & Kill-Switch API (`TelemetryEndpoints`)
- All endpoints emit `Cache-Control: no-store`.
- `GET /health` & `GET /api/overview`:
  - Report `phase = 4`, `tradingState` (`"ACTIVE"`, `"HALTED_MANUAL"`, `"HALTED_DAILY_LOSS"`, `"HALTED_SYSTEM_ERROR"`, or `"UNAVAILABLE"`), `paperSwitchArmed` (`AlpacaPaperOptions.TradingEnabled`), `paperExecutionConfigured` (`AlpacaPaperOptions.IsConfigured`), and effective `tradingEnabled` / `orderExecutionEnabled` (`paperSwitchArmed && tradingState == "ACTIVE"`).
  - `/api/overview` additionally includes `counts.ordersTotal`, `counts.ordersSubmitted`, `counts.ordersRejected`, and `latestOrders` (newest 10 rows).
- `GET /api/orders?limit=25`:
  - Returns `{ items, databaseConfigured }` ordered by `updated_at_utc DESC, submitted_at_utc DESC, client_order_id ASC` (`limit` clamped to `1..100`).
- `POST /api/system-state/halt` & `POST /api/system-state/resume`:
  - Reject requests carrying a non-loopback `Origin` header (host other than `localhost` or `127.0.0.1`) with `403 Forbidden` (`{ error = "forbidden_origin" }`).
  - Require exact ordinal header `X-SentiX-Confirm: HALT` (on `/halt`) or `X-SentiX-Confirm: RESUME` (on `/resume`); missing or mismatched values return `400 Bad Request` (`{ error = "missing_confirmation_header" }`).
  - Return `503 Service Unavailable` when PostgreSQL is unconfigured (`database_not_configured`) or unreachable (`database_unavailable`).
  - `/halt` transitions `system_state` to `HALTED_MANUAL` (`200 OK`).
  - `/resume` transitions `HALTED_MANUAL` (or `ACTIVE`) to `ACTIVE` (`200 OK`), or fails closed with `409 Conflict` (`{ error = "cannot_resume_state", code = "CannotResumeNonManualHalt", tradingState }`) on `HALTED_DAILY_LOSS` / `HALTED_SYSTEM_ERROR`.

### 3.5 Phase 4 Cockpit UI (`src/web`)
- Displays Phase 4 `Paper & shadow cockpit` status, distinguishing calm default Shadow Mode (`Disabled (Shadow Only)`) from armed paper mode (`Paper Armed (paper-api.alpaca.markets)`) and halted states (`Kill-Switch Engaged`).
- Provides one-click emergency `Engage Kill-Switch (HALT)` when `ACTIVE`, a two-step `Resume to Active` -> `Confirm Resume` / `Cancel` flow when `HALTED_MANUAL`, a circuit-breaker lock notice when `HALTED_DAILY_LOSS` / `HALTED_SYSTEM_ERROR`, and an accessible `#orders` audit table.
