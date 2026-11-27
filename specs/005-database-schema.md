# Spec 005 — PostgreSQL 17 Schema (EF Core 10 + Npgsql)

> Append-only audit trail in PostgreSQL 17 (see also [`001-masterplan.md`](./001-masterplan.md)).

## 1. DDL Specification

```sql
CREATE TABLE market_events (
    event_id TEXT PRIMARY KEY,
    source TEXT NOT NULL,
    author TEXT NOT NULL,
    published_at_utc TIMESTAMPTZ NOT NULL,
    ingested_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    headline TEXT NOT NULL,
    summary TEXT NOT NULL DEFAULT '',
    body TEXT NOT NULL,
    source_tickers TEXT[] NOT NULL DEFAULT '{}',
    raw_payload JSONB NOT NULL
);
CREATE INDEX idx_market_events_published ON market_events (published_at_utc DESC);

CREATE TABLE fast_path_results (
    id BIGSERIAL PRIMARY KEY,
    event_id TEXT NOT NULL REFERENCES market_events(event_id),
    dedup_hash TEXT NOT NULL,
    is_duplicate BOOLEAN NOT NULL,
    matched_tickers TEXT[] NOT NULL DEFAULT '{}',
    matched_sector_etfs TEXT[] NOT NULL DEFAULT '{}',
    finbert_positive DOUBLE PRECISION NOT NULL,
    finbert_negative DOUBLE PRECISION NOT NULL,
    finbert_neutral DOUBLE PRECISION NOT NULL,
    composite_score DOUBLE PRECISION NOT NULL,
    pass_to_decision_swarm BOOLEAN NOT NULL,
    drop_reason TEXT NULL,
    processing_time_us INTEGER NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_fast_path_dedup ON fast_path_results (dedup_hash);
CREATE INDEX idx_fast_path_passed ON fast_path_results (pass_to_decision_swarm, created_at_utc DESC);

CREATE TABLE decision_debates (
    proposal_id TEXT PRIMARY KEY,
    event_id TEXT NOT NULL REFERENCES market_events(event_id),
    prompt_version TEXT NOT NULL,
    fact_json JSONB NOT NULL,
    bull_json JSONB NULL,
    bear_json JSONB NULL,
    soft_risk_json JSONB NULL,
    judge_json JSONB NOT NULL,
    final_action TEXT NOT NULL,
    ticker TEXT NULL,
    confidence DOUBLE PRECISION NOT NULL,
    total_tokens INTEGER NOT NULL DEFAULT 0,
    total_latency_ms INTEGER NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_debates_created ON decision_debates (created_at_utc DESC);

CREATE TABLE risk_evaluations (
    id BIGSERIAL PRIMARY KEY,
    proposal_id TEXT NOT NULL REFERENCES decision_debates(proposal_id),
    approved BOOLEAN NOT NULL,
    trading_state TEXT NOT NULL,
    violations JSONB NOT NULL DEFAULT '[]',
    calculated_qty INTEGER NOT NULL DEFAULT 0,
    clamped_sl_pct DOUBLE PRECISION NULL,
    clamped_tp_pct DOUBLE PRECISION NULL,
    client_order_id TEXT NULL,
    evaluated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE orders (
    client_order_id TEXT PRIMARY KEY,
    alpaca_order_id TEXT UNIQUE NULL,
    proposal_id TEXT NOT NULL REFERENCES decision_debates(proposal_id),
    ticker TEXT NOT NULL,
    side TEXT NOT NULL,
    order_type TEXT NOT NULL,
    qty INTEGER NOT NULL,
    limit_price NUMERIC(12,4) NULL,
    stop_loss_price NUMERIC(12,4) NULL,
    take_profit_price NUMERIC(12,4) NULL,
    status TEXT NOT NULL,
    filled_avg_price NUMERIC(12,4) NULL,
    submitted_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_orders_ticker_status ON orders (ticker, status);

CREATE TABLE daily_benchmarks (
    trading_date DATE PRIMARY KEY,
    paper_mode BOOLEAN NOT NULL DEFAULT TRUE,
    portfolio_equity NUMERIC(14,2) NOT NULL,
    daily_pnl_pct DOUBLE PRECISION NOT NULL,
    cum_return_pct DOUBLE PRECISION NOT NULL,
    spy_close_price NUMERIC(12,4) NOT NULL,
    spy_cum_return_pct DOUBLE PRECISION NOT NULL,
    open_positions INTEGER NOT NULL,
    trades_executed INTEGER NOT NULL
);

CREATE TABLE scheduled_events (
    id BIGSERIAL PRIMARY KEY,
    event_type TEXT NOT NULL,
    ticker TEXT NULL,
    title TEXT NOT NULL,
    scheduled_at_utc TIMESTAMPTZ NOT NULL,
    source TEXT NOT NULL,
    synced_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_scheduled_events_time ON scheduled_events (scheduled_at_utc ASC);

CREATE TABLE system_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    trading_state TEXT NOT NULL DEFAULT 'ACTIVE',
    reason TEXT NULL,
    updated_by TEXT NOT NULL DEFAULT 'SYSTEM',
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
INSERT INTO system_state (id, trading_state) VALUES (1, 'ACTIVE') ON CONFLICT DO NOTHING;
```

## 2. EF Core & Migration Requirements (`SentiX.Infrastructure`)

- Exact `snake_case` table and column names, `timestamptz` for all UTC timestamps, `jsonb` for JSON columns (`raw_payload`, `fact_json`, `bull_json`, `bear_json`, `soft_risk_json`, `judge_json`, `violations`), and `text[]` for PostgreSQL array columns (`source_tickers`, `matched_tickers`, `matched_sector_etfs`).
- Include `SentiXDbContext` + initial EF Core migration (including `system_state` seed & `id = 1` check constraint + `scheduled_events`) + `IDesignTimeDbContextFactory<SentiXDbContext>` so migrations compile and validate offline without requiring a live DB during `dotnet build` / `dotnet test`.
