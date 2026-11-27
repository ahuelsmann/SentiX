# Spec 002 — IPC Contract (.NET 10 `SentiX.NativeBridge` <-> C++20/23 `sentix-native`)

> Canonical wire contract between `src/dotnet/SentiX.NativeBridge` and `src/native`.

## 1. Transport, Permissions & Framing

- **Transport:** POSIX Unix Domain Socket (`AF_UNIX`, `SOCK_STREAM`).
- **Socket Path:** Environment variable `SENTIX_SOCKET_PATH` (C++ daemon) / config key `Sentix:NativeSocketPath` (`Sentix__NativeSocketPath`, .NET host). Default: **`/run/sentix/native.sock`**.
- **Lifecycle & Permissions (Server `sentix-native`):**
  - Ensure parent directory exists, `unlink` any stale socket file prior to `bind()`, `listen()`, `chmod` socket permissions to `0660`, and cleanly `unlink` on graceful `SIGINT`/`SIGTERM` shutdown.
  - Strictly Linux POSIX (`<sys/socket.h>`, `<sys/un.h>`). Zero Windows APIs or Named Pipes.
- **Connection Pool (Client `SentiX.NativeBridge`):**
  - Maintain a small persistent connection pool (2–4 sockets).
- **Framing:**
  - `4-byte uint32_t` **Little-Endian** payload byte length `N`, immediately followed by `N` bytes of UTF-8 JSON.
  - **Max Frame Size:** `1_048_576` bytes (`1 MiB`).
  - If `N == 0`: respond with `ERROR` (`INVALID_FRAME`) if valid response channel exists and close connection.
  - If `N > 1_048_576`: immediately respond with `ERROR` (`PAYLOAD_TOO_LARGE`, `retryable: false`) and **close the connection immediately** without reading overflowing bytes into unbounded buffers.

## 2. Timeouts, Reconnect & Fail-Closed Behavior

- **Connect Timeout:** `500 ms`
- **`SCORE_EVENT` Timeout:** `250 ms`
- **`HEALTH_CHECK` Timeout:** `200 ms`
- **`RELOAD_CONFIG` Timeout:** `500 ms`
- **Reconnect Backoff:** Exponential backoff on disconnect/connection failure: `100 ms` -> `250 ms` -> `500 ms` -> capped at `5000 ms` (`5 s`).
- **Fail-Closed Rule (Non-Negotiable):** If `sentix-native` is unreachable, disconnects mid-flight, times out, returns an `ERROR` frame, or reports `status != "READY"`, `SentiX.NativeBridge` MUST return a fail-closed result (`IsFailClosed = true` / `PassToDecisionSwarm = false`) so zero trades can ever be triggered.

## 3. JSON Envelope (`UTF-8`, `camelCase`)

Every request and response uses this envelope:

```json
{
  "protocolVersion": 1,
  "correlationId": "550e8400-e29b-41d4-a716-446655440000",
  "type": "SCORE_EVENT",
  "timestampUtc": "2026-11-26T14:30:05.123Z",
  "payload": {}
}
```

Unsupported `protocolVersion` (anything `!= 1`) -> `ERROR` with code `UNSUPPORTED_VERSION` (`retryable: false`).

## 4. Message Types & Exact Payloads

### 4.1 `HEALTH_CHECK` -> `HEALTH_CHECK_ACK`
- **Request `payload`:** `{}`
- **Response `type`:** `"HEALTH_CHECK_ACK"`
- **Response `payload`:**
```json
{
  "status": "READY",
  "engineVersion": "0.1.0",
  "modelLoaded": true,
  "uptimeSeconds": 120,
  "processedEventsTotal": 42
}
```
  - `status`: `"STARTING"` | `"READY"` | `"DEGRADED"`
  - `modelLoaded`: `bool` (`true` in Phase 1 once mock `ISentimentEngine` & config are initialized)
  - `uptimeSeconds`: `int64` / non-negative integer
  - `processedEventsTotal`: `uint64` / non-negative integer

