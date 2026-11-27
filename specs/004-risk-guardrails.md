# Spec 004 — Deterministic Hard-Risk-Manager (Tier 2 Risk, `SentiX.Risk`, 0 % AI)

> Governs `src/dotnet/SentiX.Risk/` and `tests/SentiX.Risk.Tests/`. Every rule below is mandatory and deterministic.

## 1. Architectural & Purity Constraints (Non-Negotiable)

- **Assembly Boundary:** `src/dotnet/SentiX.Risk/SentiX.Risk.csproj` (`net10.0`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`) MUST remain a **zero-dependency pure assembly** with NO `<ProjectReference>` entries and NO runtime NuGet dependencies.
- **0 % AI / Zero Ambient Side Effects:**
  - Zero references to `SentiX.DecisionSwarm`, LLM SDKs, `HttpClient`, file/network I/O, or random number generators.
  - Zero calls to `DateTime.UtcNow`, `DateTime.Now`, or `DateTimeOffset.UtcNow`; all time decisions use the injected `MarketClock`.
- **100 % Branch Coverage:** `tests/SentiX.Risk.Tests` MUST achieve `100 %` line and branch coverage (`branch-rate == 1.0`) measured via Coverlet (`XPlat Code Coverage`).

## 2. Canonical Public C# Contract (`namespace SentiX.Risk`)

```csharp
namespace SentiX.Risk;

public enum TradingState
{
    Active,
    HaltedDailyLoss,
    HaltedManual,
    HaltedSystemError
}

public enum TradeAction
{
    Hold,
    Buy,
    Sell
}

public enum InstrumentType
{
    Equity,
    SectorEtf
}

public enum RiskViolationCode
{
    KillSwitchActive,
    NonActionableProposal,
    ShortingNotAllowed,
    ConfidenceTooLow,
    TickerNotAllowed,
    OutsideTradingHours,
    TickerCooldownActive,
    DuplicatePosition,
    MaxOpenPositions,
    DailyLossLimitReached,
    SectorExposureExceeded,
    InsufficientBuyingPower,
    InvalidBracketBounds
}

public sealed record TradeProposal(
    string ProposalId,
    string EventId,
    TradeAction Action,
    string Ticker,
    InstrumentType InstrumentType,
    double Confidence,
    int ExpectedHorizonHours,
    decimal SuggestedStopLossPct,
    decimal SuggestedTakeProfitPct,
    string Rationale,
    string DissentSummary);

public sealed record OpenPosition(
    string Ticker,
    string Sector,
    int Quantity,
    decimal MarketValue,
    decimal CurrentPrice);

public sealed record PendingOrder(
    string ClientOrderId,
    string Ticker,
    TradeAction Side,
    int Quantity);

public sealed record PortfolioSnapshot(
    TradingState TradingState,
    decimal Equity,
    decimal BuyingPower,
    double DailyPnLPct,
    decimal CurrentAskPrice,
    string TargetSector,
    IReadOnlyList<OpenPosition> OpenPositions,
    IReadOnlyList<PendingOrder> PendingOrders,
    IReadOnlyDictionary<string, DateTimeOffset> LastTradeTimesUtc);

public sealed record MarketClock(
    DateTimeOffset UtcNow,
    bool IsUsMarketHoliday = false);

public sealed record RiskPolicy
{
    public bool AllowShorting { get; init; } = false;
    public double MinConfidence { get; init; } = 0.70;
    public IReadOnlySet<string> AllowedTickers { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public int CooldownMinutes { get; init; } = 60;
    public int MaxOpenPositions { get; init; } = 5;
    public double MaxDailyLossPct { get; init; } = -0.02;
    public decimal MaxSectorExposurePct { get; init; } = 0.25m;
    public decimal MaxPositionEquityPct { get; init; } = 0.05m;
    public decimal MaxBuyingPowerUsagePct { get; init; } = 0.95m;
    public decimal MinOrderNotional { get; init; } = 100m;
    public decimal MinStopLossPct { get; init; } = 0.015m;
    public decimal MaxStopLossPct { get; init; } = 0.050m;
    public decimal DefaultStopLossPct { get; init; } = 0.030m;
    public decimal MinTakeProfitPct { get; init; } = 0.030m;
    public decimal MaxTakeProfitPct { get; init; } = 0.150m;
    public decimal DefaultTakeProfitPct { get; init; } = 0.060m;
    public decimal MinRewardToRiskRatio { get; init; } = 1.5m;
}

public sealed record RiskViolation(RiskViolationCode Code, string Message);

public sealed record RiskDecision(
    bool Approved,
    TradingState EffectiveTradingState,
    IReadOnlyList<RiskViolation> Violations,
    int CalculatedQuantity,
    decimal? ClampedStopLossPct,
    decimal? ClampedTakeProfitPct,
    string? ClientOrderId);
```

## 3. Pure Evaluation API & 13 Mandatory Rules

- Entry point: `HardRiskManager.Evaluate(TradeProposal proposal, PortfolioSnapshot snapshot, MarketClock clock, RiskPolicy policy)`.
- **Full Audit Semantics:** Evaluate ALL applicable rules cleanly and collect every violation into `RiskDecision.Violations` (do not short-circuit on the first violation).
- **Timezone Handling:** Convert `clock.UtcNow` to `America/New_York` (`TimeZoneInfo.FindSystemTimeZoneById("America/New_York")` on Linux) so Daylight Saving Time (`EST` `-05:00` vs `EDT` `-04:00`) is handled automatically.

### Rule Definitions

1. **`KILL_SWITCH_ACTIVE` (`KillSwitchActive`):**
   - When `snapshot.TradingState != TradingState.Active`, new entries (`TradeAction.Buy`, or `TradeAction.Sell` only if short-opening were ever enabled) MUST be rejected with `KillSwitchActive`.
   - **Closing Exits Remain Permitted:** A `TradeAction.Sell` closing an existing Long position (`Quantity > 0`) MUST NOT be blocked by `KillSwitchActive`, `DailyLossLimitReached`, `DuplicatePosition`, `MaxOpenPositions`, `SectorExposureExceeded`, or `InsufficientBuyingPower`.
2. **`NON_ACTIONABLE_PROPOSAL` (`NonActionableProposal`):**
   - `proposal.Action` MUST be `TradeAction.Buy` or `TradeAction.Sell` (`TradeAction.Hold` -> violation).
3. **`SHORTING_NOT_ALLOWED` (`ShortingNotAllowed`):**
   - If `proposal.Action == TradeAction.Sell` and `policy.AllowShorting == false`, there MUST be an open Long position (`openPos.Quantity > 0`) matching `proposal.Ticker` (case-insensitive). Otherwise reject with `ShortingNotAllowed`.
4. **`CONFIDENCE_TOO_LOW` (`ConfidenceTooLow`):**
   - `proposal.Confidence >= policy.MinConfidence` (default `0.70`; `0.6999` fails).
5. **`TICKER_NOT_ALLOWED` (`TickerNotAllowed`):**
   - `proposal.Ticker` must be non-empty and contained in `policy.AllowedTickers` (populated at runtime from `config/universe.json`).
6. **`OUTSIDE_TRADING_HOURS` (`OutsideTradingHours`):**
   - In `America/New_York` local time: `!clock.IsUsMarketHoliday`, weekday (`Monday`..`Friday`), and `09:35:00 <= timeOfDay <= 15:50:00`.
7. **`TICKER_COOLDOWN_ACTIVE` (`TickerCooldownActive`):**
   - If `snapshot.LastTradeTimesUtc` has an entry for `proposal.Ticker` and `(clock.UtcNow - lastTradeTime) < TimeSpan.FromMinutes(policy.CooldownMinutes)` (default `60` min), reject.
8. **`DUPLICATE_POSITION` (`DuplicatePosition`):**
   - On `TradeAction.Buy`: reject if `snapshot.OpenPositions` already contains `proposal.Ticker` OR `snapshot.PendingOrders` already contains `proposal.Ticker`.
9. **`MAX_OPEN_POSITIONS` (`MaxOpenPositions`):**
   - On `TradeAction.Buy`: require `snapshot.OpenPositions.Count < policy.MaxOpenPositions` (default `< 5`).
10. **`DAILY_LOSS_LIMIT_REACHED` (`DailyLossLimitReached`):**
    - On `TradeAction.Buy`: require `snapshot.DailyPnLPct > policy.MaxDailyLossPct` (default `> -0.02`; `-0.02` exactly triggers violation and transitions `EffectiveTradingState` to `TradingState.HaltedDailyLoss`).
11. **`SECTOR_EXPOSURE_EXCEEDED` (`SectorExposureExceeded`):**
    - On `TradeAction.Buy`: existing sector market value for `snapshot.TargetSector` + new target order notional (`Qty * CurrentAskPrice`, or unrounded target notional when `Qty == 0`) divided by `snapshot.Equity` MUST be `<= policy.MaxSectorExposurePct` (`0.25m`, 25 %; reject if `Equity <= 0` or ratio exceeds `0.25m`).
12. **`INSUFFICIENT_BUYING_POWER` (`InsufficientBuyingPower`):**
    - On `TradeAction.Buy`: `TargetNotional = min(snapshot.Equity * policy.MaxPositionEquityPct, snapshot.BuyingPower * policy.MaxBuyingPowerUsagePct)`. Require `snapshot.CurrentAskPrice > 0m`, `Quantity = floor(TargetNotional / snapshot.CurrentAskPrice) >= 1`, `ActualNotional = Quantity * snapshot.CurrentAskPrice >= policy.MinOrderNotional` (`100m`), and `ActualNotional <= snapshot.BuyingPower * policy.MaxBuyingPowerUsagePct`. On closing `TradeAction.Sell`, set `Quantity = existingLongPosition.Quantity` (no `InsufficientBuyingPower` violation).
13. **`INVALID_BRACKET_BOUNDS` (`InvalidBracketBounds`):**
    - Input `SuggestedStopLossPct <= 0m` or `SuggestedTakeProfitPct <= 0m` triggers `InvalidBracketBounds` (or if valid positive inputs, clamp SL to `[policy.MinStopLossPct, policy.MaxStopLossPct]` (`[1.5 %, 5.0 %]`) and TP to `[policy.MinTakeProfitPct, policy.MaxTakeProfitPct]` (`[3.0 %, 15.0 %]`), then verify `clampedTP >= clampedSL * policy.MinRewardToRiskRatio` (`1.5m`), flagging `InvalidBracketBounds` if violated).

## 4. Deterministic `ClientOrderId` & `TradingStateMachine`

- **ClientOrderId (when Approved, `<= 48` chars):**
  `SX-{etDate:yyyyMMdd}-{UPPER_TICKER}-{BUY|SELL}-{sha256LowerHex8}` where `sha256LowerHex8` is the first 8 lowercase hex characters of `SHA256(UTF8(proposal.EventId + proposal.ProposalId))` and `etDate` is the `America/New_York` date of `clock.UtcNow`.
- **`TradingStateMachine` Transitions:**
  - `HaltedDailyLoss` -> resets to `Active` ONLY on a later US trading day at `>= 09:30 ET` (not a weekend/holiday) or explicit manual reset (`IsManualReset = true`).
  - `HaltedManual` -> resets to `Active` ONLY via explicit manual reset (`IsManualReset = true`).
  - `HaltedSystemError` -> resets to `Active` after `>= 30 s` continuous healthy native state (`HealthyDuration >= TimeSpan.FromSeconds(30)`) or explicit manual reset.
