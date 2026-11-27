# Spec 001 — SentiX Masterplan, ADRs, Pipeline & External Limits

> Single Source of Truth for SentiX architecture, ADRs, and limits (Stand 2026-11-26).

## 1. Mission & Strategy

- **Project:** SentiX (`sentimentx.dev`)
- **Objective:** 24/7 automated event-driven **Post-News-Drift** trading system (holding horizon: hours up to 5 trading days) for liquid US equities and sector ETFs via the **Alpaca Trading API**.
- **Focus:** Fast-Consumer advantage via persistent WebSockets (never polling), local C++20/23 IPC Fast-Path (< 30 ms), US-East deployment (Manassas, VA, ~5–15 ms to Alpaca), and in-memory deterministic guardrails. Not microsecond HFT.
- **Launch Constraints:** Strict **Alpaca Paper Trading** + **Long-Only** (`AllowShorting = false`, whole shares only for Alpaca Bracket Orders) benchmarked daily against **SPY Buy-and-Hold** for 4–8 weeks before considering live capital.
- **Two Separate Swarms:**
  1. **Dev-Swarm (Build-Time):** Antigravity (Gemini/Opus), Claude Code, ChatGPT Codex.
  2. **Decision-Swarm (Run-Time):** Internal 5-agent committee (`FactAnalyst`, `BullAgent`, `BearAgent`, `SoftRiskAgent`, `JudgeAgent`).

## 2. Architecture Decision Records (ADRs)

- **ADR-01 (Target Platform):** Exclusively **Headless Linux** (`Debian 13` / `Ubuntu 24.04 LTS`, `x86_64`). Zero Windows code, zero C++/CLI. Managed via ASP.NET Core + React/TypeScript.
- **ADR-02 (Target Host & Deployment):** Single-Instance portable **Docker Compose** stack targeting netcup Root-Server RS 1000 G12.5 (Debian 13, 4 EPYC cores, 8 GB RAM, 128 GB NVMe) in Manassas, VA. Dashboard binds strictly to `127.0.0.1:8080` (Tailscale/SSH tunnel access).
- **ADR-03 (Hybrid Stack):** `.NET 10 (C#)` for orchestration, WebSockets, Decision-Swarm, EF Core persistence, Hard-Risk, Execution, Web API/SignalR; pure `C++20/23` for Fast-Path filtering, HTML stripping, local quantized ONNX-FinBERT (`INT8`), and backtesting.
- **ADR-04 (Process Isolation & IPC):** Separate OS processes over **Unix Domain Socket** (`/run/sentix/native.sock` on `tmpfs`) so native faults cannot compromise `SentiX.Risk` or `SentiX.Host`.
- **ADR-05 (Database):** **PostgreSQL 17** with append-only audit trail (`market_events`, `fast_path_results`, `decision_debates`, `risk_evaluations`, `orders`, `daily_benchmarks`, `scheduled_events`, `system_state`).
- **ADR-06 (Two-Tier Risk Model):**
  - Tier 1 (**Soft Risk**): AI `SoftRiskAgent` evaluates macro/earnings timing, contradictions, and sector crowding.
  - Tier 2 (**Hard Risk**): Deterministic `SentiX.Risk` (**0 % AI**, 100 % branch coverage) directly before execution.
- **ADR-07 (Runtime Models):** Local ONNX-FinBERT (`INT8` in C++, drops ~98 % noise) + cloud LLM APIs behind replaceable `ILlmClient` (fast tier for Fact/Bull/Bear/SoftRisk, strong tier for Judge).

## 3. Six-Stage Runtime Pipeline & Phase Plan

1. **Ingestion (`SentiX.Ingestion`, .NET 10):** Alpaca News WS (`v1beta1/news`), Alpaca Quotes (`v2/iex`), SEC EDGAR (Phase 2), X Filtered Stream (Phase 2), Calendar Sync (Phase 2/3).
2. **Fast-Path (`src/native`, C++20/23 IPC):** HTML stripping + `summary` fallback, SHA-256 deduplication, watchlist/ETF mapping, sentiment scoring (< 30 ms).
3. **Decision-Swarm (`SentiX.DecisionSwarm`, .NET 10):** `FactAnalyst` -> parallel `[BullAgent, BearAgent, SoftRiskAgent]` -> `JudgeAgent` -> `TradeProposal`.
4. **Hard-Risk-Manager (`SentiX.Risk`, .NET 10):** 13 deterministic rules in RAM (< 1 ms).
5. **Execution (`SentiX.Execution`, .NET 10):** Idempotent bracket/limit orders (whole shares) via Alpaca Paper REST.
6. **Slow-Path & UI (`SentiX.Infrastructure` + `src/web`):** PostgreSQL 17 audit trail, daily SPY benchmark, SignalR dashboard & Kill-Switch.

