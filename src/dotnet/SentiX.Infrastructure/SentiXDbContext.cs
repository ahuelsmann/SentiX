using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace SentiX.Infrastructure;

public sealed class SentiXDbContext(DbContextOptions<SentiXDbContext> options) : DbContext(options)
{
    public DbSet<MarketEventRow> MarketEvents => Set<MarketEventRow>();
    public DbSet<FastPathResultRow> FastPathResults => Set<FastPathResultRow>();
    public DbSet<DecisionDebateRow> DecisionDebates => Set<DecisionDebateRow>();
    public DbSet<RiskEvaluationRow> RiskEvaluations => Set<RiskEvaluationRow>();
    public DbSet<OrderRow> Orders => Set<OrderRow>();
    public DbSet<DailyBenchmarkRow> DailyBenchmarks => Set<DailyBenchmarkRow>();
    public DbSet<ScheduledEventRow> ScheduledEvents => Set<ScheduledEventRow>();
    public DbSet<SystemStateRow> SystemState => Set<SystemStateRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Spec 005 defines its indexes explicitly and has no implicit FK indexes.
        configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseSerialColumns();

        modelBuilder.Entity<MarketEventRow>(entity =>
        {
            entity.ToTable("market_events");
            entity.HasKey(e => e.EventId).HasName("market_events_pkey");
            entity.Property(e => e.PublishedAtUtc).HasColumnType("timestamptz");
            entity.Property(e => e.IngestedAtUtc).HasColumnType("timestamptz").HasDefaultValueSql("now()");
            entity.Property(e => e.SourceTickers).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
            entity.Property(e => e.RawPayload).HasColumnType("jsonb");
            entity.Property(e => e.Summary).HasDefaultValue("");
            entity.HasIndex(e => e.PublishedAtUtc).IsDescending(true).HasDatabaseName("idx_market_events_published");
        });

        modelBuilder.Entity<FastPathResultRow>(entity =>
        {
            entity.ToTable("fast_path_results");
            entity.HasKey(e => e.Id).HasName("fast_path_results_pkey");
            entity.Property(e => e.MatchedTickers).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
            entity.Property(e => e.MatchedSectorEtfs).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
            entity.Property(e => e.CreatedAtUtc).HasColumnType("timestamptz").HasDefaultValueSql("now()");
            entity.HasOne<MarketEventRow>().WithMany().HasForeignKey(e => e.EventId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(e => e.DedupHash).HasDatabaseName("idx_fast_path_dedup");
            entity.HasIndex(e => new { e.PassToDecisionSwarm, e.CreatedAtUtc })
                .IsDescending(false, true).HasDatabaseName("idx_fast_path_passed");
        });

        modelBuilder.Entity<DecisionDebateRow>(entity =>
        {
            entity.ToTable("decision_debates");
            entity.HasKey(e => e.ProposalId).HasName("decision_debates_pkey");
            entity.Property(e => e.FactJson).HasColumnType("jsonb");
            entity.Property(e => e.BullJson).HasColumnType("jsonb");
            entity.Property(e => e.BearJson).HasColumnType("jsonb");
            entity.Property(e => e.SoftRiskJson).HasColumnType("jsonb");
            entity.Property(e => e.JudgeJson).HasColumnType("jsonb");
            entity.Property(e => e.CreatedAtUtc).HasColumnType("timestamptz").HasDefaultValueSql("now()");
            entity.Property(e => e.TotalTokens).HasDefaultValue(0);
            entity.HasOne<MarketEventRow>().WithMany().HasForeignKey(e => e.EventId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(e => e.CreatedAtUtc).IsDescending(true).HasDatabaseName("idx_debates_created");
        });

        modelBuilder.Entity<RiskEvaluationRow>(entity =>
        {
            entity.ToTable("risk_evaluations");
            entity.HasKey(e => e.Id).HasName("risk_evaluations_pkey");
            entity.Property(e => e.Violations).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            entity.Property(e => e.EvaluatedAtUtc).HasColumnType("timestamptz").HasDefaultValueSql("now()");
            entity.Property(e => e.CalculatedQty).HasDefaultValue(0);
            entity.HasOne<DecisionDebateRow>().WithMany().HasForeignKey(e => e.ProposalId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<OrderRow>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(e => e.ClientOrderId).HasName("orders_pkey");
            entity.Property(e => e.LimitPrice).HasPrecision(12, 4);
            entity.Property(e => e.StopLossPrice).HasPrecision(12, 4);
            entity.Property(e => e.TakeProfitPrice).HasPrecision(12, 4);
            entity.Property(e => e.FilledAvgPrice).HasPrecision(12, 4);
            entity.Property(e => e.SubmittedAtUtc).HasColumnType("timestamptz").HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAtUtc).HasColumnType("timestamptz").HasDefaultValueSql("now()");
            entity.HasOne<DecisionDebateRow>().WithMany().HasForeignKey(e => e.ProposalId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(e => e.AlpacaOrderId).IsUnique().HasDatabaseName("orders_alpaca_order_id_key");
            entity.HasIndex(e => new { e.Ticker, e.Status }).HasDatabaseName("idx_orders_ticker_status");
        });

        modelBuilder.Entity<DailyBenchmarkRow>(entity =>
        {
            entity.ToTable("daily_benchmarks");
            entity.HasKey(e => e.TradingDate).HasName("daily_benchmarks_pkey");
            entity.Property(e => e.PortfolioEquity).HasPrecision(14, 2);
            entity.Property(e => e.SpyClosePrice).HasPrecision(12, 4);
            entity.Property(e => e.PaperMode).HasDefaultValue(true);
        });

        modelBuilder.Entity<ScheduledEventRow>(entity =>
        {
            entity.ToTable("scheduled_events");
            entity.HasKey(e => e.Id).HasName("scheduled_events_pkey");
            entity.Property(e => e.ScheduledAtUtc).HasColumnType("timestamptz");
            entity.Property(e => e.SyncedAtUtc).HasColumnType("timestamptz").HasDefaultValueSql("now()");
            entity.HasIndex(e => e.ScheduledAtUtc).HasDatabaseName("idx_scheduled_events_time");
        });

        modelBuilder.Entity<SystemStateRow>(entity =>
        {
            entity.ToTable("system_state", table => table.HasCheckConstraint("system_state_id_check", "id = 1"));
            entity.HasKey(e => e.Id).HasName("system_state_pkey");
            entity.Property(e => e.UpdatedAtUtc).HasColumnType("timestamptz").HasDefaultValueSql("now()");
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.TradingState).HasDefaultValue("ACTIVE");
            entity.Property(e => e.UpdatedBy).HasDefaultValue("SYSTEM");
            entity.HasData(new { Id = 1, TradingState = "ACTIVE" });
        });

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                string columns = string.Join("_", foreignKey.Properties.Select(property => property.GetColumnName()));
                foreignKey.SetConstraintName($"{entity.GetTableName()}_{columns}_fkey");
            }
        }
    }
}

public sealed class SentiXDbContextFactory : IDesignTimeDbContextFactory<SentiXDbContext>
{
    public SentiXDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SentiXDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Default"),
                postgres => postgres.SetPostgresVersion(17, 0));
        return new SentiXDbContext(options.Options);
    }
}
