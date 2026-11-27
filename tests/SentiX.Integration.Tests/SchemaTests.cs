using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentiX.Infrastructure;

namespace SentiX.Integration.Tests;

public sealed class SchemaTests
{
    [Fact]
    public void InitialMigrationMatchesModelAndGeneratesPostgresSchemaOffline()
    {
        using var database = new SentiXDbContextFactory().CreateDbContext([]);
        Assert.False(database.Database.HasPendingModelChanges());
        Assert.EndsWith("_InitialCreate", Assert.Single(database.Database.GetMigrations()));

        string sql = database.GetService<IMigrator>().GenerateScript();
        foreach (var table in new[]
        {
            "market_events", "fast_path_results", "decision_debates", "risk_evaluations",
            "orders", "daily_benchmarks", "scheduled_events", "system_state"
        })
        {
            Assert.Contains($"CREATE TABLE {table} (", sql, StringComparison.Ordinal);
        }

        Assert.Contains("raw_payload jsonb NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("source_tickers text[] NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("published_at_utc timestamptz NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("id bigserial", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (id = 1)", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO system_state", sql, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT orders_alpaca_order_id_key UNIQUE (alpaca_order_id)", sql, StringComparison.Ordinal);
        Assert.Contains("idx_fast_path_passed ON fast_path_results (pass_to_decision_swarm, created_at_utc DESC)",
            sql, StringComparison.Ordinal);
    }
}
