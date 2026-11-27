using System.Diagnostics;
using System.Text.Json;
using SentiX.Domain;

namespace SentiX.DecisionSwarm.Tests;

public sealed class DecisionSwarmOrchestratorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private static (DecisionSwarmOrchestrator Sut, ScriptedLlmClient Client, ManualTimeProvider Time) Create(
        LlmOptions? options = null)
    {
        var client = new ScriptedLlmClient();
        var time = new ManualTimeProvider();
        var sut = new DecisionSwarmOrchestrator(client, options ?? Fixtures.ConfiguredOptions(), timeProvider: time);
        return (sut, client, time);
    }

    private static Task<LlmCompletionResult> Hang(CancellationToken ct) =>
        Task.Delay(Timeout.Infinite, ct).ContinueWith<LlmCompletionResult>(
            _ => throw new OperationCanceledException(ct), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static void AssertFailClosed(DecisionDebateOutcome outcome, string reasonFragment)
    {
        Assert.Equal(TradeAction.Hold, outcome.Proposal.Action);
        Assert.Equal(0, outcome.Proposal.Confidence);
        Assert.True(outcome.FallbackUsed);
        Assert.NotNull(outcome.FailureReason);
        Assert.Contains(reasonFragment, outcome.FailureReason, StringComparison.Ordinal);
        Assert.NotNull(outcome.JudgeJson);
        var judge = JsonSerializer.Deserialize<TradeProposal>(outcome.JudgeJson!, SentiXJson.Options)!;
        Assert.Equal(TradeAction.Hold, judge.Action);
        Assert.Equal(0, judge.Confidence);
        Assert.Equal(Fixtures.EventId, outcome.Proposal.EventId);
        Assert.Equal("v3.0", outcome.PromptVersion);
    }

    [Fact]
    public async Task HappyPath_ProducesBuyProposal_WithServerSideIds_AndFullAudit()
    {
        var (sut, client, _) = Create();

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        Assert.False(outcome.FallbackUsed);
        Assert.Null(outcome.FailureReason);
        Assert.Equal(TradeAction.Buy, outcome.Proposal.Action);
        Assert.Equal("AAPL", outcome.Proposal.Ticker);
        Assert.Equal(InstrumentType.Equity, outcome.Proposal.InstrumentType);
        Assert.Equal(0.79, outcome.Proposal.Confidence, 6);
        Assert.Equal(0.03m, outcome.Proposal.SuggestedStopLossPct);
        Assert.Equal(0.06m, outcome.Proposal.SuggestedTakeProfitPct);
        Assert.Equal(48, outcome.Proposal.ExpectedHorizonHours);
        Assert.Equal(Fixtures.EventId, outcome.Proposal.EventId);
        Assert.Equal(DecisionSwarmOrchestrator.CreateProposalId(Fixtures.EventId, "v3.0"), outcome.Proposal.ProposalId);
        Assert.StartsWith("prop_", outcome.Proposal.ProposalId, StringComparison.Ordinal);
        Assert.NotEqual("prop_llm_supplied", outcome.Proposal.ProposalId);

        Assert.Equal(Fixtures.FactJson, outcome.FactJson);
        Assert.Equal(Fixtures.BullJson, outcome.BullJson);
        Assert.Equal(Fixtures.BearJson, outcome.BearJson);
        Assert.Equal(Fixtures.SoftRiskJson, outcome.SoftRiskJson);
        Assert.Equal(Fixtures.JudgeJson(), outcome.JudgeJson);
        Assert.Equal("v3.0", outcome.PromptVersion);
        Assert.Equal(500, outcome.TotalTokens);
        Assert.True(outcome.TotalLatencyMs >= 0);
        Assert.Equal(5, client.TotalCalls);
    }

    [Fact]
    public async Task AgentCalls_UseSpecTemperatures_AndExplicitTiers()
    {
        var (sut, client, _) = Create();

        await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        (AgentRole Role, double Temperature, LlmTier Tier)[] expected =
        [
            (AgentRole.FactAnalyst, 0.1, LlmTier.Fast),
            (AgentRole.BullAgent, 0.2, LlmTier.Fast),
            (AgentRole.BearAgent, 0.2, LlmTier.Fast),
            (AgentRole.SoftRiskAgent, 0.1, LlmTier.Fast),
            (AgentRole.JudgeAgent, 0.1, LlmTier.Judge)
        ];

        foreach (var (role, temperature, tier) in expected)
        {
            var request = client.Request(role);
            Assert.Equal(temperature, request.Temperature, 6);
            Assert.Equal(tier, request.Tier);
            Assert.Equal(AgentJsonSchemas.For(role), request.JsonSchema);
            Assert.Equal(AgentJsonSchemas.NameFor(role), request.EffectiveSchemaName);
            Assert.Contains(PromptBuilder.InjectionGuard, request.SystemPrompt, StringComparison.Ordinal);
        }

        Assert.Equal(AgentRole.FactAnalyst, client.Requests.First().Role);
        Assert.Equal(AgentRole.JudgeAgent, client.Requests.Last().Role);
    }

    [Fact]
    public async Task SoftRiskAgent_ReceivesUpcomingEvents_AndQuote()
    {
        var (sut, client, _) = Create();

        await sut.EvaluateAsync(Fixtures.Request(["AAPL EARNINGS 2026-11-28T21:00:00Z (Finnhub)"]), CancellationToken.None)
            .WaitAsync(TestTimeout);

        var soft = client.Request(AgentRole.SoftRiskAgent).UserPrompt;
        Assert.Contains("AAPL EARNINGS 2026-11-28T21:00:00Z (Finnhub)", soft, StringComparison.Ordinal);
        Assert.Contains("spreadBps=1.75", soft, StringComparison.Ordinal);
        Assert.Contains("proposalId: " + DecisionSwarmOrchestrator.CreateProposalId(Fixtures.EventId, "v3.0"),
            client.Request(AgentRole.JudgeAgent).UserPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("your_key")]
    public async Task UnconfiguredOptions_FailClosedWithoutAnyLlmCall(string key)
    {
        var (sut, client, _) = Create(new LlmOptions { FastApiKey = key, JudgeApiKey = key });

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        AssertFailClosed(outcome, "llm_unconfigured");
        Assert.Equal(0, client.TotalCalls);
        Assert.Null(outcome.FactJson);
        Assert.Null(outcome.BullJson);
        Assert.Equal(0, outcome.TotalTokens);
        Assert.Equal("AAPL", outcome.Proposal.Ticker);
    }

    [Theory]
    [InlineData("""{"isActionable":false,"eventCategory":"EARNINGS","summary":"Old.","primaryTicker":"AAPL","instrumentType":"EQUITY","affectedTickers":["AAPL"],"noveltyScore":0.1,"reasoning":"Recycled."}""")]
    [InlineData("""{"isActionable":true,"eventCategory":"NOISE","summary":"Chatter.","primaryTicker":"AAPL","instrumentType":"EQUITY","affectedTickers":["AAPL"],"noveltyScore":0.2,"reasoning":"Noise."}""")]
    [InlineData(Fixtures.NonActionableFactJson)]
    public async Task NonActionableFact_ShortCircuitsToHold_WithoutStage2Or3(string factJson)
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.FactAnalyst] = (_, _) => Task.FromResult(ScriptedLlmClient.Result(factJson, 40));

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal(TradeAction.Hold, outcome.Proposal.Action);
        Assert.Equal(0, outcome.Proposal.Confidence);
        Assert.Equal(factJson, outcome.FactJson);
        Assert.Null(outcome.BullJson);
        Assert.Null(outcome.BearJson);
        Assert.Null(outcome.SoftRiskJson);
        Assert.NotNull(outcome.JudgeJson);
        Assert.Equal(40, outcome.TotalTokens);
        Assert.Equal(1, client.TotalCalls);
        Assert.Equal(1, client.Calls(AgentRole.FactAnalyst));
        Assert.Equal("AAPL", outcome.Proposal.Ticker);
    }

    [Fact]
    public async Task SoftVeto_ForcesHold_EvenWhenJudgeSaysBuy()
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.SoftRiskAgent] = (_, _) => Task.FromResult(ScriptedLlmClient.Result(Fixtures.SoftVetoJson));
        client.Handlers[AgentRole.JudgeAgent] = (_, _) => Task.FromResult(ScriptedLlmClient.Result(Fixtures.JudgeJson("BUY")));

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal(TradeAction.Hold, outcome.Proposal.Action);
        Assert.StartsWith("[SoftVeto override: HOLD]", outcome.Proposal.Rationale, StringComparison.Ordinal);
        Assert.False(outcome.FallbackUsed);
        Assert.Null(outcome.FailureReason);
        Assert.Equal(Fixtures.SoftVetoJson, outcome.SoftRiskJson);
        Assert.Equal(Fixtures.JudgeJson("BUY"), outcome.JudgeJson);
        Assert.Equal(5, client.TotalCalls);
    }

    [Fact]
    public async Task SoftVeto_ForcesHold_WhenJudgeSaysSell()
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.SoftRiskAgent] = (_, _) => Task.FromResult(ScriptedLlmClient.Result(Fixtures.SoftVetoJson));
        client.Handlers[AgentRole.JudgeAgent] = (_, _) => Task.FromResult(ScriptedLlmClient.Result(Fixtures.JudgeJson("SELL")));

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal(TradeAction.Hold, outcome.Proposal.Action);
    }

    [Fact]
    public async Task Stage2_RunsBullBearSoftRiskInParallel()
    {
        var (sut, client, _) = Create();
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<int, CancellationToken, Task<LlmCompletionResult>> Barrier(AgentRole role) => async (_, ct) =>
        {
            if (Interlocked.Increment(ref started) == 3)
            {
                allStarted.TrySetResult();
            }

            await allStarted.Task.WaitAsync(ct);
            return ScriptedLlmClient.Result(Fixtures.DefaultJson(role));
        };

        client.Handlers[AgentRole.BullAgent] = Barrier(AgentRole.BullAgent);
        client.Handlers[AgentRole.BearAgent] = Barrier(AgentRole.BearAgent);
        client.Handlers[AgentRole.SoftRiskAgent] = Barrier(AgentRole.SoftRiskAgent);

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        Assert.False(outcome.FallbackUsed);
        Assert.Equal(TradeAction.Buy, outcome.Proposal.Action);
    }

    [Fact]
    public async Task TransientError_IsRetriedOnce_ThenSucceeds()
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.BearAgent] = (attempt, _) => attempt == 1
            ? throw new LlmException("anthropic: HTTP 503.", isTransient: true)
            : Task.FromResult(ScriptedLlmClient.Result(Fixtures.BearJson));

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        Assert.False(outcome.FallbackUsed);
        Assert.Equal(TradeAction.Buy, outcome.Proposal.Action);
        Assert.Equal(2, client.Calls(AgentRole.BearAgent));
    }

    [Fact]
    public async Task TransientError_TwiceFailsClosed_AfterExactlyOneRetry()
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.FactAnalyst] = (_, _) =>
            throw new LlmException("openai_compatible: HTTP 429.", isTransient: true);

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        AssertFailClosed(outcome, "FactAnalyst: transient_error after 1 retry");
        Assert.Equal(2, client.Calls(AgentRole.FactAnalyst));
        Assert.Equal(2, client.TotalCalls);
    }

    [Fact]
    public async Task PermanentError_FailsClosedWithoutRetry()
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.JudgeAgent] = (_, _) =>
            throw new LlmException("anthropic: HTTP 401.", isTransient: false);

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        AssertFailClosed(outcome, "JudgeAgent: llm_error");
        Assert.Equal(1, client.Calls(AgentRole.JudgeAgent));
        Assert.NotNull(outcome.BullJson);
        Assert.NotNull(outcome.BearJson);
        Assert.NotNull(outcome.SoftRiskJson);
    }

    [Fact]
    public async Task UnexpectedException_FailsClosed()
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.FactAnalyst] = (_, _) => throw new InvalidOperationException("boom");

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        AssertFailClosed(outcome, "FactAnalyst: unexpected_error");
        Assert.Equal(1, client.TotalCalls);
    }

    [Fact]
    public async Task InvalidJson_IsRetriedOnce_ThenSucceeds_AndTokensAccumulate()
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.BullAgent] = (attempt, _) => Task.FromResult(attempt == 1
            ? ScriptedLlmClient.Result("""{"direction":"UP","conviction":"high"}""", 60)
            : ScriptedLlmClient.Result(Fixtures.BullJson, 100));

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        Assert.False(outcome.FallbackUsed);
        Assert.Equal(2, client.Calls(AgentRole.BullAgent));
        Assert.Equal(560, outcome.TotalTokens);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"action":"BUY"}""")]
    [InlineData("""{"proposalId":"p","eventId":"e","action":"YOLO","ticker":"AAPL","instrumentType":"EQUITY","confidence":0.7,"expectedHorizonHours":48,"suggestedStopLossPct":0.03,"suggestedTakeProfitPct":0.06,"rationale":"r","dissentSummary":"d"}""")]
    [InlineData("""{"proposalId":"p","eventId":"e","action":"BUY","ticker":"AAPL","instrumentType":"EQUITY","confidence":1.7,"expectedHorizonHours":48,"suggestedStopLossPct":0.03,"suggestedTakeProfitPct":0.06,"rationale":"r","dissentSummary":"d"}""")]
    [InlineData("""{"proposalId":"p","eventId":"e","action":"BUY","ticker":"AAPL","instrumentType":"EQUITY","confidence":0.7,"expectedHorizonHours":48,"suggestedStopLossPct":3,"suggestedTakeProfitPct":0.06,"rationale":"r","dissentSummary":"d"}""")]
    [InlineData("""{"proposalId":"p","eventId":"e","action":"BUY","ticker":"AAPL","instrumentType":"EQUITY","confidence":"0.7","expectedHorizonHours":48,"suggestedStopLossPct":0.03,"suggestedTakeProfitPct":0.06,"rationale":"r","dissentSummary":"d"}""")]
    public async Task InvalidJudgeJson_TwiceFailsClosedToHold(string judgeJson)
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.JudgeAgent] = (_, _) => Task.FromResult(ScriptedLlmClient.Result(judgeJson));

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        AssertFailClosed(outcome, "JudgeAgent: invalid_json after 1 retry");
        Assert.Equal(2, client.Calls(AgentRole.JudgeAgent));
        Assert.NotNull(outcome.FactJson);
        Assert.NotNull(outcome.SoftRiskJson);
        Assert.Equal(600, outcome.TotalTokens);
    }

    [Fact]
    public async Task InvalidFactJson_FailsClosed_BeforeStage2()
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.FactAnalyst] = (_, _) => Task.FromResult(ScriptedLlmClient.Result("""{"isActionable":"maybe"}"""));

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        AssertFailClosed(outcome, "FactAnalyst: invalid_json");
        Assert.Equal(2, client.TotalCalls);
        Assert.Null(outcome.FactJson);
    }

    [Fact]
    public async Task JudgeTickerOutsideAllowlist_FailsClosed()
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.JudgeAgent] = (_, _) => Task.FromResult(ScriptedLlmClient.Result(Fixtures.JudgeJson("BUY", "TSLA")));

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        AssertFailClosed(outcome, "judge_ticker_not_allowlisted");
        Assert.Equal("AAPL", outcome.Proposal.Ticker);
    }

    [Fact]
    public async Task JudgeSectorEtf_IsNormalizedToSectorEtfInstrument()
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.JudgeAgent] = (_, _) => Task.FromResult(ScriptedLlmClient.Result(Fixtures.JudgeJson("BUY", "xlk")));

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal(TradeAction.Buy, outcome.Proposal.Action);
        Assert.Equal("XLK", outcome.Proposal.Ticker);
        Assert.Equal(InstrumentType.SectorEtf, outcome.Proposal.InstrumentType);
    }

    [Fact]
    public async Task Stage2Failure_CancelsSiblings_AndReportsRootCause()
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.BullAgent] = (_, ct) => Hang(ct);
        client.Handlers[AgentRole.SoftRiskAgent] = (_, ct) => Hang(ct);
        client.Handlers[AgentRole.BearAgent] = (_, _) => throw new LlmException("openai_compatible: HTTP 400.", false);

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        AssertFailClosed(outcome, "BearAgent: llm_error");
        Assert.Null(outcome.BullJson);
        Assert.Null(outcome.SoftRiskJson);
        Assert.Equal(0, client.Calls(AgentRole.JudgeAgent));
        Assert.Equal(1, client.Calls(AgentRole.BullAgent));
    }

    [Fact]
    public async Task PerAgentTimeout_8s_IsRetriedOnce_ThenFailsClosed()
    {
        var (sut, client, time) = Create();
        client.Handlers[AgentRole.FactAnalyst] = (_, ct) => Hang(ct);

        var outcome = await time.DriveAsync(sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None), client,
            TimeSpan.FromMilliseconds(500));

        AssertFailClosed(outcome, "FactAnalyst: timeout after 1 retry");
        Assert.Equal(2, client.Calls(AgentRole.FactAnalyst));
        Assert.InRange(time.Elapsed, TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(19));
        Assert.InRange(outcome.TotalLatencyMs, 16_000, 19_000);
    }

    [Fact]
    public async Task PerAgentTimeout_FirstAttemptOnly_RecoversOnRetry()
    {
        var (sut, client, time) = Create();
        client.Handlers[AgentRole.FactAnalyst] = (attempt, ct) => attempt == 1
            ? Hang(ct)
            : Task.FromResult(ScriptedLlmClient.Result(Fixtures.FactJson));

        var outcome = await time.DriveAsync(sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None), client,
            TimeSpan.FromMilliseconds(500));

        Assert.False(outcome.FallbackUsed);
        Assert.Equal(TradeAction.Buy, outcome.Proposal.Action);
        Assert.Equal(2, client.Calls(AgentRole.FactAnalyst));
        Assert.InRange(time.Elapsed, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TotalDebateBudget_20s_FailsClosed_WithoutRetry()
    {
        var (sut, client, time) = Create();
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var captured = role;
            client.Handlers[captured] = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(7), time, ct);
                return ScriptedLlmClient.Result(Fixtures.DefaultJson(captured));
            };
        }

        var outcome = await time.DriveAsync(sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None), client,
            TimeSpan.FromMilliseconds(250));

        AssertFailClosed(outcome, "JudgeAgent: debate_budget_exhausted");
        Assert.Equal(1, client.Calls(AgentRole.JudgeAgent));
        Assert.NotNull(outcome.BullJson);
        Assert.NotNull(outcome.BearJson);
        Assert.NotNull(outcome.SoftRiskJson);
        Assert.Equal(400, outcome.TotalTokens);
        Assert.InRange(time.Elapsed, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(21));
    }

    [Fact]
    public async Task Stage1_NonCooperativeClient_IgnoresCancellation_AbortsAtTimeoutAndFailsClosed()
    {
        var (sut, client, time) = Create();
        var neverCompletes = new TaskCompletionSource<LlmCompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Handlers[AgentRole.FactAnalyst] = (_, _) => neverCompletes.Task;

        var outcome = await time.DriveAsync(sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None), client,
            TimeSpan.FromMilliseconds(500));

        AssertFailClosed(outcome, "FactAnalyst: timeout after 1 retry");
        Assert.Equal(2, client.Calls(AgentRole.FactAnalyst));
        Assert.InRange(time.Elapsed, TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(19));
    }

    [Fact]
    public async Task Stage1_NonCooperativeClient_LateFaultAfterAbandonment_IsObserved()
    {
        var (sut, client, time) = Create();
        var abandonedTcs = new TaskCompletionSource<LlmCompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Handlers[AgentRole.FactAnalyst] = (attempt, _) => attempt == 1
            ? abandonedTcs.Task
            : Task.FromResult(ScriptedLlmClient.Result(Fixtures.FactJson));

        var observedUnobserved = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, args) =>
        {
            if (args.Exception.InnerExceptions.Any(ex => ex.Message == "late-abandon-sentinel"))
            {
                observedUnobserved.Add(args.Exception);
            }
        };

        TaskScheduler.UnobservedTaskException += onUnobserved;
        try
        {
            var outcome = await time.DriveAsync(sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None), client,
                TimeSpan.FromMilliseconds(500));

            Assert.False(outcome.FallbackUsed);
            Assert.Equal(2, client.Calls(AgentRole.FactAnalyst));

            // Now fault the abandoned task after the orchestrator moved on; ObserveAbandoned must observe it.
            abandonedTcs.SetException(new InvalidOperationException("late-abandon-sentinel"));
            await Task.Yield();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Empty(observedUnobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }
    }

    [Fact]
    public async Task Stage1_NonCooperativeClient_CompletesLateAfterBudgetDeadline_IsRejected()
    {
        var (sut, client, time) = Create();
        var gate = new TaskCompletionSource<LlmCompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Handlers[AgentRole.FactAnalyst] = (_, _) => gate.Task;

        var evaluation = sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None);
        var sw = Stopwatch.StartNew();
        while (client.InFlight == 0 && sw.Elapsed < TestTimeout)
        {
            await Task.Delay(1);
        }

        // Advance past the 20s debate deadline, then resolve the non-cooperative task late.
        time.Advance(TimeSpan.FromSeconds(21));
        gate.TrySetResult(ScriptedLlmClient.Result(Fixtures.FactJson));

        var outcome = await evaluation.WaitAsync(TestTimeout);

        AssertFailClosed(outcome, "FactAnalyst: debate_budget_exhausted");
        Assert.Equal(1, client.Calls(AgentRole.FactAnalyst));
    }

    [Fact]
    public async Task Stage2_ParallelNonCooperativeClient_OneAgentHangs_AbortsWhenAllAndFailsClosed()
    {
        var (sut, client, time) = Create();
        var neverCompletes = new TaskCompletionSource<LlmCompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Handlers[AgentRole.BullAgent] = (_, _) => neverCompletes.Task;

        var outcome = await time.DriveAsync(sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None), client,
            TimeSpan.FromMilliseconds(500));

        AssertFailClosed(outcome, "BullAgent: timeout after 1 retry");
        Assert.Equal(2, client.Calls(AgentRole.BullAgent));
        Assert.Equal(1, client.Calls(AgentRole.BearAgent));
        Assert.Equal(1, client.Calls(AgentRole.SoftRiskAgent));
        Assert.Equal(0, client.Calls(AgentRole.JudgeAgent));
        Assert.InRange(time.Elapsed, TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(19));
    }

    [Fact]
    public async Task Stage2_ParallelNonCooperativeClient_SiblingFailureAbortsHungPeersImmediately()
    {
        var (sut, client, _) = Create();
        var neverCompletes = new TaskCompletionSource<LlmCompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Handlers[AgentRole.BullAgent] = (_, _) => neverCompletes.Task;
        client.Handlers[AgentRole.SoftRiskAgent] = (_, _) => neverCompletes.Task;
        client.Handlers[AgentRole.BearAgent] = (_, _) => throw new LlmException("openai_compatible: HTTP 400.", false);

        var outcome = await sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None).WaitAsync(TestTimeout);

        AssertFailClosed(outcome, "BearAgent: llm_error");
        Assert.Null(outcome.BullJson);
        Assert.Null(outcome.SoftRiskJson);
        Assert.Equal(0, client.Calls(AgentRole.JudgeAgent));
    }

    [Fact]
    public async Task Stage2_ParallelNonCooperativeClient_LateCompletionAfter20sBudget_IsRejected()
    {
        var (sut, client, time) = Create();
        client.Handlers[AgentRole.FactAnalyst] = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(7), time, ct);
            return ScriptedLlmClient.Result(Fixtures.FactJson);
        };
        client.Handlers[AgentRole.SoftRiskAgent] = async (_, _) =>
        {
            // Ignores CancellationToken and finishes at t = 7s + 14s = 21s (after the 20s debate budget).
            await Task.Delay(TimeSpan.FromSeconds(14), time, CancellationToken.None);
            return ScriptedLlmClient.Result(Fixtures.SoftRiskJson);
        };

        var outcome = await time.DriveAsync(sut.EvaluateAsync(Fixtures.Request(), CancellationToken.None), client,
            TimeSpan.FromMilliseconds(250));

        AssertFailClosed(outcome, "SoftRiskAgent: debate_budget_exhausted");
        Assert.Equal(0, client.Calls(AgentRole.JudgeAgent));
        Assert.InRange(time.Elapsed, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(21));
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.FactAnalyst] = (_, ct) => Hang(ct);
        using var cts = new CancellationTokenSource();

        var evaluation = sut.EvaluateAsync(Fixtures.Request(), cts.Token);
        var sw = Stopwatch.StartNew();
        while (client.InFlight == 0 && sw.Elapsed < TestTimeout)
        {
            await Task.Delay(1);
        }

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation.WaitAsync(TestTimeout));
        Assert.Equal(1, client.Calls(AgentRole.FactAnalyst));
    }

    [Fact]
    public async Task CallerCancellation_DuringStage2_Propagates()
    {
        var (sut, client, _) = Create();
        client.Handlers[AgentRole.BullAgent] = (_, ct) => Hang(ct);
        client.Handlers[AgentRole.BearAgent] = (_, ct) => Hang(ct);
        client.Handlers[AgentRole.SoftRiskAgent] = (_, ct) => Hang(ct);
        using var cts = new CancellationTokenSource();

        var evaluation = sut.EvaluateAsync(Fixtures.Request(), cts.Token);
        var sw = Stopwatch.StartNew();
        while (client.InFlight < 3 && sw.Elapsed < TestTimeout)
        {
            await Task.Delay(1);
        }

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation.WaitAsync(TestTimeout));
    }

    [Fact]
    public void ProposalId_IsDeterministic_AndBounded()
    {
        var a = DecisionSwarmOrchestrator.CreateProposalId("evt_1", "v3.0");
        var b = DecisionSwarmOrchestrator.CreateProposalId("evt_1", "v3.0");
        var c = DecisionSwarmOrchestrator.CreateProposalId("evt_2", "v3.0");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Matches("^prop_[0-9a-f]{16}$", a);
    }

    [Fact]
    public void SpecConstants_MatchSpec003()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), DecisionSwarmOrchestrator.PerAgentTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), DecisionSwarmOrchestrator.TotalDebateBudget);
        Assert.Equal(1, DecisionSwarmOrchestrator.MaxRetriesPerAgent);
    }

    [Fact]
    public void Assembly_HasNoReferenceToExecutionRiskOrBrokerSdks()
    {
        var references = typeof(DecisionSwarmOrchestrator).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.Contains("SentiX.Domain", references);
        Assert.DoesNotContain(references, name =>
            name.StartsWith("SentiX.Execution", StringComparison.Ordinal) ||
            name.StartsWith("SentiX.Risk", StringComparison.Ordinal) ||
            name.Contains("Alpaca", StringComparison.OrdinalIgnoreCase));
    }
}