### Delivery Phases
- **Phase 1 (Completed):** Monorepo, Linux CI, PostgreSQL 17 schema, C++20/23 IPC daemon (HTML stripping + `summary` fallback + deterministic mock scorer), Hard-Risk-Manager (`0 % AI`, `100 %` branch coverage, Long-Only), `SentiX.NativeBridge`, Alpaca News Collector (`v1beta1/news`, `0` trades), optional X adapter skeleton (graceful skip when `X_BEARER_TOKEN` is empty), minimal React web shell, Dockerfiles, IPC integration test.
- **Phase 2 (Completed):** Real ONNX-FinBERT (`INT8`) in C++20/23 via runtime `dlopen`, SEC EDGAR 8-K + X API v2 Filtered Stream (`SentiX2026`), Finnhub 14-day calendar sync (`scheduled_events`), Alpaca `v2/iex` Quotes collector & `IMarketQuoteCache`.
- **Phase 3 (Active — `specs/007`):** Decision-Swarm 5-agent orchestration (`SentiX.DecisionSwarm`) + shadow `HardRiskManager` evaluation + PostgreSQL debate persistence + React shadow dashboard (`tradingEnabled = false`).
- **Phase 4:** Alpaca Paper bracket execution + UI Kill-Switch + 4–8 week live paper benchmark vs `SPY`.

## 4. External API Limits & Configuration Contracts

### Alpaca Basic (Free) Paper Trading
- **Endpoints:**
  - Trading REST: `https://paper-api.alpaca.markets`
  - News WebSocket: `wss://stream.data.alpaca.markets/v1beta1/news`
  - Market Data WebSocket (Quotes/Trades): `wss://stream.data.alpaca.markets/v2/iex` (**STRICTLY NEVER `v2/sip`**)
- **Limits Enforced in Code:**
  - Maximum **30 equities symbols** (`config/universe.json` -> `maxEquitiesWsSymbols: 30`, validated at startup across unique watchlist + sector ETFs + `benchmarkTicker`; default preset = 10 + 8 + 1 = 19).
  - Maximum **1 active WebSocket connection** per endpoint (strict Single-Instance host).
  - Maximum **200 REST requests/min** (exponential backoff on HTTP `429`).
- **Environment Variables:** `ALPACA_API_KEY` (`Alpaca__ApiKey`), `ALPACA_SECRET_KEY` (`Alpaca__SecretKey`), `ALPACA_PAPER_MODE` (`Alpaca__PaperMode=true`), `ALPACA_ALLOW_SHORTING` (`Alpaca__AllowShorting=false`).
- **Alpaca CLI (`alpaca`):** Host-side Ops/diagnostics/verification only (`alpaca doctor`, `alpaca account get --trace`, `alpaca order cancel-all`, `alpaca position close-all`). **Forbidden** in runtime hot-path.

### X (Twitter) API v2 (`SentiX2026` Pay-per-Use)
- **Auth:** OAuth 2.0 App-Only Read-Only via `X_BEARER_TOKEN` (`X__BearerToken`). If empty/whitespace -> **graceful skip** (log informational message, no error/crash).
- **Endpoint & Rules:** `GET https://api.x.com/2/tweets/search/stream`, rules synced via `POST /2/tweets/search/stream/rules` strictly from `config/universe.json` (`source == "X_ALLOWLIST"`): `(from:handle1 OR from:handle2 ...) -is:retweet -is:reply`.
- **Daily Cap:** `X_DAILY_POST_CAP` (default `150` posts/UTC day). Disconnecting cleanly until `00:00 UTC` once reached.
- **Normalization:** Maps to `MarketEvent` with `source = "X_ALLOWLIST"`, `author = "@handle"`, `headline = text`, `summary = ""`, `body = ""`.

### Earnings & Macro Calendar (Phase 2/3)
- `FINNHUB_API_KEY` (`Finnhub__ApiKey`), empty = **graceful skip**. Daily sync at `06:00 ET` populates `scheduled_events` (14-day lookahead).
