using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SentiX.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "daily_benchmarks",
                columns: table => new
                {
                    trading_date = table.Column<DateOnly>(type: "date", nullable: false),
                    paper_mode = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    portfolio_equity = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    daily_pnl_pct = table.Column<double>(type: "double precision", nullable: false),
                    cum_return_pct = table.Column<double>(type: "double precision", nullable: false),
                    spy_close_price = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    spy_cum_return_pct = table.Column<double>(type: "double precision", nullable: false),
                    open_positions = table.Column<int>(type: "integer", nullable: false),
                    trades_executed = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("daily_benchmarks_pkey", x => x.trading_date);
                });

            migrationBuilder.CreateTable(
                name: "market_events",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    author = table.Column<string>(type: "text", nullable: false),
                    published_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ingested_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    headline = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    body = table.Column<string>(type: "text", nullable: false),
                    source_tickers = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    raw_payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("market_events_pkey", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.SerialColumn),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    ticker = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    scheduled_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    synced_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("scheduled_events_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    trading_state = table.Column<string>(type: "text", nullable: false, defaultValue: "ACTIVE"),
                    reason = table.Column<string>(type: "text", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: false, defaultValue: "SYSTEM"),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("system_state_pkey", x => x.id);
                    table.CheckConstraint("system_state_id_check", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "decision_debates",
                columns: table => new
                {
                    proposal_id = table.Column<string>(type: "text", nullable: false),
                    event_id = table.Column<string>(type: "text", nullable: false),
                    prompt_version = table.Column<string>(type: "text", nullable: false),
                    fact_json = table.Column<string>(type: "jsonb", nullable: false),
                    bull_json = table.Column<string>(type: "jsonb", nullable: true),
                    bear_json = table.Column<string>(type: "jsonb", nullable: true),
                    soft_risk_json = table.Column<string>(type: "jsonb", nullable: true),
                    judge_json = table.Column<string>(type: "jsonb", nullable: false),
                    final_action = table.Column<string>(type: "text", nullable: false),
                    ticker = table.Column<string>(type: "text", nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    total_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    total_latency_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("decision_debates_pkey", x => x.proposal_id);
                    table.ForeignKey(
                        name: "decision_debates_event_id_fkey",
                        column: x => x.event_id,
                        principalTable: "market_events",
                        principalColumn: "event_id");
                });

            migrationBuilder.CreateTable(
                name: "fast_path_results",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.SerialColumn),
                    event_id = table.Column<string>(type: "text", nullable: false),
                    dedup_hash = table.Column<string>(type: "text", nullable: false),
                    is_duplicate = table.Column<bool>(type: "boolean", nullable: false),
                    matched_tickers = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    matched_sector_etfs = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    finbert_positive = table.Column<double>(type: "double precision", nullable: false),
                    finbert_negative = table.Column<double>(type: "double precision", nullable: false),
                    finbert_neutral = table.Column<double>(type: "double precision", nullable: false),
                    composite_score = table.Column<double>(type: "double precision", nullable: false),
                    pass_to_decision_swarm = table.Column<bool>(type: "boolean", nullable: false),
                    drop_reason = table.Column<string>(type: "text", nullable: true),
                    processing_time_us = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("fast_path_results_pkey", x => x.id);
                    table.ForeignKey(
                        name: "fast_path_results_event_id_fkey",
                        column: x => x.event_id,
                        principalTable: "market_events",
                        principalColumn: "event_id");
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    client_order_id = table.Column<string>(type: "text", nullable: false),
                    alpaca_order_id = table.Column<string>(type: "text", nullable: true),
                    proposal_id = table.Column<string>(type: "text", nullable: false),
                    ticker = table.Column<string>(type: "text", nullable: false),
                    side = table.Column<string>(type: "text", nullable: false),
                    order_type = table.Column<string>(type: "text", nullable: false),
                    qty = table.Column<int>(type: "integer", nullable: false),
                    limit_price = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    stop_loss_price = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    take_profit_price = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    filled_avg_price = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    submitted_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("orders_pkey", x => x.client_order_id);
                    // EF models this as a unique index so alpaca_order_id stays nullable.
                    table.UniqueConstraint("orders_alpaca_order_id_key", x => x.alpaca_order_id);
                    table.ForeignKey(
                        name: "orders_proposal_id_fkey",
                        column: x => x.proposal_id,
                        principalTable: "decision_debates",
                        principalColumn: "proposal_id");
                });

            migrationBuilder.CreateTable(
                name: "risk_evaluations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.SerialColumn),
                    proposal_id = table.Column<string>(type: "text", nullable: false),
                    approved = table.Column<bool>(type: "boolean", nullable: false),
                    trading_state = table.Column<string>(type: "text", nullable: false),
                    violations = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    calculated_qty = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    clamped_sl_pct = table.Column<double>(type: "double precision", nullable: true),
                    clamped_tp_pct = table.Column<double>(type: "double precision", nullable: true),
                    client_order_id = table.Column<string>(type: "text", nullable: true),
                    evaluated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("risk_evaluations_pkey", x => x.id);
                    table.ForeignKey(
                        name: "risk_evaluations_proposal_id_fkey",
                        column: x => x.proposal_id,
                        principalTable: "decision_debates",
                        principalColumn: "proposal_id");
                });

            migrationBuilder.InsertData(
                table: "system_state",
                columns: new[] { "id", "reason", "trading_state" },
                values: new object[] { 1, null, "ACTIVE" });

            migrationBuilder.CreateIndex(
                name: "idx_debates_created",
                table: "decision_debates",
                column: "created_at_utc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "idx_fast_path_dedup",
                table: "fast_path_results",
                column: "dedup_hash");

            migrationBuilder.CreateIndex(
                name: "idx_fast_path_passed",
                table: "fast_path_results",
                columns: new[] { "pass_to_decision_swarm", "created_at_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_market_events_published",
                table: "market_events",
                column: "published_at_utc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "idx_orders_ticker_status",
                table: "orders",
                columns: new[] { "ticker", "status" });

            migrationBuilder.CreateIndex(
                name: "idx_scheduled_events_time",
                table: "scheduled_events",
                column: "scheduled_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "daily_benchmarks");

            migrationBuilder.DropTable(
                name: "fast_path_results");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "risk_evaluations");

            migrationBuilder.DropTable(
                name: "scheduled_events");

            migrationBuilder.DropTable(
                name: "system_state");

            migrationBuilder.DropTable(
                name: "decision_debates");

            migrationBuilder.DropTable(
                name: "market_events");
        }
    }
}