### 4.2 `RELOAD_CONFIG` -> `RELOAD_CONFIG_ACK`
- **Request `payload`:** `{}`
- Reads `SENTIX_CONFIG_PATH` (default: `/config/universe.json`).
- **Response `type`:** `"RELOAD_CONFIG_ACK"`
- **Response `payload`:**
```json
{
  "status": "RELOADED",
  "configVersion": 1,
  "watchlistCount": 10,
  "sectorEtfCount": 8,
  "socialAllowlistCount": 7
}
```
- If config file is missing/invalid JSON, preserve previous active config if one exists or set status `DEGRADED` and return `ERROR` (`INTERNAL_ERROR`).

### 4.3 `SCORE_EVENT` -> `SCORE_EVENT_RESULT`
- **Request `payload` (`MarketEvent`):**
```json
{
  "eventId": "evt_98a7f6",
  "source": "ALPACA_BENZINGA",
  "author": "Benzinga",
  "publishedAtUtc": "2026-11-26T14:30:00Z",
  "headline": "Apple announces record AI hardware demand",
  "summary": "Apple beats guidance on strong demand.",
  "body": "<p>Apple announces...</p>",
  "sourceTickers": ["AAPL"]
}
```
- **Response `type`:** `"SCORE_EVENT_RESULT"`
- **Response `payload`:**
```json
{
  "eventId": "evt_98a7f6",
  "isDuplicate": false,
  "dedupHash": "sha256:3f8a...",
  "matchedTickers": ["AAPL"],
  "matchedSectorEtfs": ["XLK"],
  "finbert": {
    "positive": 0.89,
    "negative": 0.03,
    "neutral": 0.08,
    "compositeScore": 0.86
  },
  "passToDecisionSwarm": true,
  "dropReason": null,
  "processingTimeUs": 18450
}
```
  - `finbert.compositeScore = finbert.positive - finbert.negative` in `[-1.0, +1.0]`, with `positive + negative + neutral == 1.0`.
  - `dropReason`: `null` | `"SOURCE_NOT_ALLOWED"` | `"DUPLICATE"` | `"NO_WATCHLIST_MATCH"` | `"LOW_SENTIMENT_STRENGTH"`.

### 4.4 `ERROR`
- **Response `type`:** `"ERROR"`
- **Response `payload`:**
```json
{
  "code": "INVALID_FRAME",
  "message": "Frame length must be between 1 and 1048576 bytes.",
  "retryable": false
}
```
- **Allowed `code` values:**
  `INVALID_FRAME` | `PAYLOAD_TOO_LARGE` | `DESERIALIZATION_ERROR` | `UNSUPPORTED_VERSION` | `MODEL_NOT_READY` | `INFERENCE_TIMEOUT` | `INTERNAL_ERROR`.

## 5. Native Text Preprocessing, Dedup, Allowlist & Scoring Pipeline

Inside `sentix-native`, `SCORE_EVENT` executes deterministically in this order:

1. **HTML Stripping & Whitespace Normalization:**
   - Remove HTML tags (`<...>`), decode basic entities (`&amp;`, `&lt;`, `&gt;`, `&quot;`, `&#39;`, `&nbsp;`), collapse consecutive whitespace to single spaces, trim.
2. **Summary Fallback (Empty Body Handling):**
   - If stripped `body` is non-empty -> `effectiveText = trim(cleanHeadline + " " + cleanBody)`.
   - If stripped `body` is empty -> `effectiveText = trim(cleanHeadline + " " + cleanSummary)`.
3. **SHA-256 Deduplication Key:**
   - Normalize case/whitespace of `effectiveText` -> compute `SHA-256` -> format `"sha256:<64 lowercase hex chars>"`.
