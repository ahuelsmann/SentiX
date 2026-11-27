using System.Collections.Concurrent;
using System.Net;
using System.Text;
using SentiX.Domain;

namespace SentiX.DecisionSwarm.Tests;

/// <summary>Deterministic, manually advanced <see cref="TimeProvider"/> (no wall-clock dependence).</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = new(2026, 11, 27, 15, 0, 0, TimeSpan.Zero);
    private long _elapsedTicks;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _elapsedTicks;
        }
    }

    public TimeSpan Elapsed => TimeSpan.FromTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _elapsedTicks += delta.Ticks;
            _utcNow += delta;
            due = _timers.Where(t => t.DueTicks is { } d && d <= _elapsedTicks).OrderBy(t => t.DueTicks).ToList();
            foreach (var timer in due)
            {
                timer.Reschedule(_elapsedTicks);
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    /// <summary>
    /// Advances fake time in <paramref name="step"/> increments, but only while an LLM call is in flight, so that
    /// inter-stage orchestration code never races the clock.
    /// </summary>
    public async Task<T> DriveAsync<T>(Task<T> task, ScriptedLlmClient client, TimeSpan step, int maxSteps = 400)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            var spins = 0;
            while (!task.IsCompleted && client.InFlight == 0 && spins++ < 5_000)
            {
                await Task.Delay(1);
            }

            if (task.IsCompleted)
            {
                break;
            }

            Advance(step);
            await Task.Delay(2);
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private long _periodTicks;
        private bool _disposed;

        public long? DueTicks { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                DueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : owner._elapsedTicks + Math.Max(0, dueTime.Ticks);
                _periodTicks = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
            }

            return true;
        }

        public void Reschedule(long now) => DueTicks = _periodTicks > 0 ? now + _periodTicks : null;

        public void Fire()
        {
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                DueTicks = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>In-memory <see cref="ILlmClient"/> with per-role scripted behaviour.</summary>
internal sealed class ScriptedLlmClient : ILlmClient
{
    private readonly ConcurrentDictionary<AgentRole, int> _calls = new();
    private int _inFlight;

    public ConcurrentQueue<LlmCompletionRequest> Requests { get; } = new();

    public Dictionary<AgentRole, Func<int, CancellationToken, Task<LlmCompletionResult>>> Handlers { get; } = new();

    public int InFlight => Volatile.Read(ref _inFlight);

    public int Calls(AgentRole role) => _calls.TryGetValue(role, out var count) ? count : 0;

    public int TotalCalls => _calls.Values.Sum();

    public LlmCompletionRequest Request(AgentRole role) => Requests.Last(r => r.Role == role);

    public async Task<LlmCompletionResult> CompleteJsonAsync(LlmCompletionRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        var attempt = _calls.AddOrUpdate(request.Role, 1, (_, count) => count + 1);
        Interlocked.Increment(ref _inFlight);
        try
        {
            if (Handlers.TryGetValue(request.Role, out var handler))
            {
                return await handler(attempt, cancellationToken);
            }

            return Result(Fixtures.DefaultJson(request.Role));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public static LlmCompletionResult Result(string json, int tokens = 100) =>
        new(json, tokens - (tokens / 4), tokens / 4, tokens, "test-model");
}

/// <summary>Captures outgoing HTTP requests and replies with a scripted response. Never touches the network.</summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public Uri? RequestUri { get; private set; }
    public HttpMethod? Method { get; private set; }
    public string? Body { get; private set; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        RequestUri = request.RequestUri;
        Method = request.Method;
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        foreach (var header in request.Headers)
        {
            Headers[header.Key] = string.Join(",", header.Value);
        }

        return responder(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

internal static class Fixtures
{
    public const string EventId = "evt_test_001";
    public const string FastKey = "sk-test-fast-SECRET";
    public const string JudgeKey = "sk-ant-test-judge-SECRET";

    public const string FactJson =
        """{"isActionable":true,"eventCategory":"EARNINGS","summary":"Apple beat Q4 estimates.","primaryTicker":"AAPL","instrumentType":"EQUITY","affectedTickers":["AAPL","XLK"],"noveltyScore":0.9,"reasoning":"New quarterly results."}""";

    public const string NonActionableFactJson =
        """{"isActionable":false,"eventCategory":"NOISE","summary":"Recycled rumour.","primaryTicker":"","instrumentType":"EQUITY","affectedTickers":[],"noveltyScore":0.05,"reasoning":"No new facts."}""";

    public const string BullJson =
        """{"direction":"LONG","conviction":0.8,"keyArguments":["Revenue beat"],"expectedHorizonHours":48,"priceConfirmationSupported":true}""";

    public const string BearJson =
        """{"counterConviction":0.3,"keyRisks":["Priced in"],"pricedInLikelihood":0.2,"whipsawRisk":"LOW"}""";

    public const string SoftRiskJson =
        """{"softVeto":false,"riskLevel":"LOW","flags":[],"recommendation":"Proceed."}""";

    public const string SoftVetoJson =
        """{"softVeto":true,"riskLevel":"HIGH","flags":["FOMC within 48h"],"recommendation":"Stand aside."}""";

    public static string JudgeJson(string action = "BUY", string ticker = "AAPL", double confidence = 0.79) =>
        $$"""{"proposalId":"prop_llm_supplied","eventId":"evt_llm_supplied","action":"{{action}}","ticker":"{{ticker}}","instrumentType":"EQUITY","confidence":{{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"expectedHorizonHours":48,"suggestedStopLossPct":0.03,"suggestedTakeProfitPct":0.06,"rationale":"Strong beat.","dissentSummary":"Bear noted priced-in risk."}""";

    public static string DefaultJson(AgentRole role) => role switch
    {
        AgentRole.FactAnalyst => FactJson,
        AgentRole.BullAgent => BullJson,
        AgentRole.BearAgent => BearJson,
        AgentRole.SoftRiskAgent => SoftRiskJson,
        _ => JudgeJson()
    };

    public static LlmOptions ConfiguredOptions() => new() { FastApiKey = FastKey, JudgeApiKey = JudgeKey };

    public static MarketEvent Event(string headline = "Apple beats record Q4 earnings", string summary = "Revenue +8% y/y.",
        string body = "Apple Inc. reported quarterly results above consensus.", string author = "Benzinga Newsdesk") =>
        new(EventId, EventSource.AlpacaBenzinga, author, new DateTimeOffset(2026, 11, 27, 14, 30, 0, TimeSpan.Zero),
            headline, summary, body, ["AAPL"]);

    public static FastPathResult FastPath() =>
        new(EventId, false, "dedup-hash", ["AAPL"], ["XLK"], new FinbertScores(0.91, 0.03, 0.06, 0.88), true, null, 1200);

    public static MarketQuoteSnapshot Quote() =>
        new("AAPL", 229.10m, 229.14m, 229.12m, 1.75, new DateTimeOffset(2026, 11, 27, 14, 31, 0, TimeSpan.Zero));

    public static DecisionDebateRequest Request(IReadOnlyList<string>? upcoming = null, MarketQuoteSnapshot? quote = null) =>
        new(Event(), FastPath(), upcoming ?? ["AAPL EARNINGS 2026-11-28T21:00:00Z (Finnhub)"], quote ?? Quote());

    public static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
