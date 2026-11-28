using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SentiX.Host;
using SentiX.Ingestion;

namespace SentiX.Integration.Tests;

public sealed class HostTests
{
    [Fact]
    public async Task HealthIsAvailableWithPlaceholderCredentialsAndNativeOffline()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "",
                ["Sentix:ConfigPath"] = UniverseAndXTests.UniversePath,
                ["Sentix:NativeSocketPath"] = Path.Combine(Path.GetTempPath(), $"sx-{Guid.NewGuid():N}.sock"),
                ["Alpaca:ApiKey"] = "PK_YOUR_PAPER_KEY",
                ["Alpaca:SecretKey"] = "YOUR_PAPER_SECRET",
                ["ALPACA_API_KEY"] = "PK_YOUR_PAPER_KEY",
                ["ALPACA_SECRET_KEY"] = "YOUR_PAPER_SECRET",
                ["ALPACA_TRADING_ENABLED"] = "false",
                ["TRADING_ENABLED"] = "false",
                ["X:BearerToken"] = "",
                ["Finnhub:ApiKey"] = "placeholder",
                ["Sec:UserAgent"] = "disabled"
            })));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal("0.4.0", payload.RootElement.GetProperty("version").GetString());
        Assert.Equal("unavailable", payload.RootElement.GetProperty("native").GetString());
        Assert.Equal(4, payload.RootElement.GetProperty("phase").GetInt32());
        Assert.Equal("UNAVAILABLE", payload.RootElement.GetProperty("tradingState").GetString());
        Assert.False(payload.RootElement.GetProperty("paperSwitchArmed").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("paperExecutionConfigured").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("tradingEnabled").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("orderExecutionEnabled").GetBoolean());

        using var overviewResponse = await client.GetAsync("/api/overview");
        Assert.Equal(HttpStatusCode.OK, overviewResponse.StatusCode);
        using var overviewPayload = JsonDocument.Parse(await overviewResponse.Content.ReadAsStringAsync());
        Assert.Equal("0.4.0", overviewPayload.RootElement.GetProperty("version").GetString());

        var workers = factory.Services.GetServices<IHostedService>().OfType<BackgroundService>().ToArray();
        await Assert.Single(workers.OfType<AlpacaNewsCollector>()).ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.Single(workers.OfType<XIngestionWorker>()).ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.Single(workers.OfType<SecEdgarCollector>()).ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.Single(workers.OfType<ScheduledEventsSyncWorker>()).ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.Single(workers.OfType<AlpacaQuotesCollector>()).ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("0.4.0", "0.4.0")]
    [InlineData("v0.4.0", "0.4.0")]
    [InlineData("0.4.0+42c9af79dcaf3895", "0.4.0+42c9af7")]
    [InlineData("  ", "0.4.0")]
    public void ResolveHostVersionFormatsCleanVersionStrings(string raw, string expected)
    {
        Assert.Equal(expected, TelemetryEndpoints.ResolveHostVersion(raw));
    }
}