4. **Evaluation Precedence:**
   - **Check 1 — Allowlist (`SOURCE_NOT_ALLOWED`):** Validate `source` against loaded `socialAllowlist`. When `source == "X_ALLOWLIST"`, strip optional leading `@` from `author` and verify case-insensitive membership in `X_ALLOWLIST` identifiers; otherwise return `dropReason = "SOURCE_NOT_ALLOWED"`, `passToDecisionSwarm = false`.
   - **Check 2 — Rolling Window Dedup (`DUPLICATE`):** If `dedupHash` was already seen within `fastPath.dedupWindowMinutes` (default `180` min) -> return `isDuplicate = true`, `dropReason = "DUPLICATE"`, `passToDecisionSwarm = false`. Record `dedupHash` upon non-duplicate evaluation.
   - **Check 3 — Watchlist & Sector ETF Matching (`NO_WATCHLIST_MATCH`):** Match `sourceTickers` (case-insensitive) and whole-word/boundary occurrences of watchlist tickers, aliases, and sector ETF tickers in `effectiveText`. Collect unique sorted/ordered `matchedTickers` and unique associated `matchedSectorEtfs`. If both are empty -> `dropReason = "NO_WATCHLIST_MATCH"`, `passToDecisionSwarm = false`.
   - **Check 4 — Sentiment Engine (`ISentimentEngine` / `LOW_SENTIMENT_STRENGTH`):** Run Phase 1 deterministic mock scorer (swappable for ONNX-FinBERT `INT8` in Phase 2). If `abs(compositeScore) < fastPath.minAbsCompositeScore` (default `0.55`) -> `dropReason = "LOW_SENTIMENT_STRENGTH"`, `passToDecisionSwarm = false`.
   - **Pass:** Otherwise `dropReason = null`, `passToDecisionSwarm = true`.

## 6. Clarified Edge Cases & Concurrency Guarantees (Spec-First Sync)

- **Connection & Frame Limits:** `sentix-native` accepts up to `16` concurrent active connections (`INTERNAL_ERROR` returned when exhausted) and applies a `5 s` mid-frame read timeout per partial frame. Framing or JSON-parse failures carry `correlationId: ""`; unknown `type` values return `ERROR` (`DESERIALIZATION_ERROR`).
- **Config Reload & Limits:** `RELOAD_CONFIG` validates `maxEquitiesWsSymbols <= 30` and total unique symbols as well; a failed reload returns `ERROR` (`INTERNAL_ERROR`, `retryable: true`) and retains the prior active config if one was loaded (entering `DEGRADED` only when no valid config is active).
- **Extended HTML Cleaning:** In addition to `<...>` stripping and `&amp;`, `&lt;`, `&gt;`, `&quot;`, `&#39;`, `&nbsp;`, the cleaner strips `<script>`/`<style>` element contents, decodes `&apos;` and numeric character references (`&#...;` / `&#x...;`), and preserves `<` when not followed by an ASCII tag starter letter.
- **Deduplication Time Base & Empty Text:** Rolling `dedupWindowMinutes` is measured via daemon monotonic `steady_clock` receive time. Empty `effectiveText` events normalize to a single SHA-256 digest and therefore deduplicate consistently.
- **Matching & Drop Semantics:**
  - `socialAllowlist` `source` values are matched case-sensitively (`ALPACA_BENZINGA`, `SEC_EDGAR`, `X_ALLOWLIST`); for non-`X_ALLOWLIST` sources, `author` is not required to match `identifier`.
  - Text ticker matching requires uppercase tickers as written or `$TICKER` cashtags (case-insensitive), while watchlist `aliases` match case-insensitively.
  - Direct sector ETF ticker matches populate `matchedSectorEtfs` only; `benchmarkTicker` (`SPY`) never triggers `matchedTickers` or `matchedSectorEtfs`.
  - Pre-scoring drops (`SOURCE_NOT_ALLOWED`, `DUPLICATE`, `NO_WATCHLIST_MATCH`) emit `finbert: { positive: 0, negative: 0, neutral: 1, compositeScore: 0 }` and empty match lists (unless already populated prior to drop).
