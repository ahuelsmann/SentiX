# Spec 006 — Phase 2 Ingestion Contracts (`T-08`)

> Phase 2 adds real-time Alpaca `v2/iex` quotes cache, SEC EDGAR 8-K filings ingestion, and Finnhub earnings/macro calendar synchronization into `scheduled_events`.

## 1. SEC EDGAR 8-K Collector (`SecEdgarCollector`)

- **Configuration:**
  - `SEC_USER_AGENT` (`Sec:UserAgent` / `SecOptions.UserAgent`): e.g. `"SentiX sentimentx.dev contact@example.com"`.
  - `SEC_POLL_INTERVAL_SECONDS` (`Sec:PollIntervalSeconds`, default `30`).
  - If `UserAgent` is empty/whitespace (or `"disabled"`), **graceful skip**: log an informational message and exit without error.
- **Compliance & Rate Limit:**
  - Always send a valid `User-Agent` header on SEC requests.
  - Strict rate limit `<= 2 requests/second` (exponential backoff on HTTP `429` / `5xx`).
- **Filtering & Normalization:**
  - Parses SEC EDGAR current-events / Atom 8-K feed (`https://www.sec.gov/cgi-bin/browse-edgar?action=getcurrent&type=8-K&owner=include&count=40&output=atom` or injectable feed endpoint).
  - Extracts accession number, CIK, company name/symbol, form type, filing timestamp (`publishedAtUtc`), title/summary, and normalized `sourceTickers` matched against `config/universe.json`.
  - Maps to `MarketEvent`:
    - `eventId`: `"sec-edgar-{accessionNumber}"`
    - `source`: `"SEC_EDGAR"`
    - `author`: `"SEC EDGAR"`
    - `headline` / `summary` / `body`: sanitized filing title & item description
  - Forwards through `IMarketEventPipeline.ProcessAsync(...)` (which runs `sentix-native` dedup + scoring + persists `market_events` / `fast_path_results`; **zero trades in Phase 2**).

## 2. Finnhub Earnings & Macro Calendar Sync (`ScheduledEventsSyncWorker`)

- **Configuration:**
  - `FINNHUB_API_KEY` (`Finnhub:ApiKey` / `FinnhubOptions.ApiKey`). If empty/whitespace -> **graceful skip** (log informational message, exit cleanly).
- **Schedule:**
  - Runs an initial sync on startup (once PostgreSQL schema is ready) and then daily at **`06:00 America/New_York`** using `TimeProvider` + Eastern time zone (clean DST handling).
- **Persistence (`scheduled_events`):**
  - Fetches 14-day lookahead earnings calendar (`/api/v1/calendar/earnings`) restricted to unique watchlist tickers in `config/universe.json`, plus US macro/economic calendar if provided.
  - Upserts/deduplicates idempotently in `scheduled_events` (keyed by `(event_type, ticker, title, scheduled_at_utc, source)`) so daily re-syncs never create duplicate rows.

## 3. Alpaca `v2/iex` Market Quotes Collector (`AlpacaQuotesCollector` & `IMarketQuoteCache`)

- **Endpoint & Plan Limits (Zero `v2/sip`!):**
  - Endpoint: `wss://stream.data.alpaca.markets/v2/iex` (**NEVER `v2/sip`**).
  - Single-Instance WebSocket connection.
  - Startup validates total unique symbols (`watchlist` + `sectorEtfs` + `benchmarkTicker`) `<= 30` (matching `config/universe.json` `maxEquitiesWsSymbols: 30`).
  - If `ALPACA_API_KEY` or `ALPACA_SECRET_KEY` is missing/empty -> **graceful skip** (log info and exit cleanly).
- **Quote Cache (`IMarketQuoteCache`):**
  - Thread-safe in-memory snapshot cache keyed case-insensitively by ticker symbol:
    `MarketQuoteSnapshot(string Symbol, decimal BidPrice, decimal AskPrice, decimal MidPrice, double SpreadBps, DateTimeOffset TimestampUtc)`.
  - Processes Alpaca `"q"` (quote) messages where `S` = symbol, `bp` = bid price, `ap` = ask price, `t` = RFC3339 timestamp; ignores invalid/non-positive quotes safely.

## 4. Host Health & Phase Status (`SentiX.Host`)

- `/health` reports:
  - `status: "ok"`
  - `native: "ready" | "starting" | "degraded" | "unavailable"`
  - `phase: 2`
  - `tradingEnabled: false`
