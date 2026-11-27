using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentiX.Infrastructure;

namespace SentiX.Integration.Tests;

public sealed class ScheduledEventStoreTests
{
    [LocalPostgresFact]
    public async Task ConcurrentCalendarSyncsPersistEachNaturalKeyOnce()
    {
        var dbName = $"sentix_calendar_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable(LocalPostgresFactAttribute.SocketVariable),
            Username = "postgres",
            Database = "postgres",
            Pooling = false
        };
        await using var admin = new NpgsqlConnection(connection.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {dbName}", admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            connection.Database = dbName;
            var options = new DbContextOptionsBuilder<SentiXDbContext>()
                .UseNpgsql(connection.ConnectionString, postgres => postgres.SetPostgresVersion(17, 0)).Options;
            var factory = new ContextFactory(options);
            await using var db = factory.CreateDbContext();
            await db.Database.MigrateAsync();

            var timestamp = new DateTimeOffset(2026, 6, 5, 12, 30, 0, TimeSpan.Zero);
            ScheduledEventRow[] snapshot =
            [
                new(0, "CPI", null, "Consumer Price Index", timestamp, "FINNHUB", default),
                new(0, "EARNINGS", "AAPL", "AAPL Earnings", timestamp, "FINNHUB", default)
            ];
            var store = new ScheduledEventStore(factory);
            await Task.WhenAll(store.AddNewAsync(snapshot, CancellationToken.None),
                store.AddNewAsync(snapshot, CancellationToken.None));
            await store.AddNewAsync(snapshot, CancellationToken.None);

            var saved = await db.ScheduledEvents.AsNoTracking().ToArrayAsync();
            Assert.Equal(2, saved.Length);
            Assert.Single(saved, row => row.Ticker is null && row.EventType == "CPI");
            Assert.Single(saved, row => row.Ticker == "AAPL" && row.EventType == "EARNINGS");
            Assert.All(saved, row => Assert.Equal(timestamp, row.ScheduledAtUtc));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {dbName}", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class ContextFactory(DbContextOptions<SentiXDbContext> options) : IDbContextFactory<SentiXDbContext>
    {
        public SentiXDbContext CreateDbContext() => new(options);
    }
}

internal sealed class LocalPostgresFactAttribute : FactAttribute
{
    public const string SocketVariable = "SENTIX_TEST_POSTGRES_SOCKET";

    public LocalPostgresFactAttribute()
    {
        var socketDirectory = Environment.GetEnvironmentVariable(SocketVariable);
        if (!OperatingSystem.IsLinux() || socketDirectory is null ||
            !socketDirectory.StartsWith('/') || socketDirectory.IndexOfAny([',', ':']) >= 0 ||
            !Directory.Exists(socketDirectory))
        {
            Skip = $"Set {SocketVariable} to a local PostgreSQL 17 Unix socket directory to run this test.";
        }
    }
}
