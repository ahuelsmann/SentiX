using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SentiX.Domain;
using SentiX.NativeBridge;

namespace SentiX.Integration.Tests;

[Collection(NativeSocketCollection.Name)]
public sealed class NativeBinaryTests
{
    [NativeBinaryFact]
    public async Task NativeProcessStripsHtmlUsesSummaryDeduplicatesAndFailsClosedAfterShutdown()
    {
        string? binary = Environment.GetEnvironmentVariable("SENTIX_NATIVE_BIN");
        Assert.False(string.IsNullOrWhiteSpace(binary), "Set SENTIX_NATIVE_BIN to run the Linux daemon test.");
        Assert.True(File.Exists(binary), $"SENTIX_NATIVE_BIN must point to the compiled Linux daemon: {binary}");
        using var socketPath = new TestSocketPath();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        var startInfo = new ProcessStartInfo(Path.GetFullPath(binary))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["SENTIX_SOCKET_PATH"] = socketPath.Path;
        startInfo.Environment["SENTIX_CONFIG_PATH"] = UniverseAndXTests.UniversePath;
        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEndAsync(token);
        var stdout = process.StandardOutput.ReadToEndAsync(token);

        try
        {
            using var bridge = new NativeBridgeClient(socketPath.Path);
            NativeResult<NativeHealth>? health = null;
            for (int attempt = 0; attempt < 100 && !process.HasExited; attempt++)
            {
                health = await bridge.HealthCheckAsync(token);
                if (!health.IsFailClosed)
                {
                    break;
                }

                await Task.Delay(100, token);
            }

            string diagnostics = process.HasExited ? $"{await stderr}\n{await stdout}" : "";
            Assert.True(health is { IsFailClosed: false, Value.Status: NativeStatus.Ready },
                $"Native daemon did not become READY. {diagnostics}");

            var html = NativeBridgeTests.CreateEvent("html") with
            {
                Headline = "<h1> Apple   update </h1>",
                Summary = "This summary must be ignored.",
                Body = "<p> Record &amp; strong&nbsp;growth. </p>",
                SourceTickers = []
            };
            var first = await bridge.ScoreEventAsync(html, token);
            Assert.False(first.IsFailClosed);
            Assert.NotNull(first.Value);
            Assert.False(first.Value.IsDuplicate);
            Assert.Equal(Hash("apple update record & strong growth."), first.Value.DedupHash);
            Assert.Contains("AAPL", first.Value.MatchedTickers);

            var duplicate = await bridge.ScoreEventAsync(html with
            {
                EventId = "plain",
                Headline = "APPLE update",
                Body = "Record & strong growth."
            }, token);
            Assert.False(duplicate.IsFailClosed);
            Assert.NotNull(duplicate.Value);
            Assert.Equal(first.Value.DedupHash, duplicate.Value.DedupHash);
            Assert.True(duplicate.Value.IsDuplicate);
            Assert.Equal(DropReason.Duplicate, duplicate.Value.DropReason);
            Assert.False(duplicate.PassToDecisionSwarm);

            var fallback = await bridge.ScoreEventAsync(html with
            {
                EventId = "fallback",
                Headline = "<b>Market bulletin</b>",
                Summary = "<span>Nvidia reports&nbsp;strong demand.</span>",
                Body = "<p>  </p>"
            }, token);
            Assert.False(fallback.IsFailClosed);
            Assert.NotNull(fallback.Value);
            Assert.False(fallback.Value.IsDuplicate);
            Assert.Equal(Hash("market bulletin nvidia reports strong demand."), fallback.Value.DedupHash);
            Assert.Contains("NVDA", fallback.Value.MatchedTickers);

            using var terminate = Process.Start(new ProcessStartInfo("kill")
            {
                ArgumentList = { "-TERM", process.Id.ToString(CultureInfo.InvariantCulture) }
            })!;
            await terminate.WaitForExitAsync(token);
            Assert.Equal(0, terminate.ExitCode);
            await process.WaitForExitAsync(token);
            Assert.Equal(0, process.ExitCode);
            Assert.False(File.Exists(socketPath.Path));

            var offline = await bridge.ScoreEventAsync(html with { EventId = "offline" }, token);
            Assert.True(offline.IsFailClosed);
            Assert.False(offline.PassToDecisionSwarm);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static string Hash(string normalizedText) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText)))}";
}

internal sealed class NativeBinaryFactAttribute : FactAttribute
{
    public NativeBinaryFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "The real native daemon test requires Linux.";
            return;
        }

        string? binary = Environment.GetEnvironmentVariable("SENTIX_NATIVE_BIN");
        bool onGitHubActions = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(binary) && !onGitHubActions)
        {
            Skip = "Set SENTIX_NATIVE_BIN to run the real daemon test.";
        }
    }
}
