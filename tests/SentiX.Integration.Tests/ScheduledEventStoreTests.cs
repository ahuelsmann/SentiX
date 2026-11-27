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

            var nextSession = timestamp.AddDays(1).AddHours(6);
            var rescheduled = snapshot[1] with { ScheduledAtUtc = nextSession };
            var outside = snapshot[1] with { ScheduledAtUtc = timestamp.AddDays(-1) };
            var otherSource = snapshot[1] with { Source = "OTHER" };
            var missing = snapshot[1] with { Ticker = "MSFT", Title = "MSFT Earnings" };
            await store.AddNewAsync([outside, otherSource, missing], CancellationToken.None);

            await store.ReplaceFinnhubWindowAsync(timestamp, timestamp.AddDays(14), [rescheduled],
                includesEconomicCalendar: false, CancellationToken.None);
            var refreshed = await db.ScheduledEvents.AsNoTracking().ToArrayAsync();
            Assert.DoesNotContain(refreshed, row => row.Ticker == "MSFT");
            Assert.Contains(refreshed, row => row.Ticker is null && row.EventType == "CPI");
            Assert.Contains(refreshed, row => row.Source == "OTHER");
            Assert.Contains(refreshed, row => row.ScheduledAtUtc == outside.ScheduledAtUtc);
            var moved = Assert.Single(refreshed, row => row.Source == "FINNHUB" && row.Ticker == "AAPL" &&
                row.ScheduledAtUtc >= timestamp);
            Assert.Equal(nextSession, moved.ScheduledAtUtc);
            Assert.NotEqual(default, moved.SyncedAtUtc);

            var priorSync = moved.SyncedAtUtc.AddDays(-1);
            await db.ScheduledEvents.Where(row => row.Id == moved.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.SyncedAtUtc, priorSync));
            await store.ReplaceFinnhubWindowAsync(timestamp, timestamp.AddDays(14), [rescheduled, rescheduled],
                includesEconomicCalendar: false, CancellationToken.None);
            var repeated = Assert.Single(await db.ScheduledEvents.AsNoTracking().ToArrayAsync(),
                row => row.Source == "FINNHUB" && row.Ticker == "AAPL" && row.ScheduledAtUtc >= timestamp);
            Assert.Equal(moved.Id, repeated.Id);
            Assert.True(repeated.SyncedAtUtc > priorSync);

            await store.ReplaceFinnhubWindowAsync(timestamp, timestamp.AddDays(14), [],
                includesEconomicCalendar: true, CancellationToken.None);
            var afterEmpty = await db.ScheduledEvents.AsNoTracking().ToArrayAsync();
            Assert.Equal(2, afterEmpty.Length);
            Assert.DoesNotContain(afterEmpty, row => row.Source == "FINNHUB" && row.ScheduledAtUtc >= timestamp);
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
