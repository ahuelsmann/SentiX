using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentiX.Domain;

namespace SentiX.DecisionSwarm;

/// <summary>Input for one committee debate (Spec 007 §2/§3).</summary>
public sealed record DecisionDebateRequest(
    MarketEvent Event,
    FastPathResult FastPath,
    IReadOnlyList<string> UpcomingEventsWithin48h,
    MarketQuoteSnapshot? Quote);

/// <summary>Result of one committee debate (Spec 007 §2). <see cref="Proposal"/> is always non-null.</summary>
public sealed record DecisionDebateOutcome(
    TradeProposal Proposal,
    string? FactJson,
    string? BullJson,
    string? BearJson,
    string? SoftRiskJson,
    string? JudgeJson,
    string PromptVersion,
    int TotalTokens,
    long TotalLatencyMs,
    bool FallbackUsed,
    string? FailureReason);

public interface IDecisionSwarmOrchestrator
{
    /// <summary>
    /// Runs the 3-stage debate. Never throws for LLM/config failures (fails closed to <c>HOLD</c>); only caller
    /// cancellation is propagated as <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<DecisionDebateOutcome> EvaluateAsync(DecisionDebateRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Spec 003 / Spec 007 §2 Decision-Swarm DAG: FactAnalyst → (Bull ∥ Bear ∥ SoftRisk) → Judge → <see cref="TradeProposal"/>.
/// Emits DTOs only; has no knowledge of execution or broker APIs.
/// </summary>
public sealed class DecisionSwarmOrchestrator : IDecisionSwarmOrchestrator
{
    public static readonly TimeSpan PerAgentTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan TotalDebateBudget = TimeSpan.FromSeconds(20);
    public const int MaxRetriesPerAgent = 1;

    public const double FactAnalystTemperature = 0.1;
    public const double BullAgentTemperature = 0.2;
    public const double BearAgentTemperature = 0.2;
    public const double SoftRiskAgentTemperature = 0.1;
    public const double JudgeAgentTemperature = 0.1;

    private readonly ILlmClient _client;
    private readonly LlmOptions _options;
    private readonly PromptBuilder _prompts;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public DecisionSwarmOrchestrator(ILlmClient client, LlmOptions options, PromptBuilder? promptBuilder = null,
        TimeProvider? timeProvider = null, ILogger<DecisionSwarmOrchestrator>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _prompts = promptBuilder ?? new PromptBuilder();
        _time = timeProvider ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string PromptVersion => _prompts.PromptVersion;

    /// <summary>Deterministic proposal id derived from the event id and prompt version.</summary>
    public static string CreateProposalId(string eventId, string promptVersion)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{eventId}|{promptVersion}"));
        return "prop_" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    public async Task<DecisionDebateOutcome> EvaluateAsync(DecisionDebateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Event);
        ArgumentNullException.ThrowIfNull(request.FastPath);
        cancellationToken.ThrowIfCancellationRequested();

        var state = new DebateState(request, CreateProposalId(request.Event.EventId, _prompts.PromptVersion),
            _time.GetTimestamp());

        if (!_options.IsConfigured)
        {
            return Fail(state, "llm_unconfigured");
        }

        using var budget = new CancellationTokenSource(TotalDebateBudget, _time);
        using var debate = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var scope = new CallScope(cancellationToken, budget.Token, debate.Token);

        try
        {
            // Stage 1 — FactAnalyst (sequential).
            var factPrompt = _prompts.BuildFactAnalyst(request.Event, request.FastPath);
            var (fact, factJson) = await RunAgentAsync<FactAnalysis>(AgentRole.FactAnalyst, LlmTier.Fast, factPrompt,
                FactAnalystTemperature, ValidateFact, state, scope).ConfigureAwait(false);
            state.FactJson = factJson;

            if (!fact.IsActionable || fact.EventCategory == EventCategory.Noise)
            {
                var hold = SyntheticHold(state, fact.PrimaryTicker,
                    $"FactAnalyst classified event as non-actionable ({fact.EventCategory}).");
                return Complete(state, hold, judgeJson: Serialize(hold), fallbackUsed: false, failureReason: null);
            }

            // Stage 2 — Bull ∥ Bear ∥ SoftRisk.
            using var stage = CancellationTokenSource.CreateLinkedTokenSource(debate.Token);
            var stageScope = scope with { Effective = stage.Token };
            var bullPrompt = _prompts.BuildBullAgent(request.Event, request.FastPath, fact, request.Quote);
            var bearPrompt = _prompts.BuildBearAgent(request.Event, request.FastPath, fact, request.Quote);
            var softPrompt = _prompts.BuildSoftRiskAgent(request.Event, request.FastPath, fact,
                request.UpcomingEventsWithin48h ?? [], request.Quote);

            var bullTask = CancelSiblingsOnFailure(RunAgentAsync<BullAnalysis>(AgentRole.BullAgent, LlmTier.Fast,
                bullPrompt, BullAgentTemperature, ValidateBull, state, stageScope), stage);
            var bearTask = CancelSiblingsOnFailure(RunAgentAsync<BearAnalysis>(AgentRole.BearAgent, LlmTier.Fast,
                bearPrompt, BearAgentTemperature, ValidateBear, state, stageScope), stage);
            var softTask = CancelSiblingsOnFailure(RunAgentAsync<SoftRiskAnalysis>(AgentRole.SoftRiskAgent, LlmTier.Fast,
                softPrompt, SoftRiskAgentTemperature, static _ => { }, state, stageScope), stage);

            try
            {
                await Task.WhenAll(bullTask, bearTask, softTask).ConfigureAwait(false);
            }
            finally
            {
                if (bullTask.IsCompletedSuccessfully) state.BullJson = bullTask.Result.Json;
                if (bearTask.IsCompletedSuccessfully) state.BearJson = bearTask.Result.Json;
                if (softTask.IsCompletedSuccessfully) state.SoftRiskJson = softTask.Result.Json;
            }

            var bull = bullTask.Result.Value;
            var bear = bearTask.Result.Value;
            var softRisk = softTask.Result.Value;

            // Stage 3 — Judge (sequential).
            var judgePrompt = _prompts.BuildJudgeAgent(request.Event, request.FastPath, state.ProposalId, fact, bull, bear,
                softRisk, request.Quote);
            var (judge, judgeJson) = await RunAgentAsync<JudgeOutput>(AgentRole.JudgeAgent, LlmTier.Judge, judgePrompt,
                JudgeAgentTemperature, ValidateJudge, state, scope).ConfigureAwait(false);

            return BuildFinalOutcome(state, judge, judgeJson, softRisk);
        }
        catch (AgentFailedException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Fail(state, state.FirstFailure ?? ex.Reason);
        }
    }

    private DecisionDebateOutcome BuildFinalOutcome(DebateState state, JudgeOutput judge, string judgeJson,
        SoftRiskAnalysis softRisk)
    {
        var fastPath = state.Request.FastPath;
        var ticker = (judge.Ticker ?? string.Empty).Trim().ToUpperInvariant();
        var etfs = new HashSet<string>(fastPath.MatchedSectorEtfs, StringComparer.OrdinalIgnoreCase);
        var equities = new HashSet<string>(fastPath.MatchedTickers, StringComparer.OrdinalIgnoreCase);
        var allowlisted = etfs.Contains(ticker) || equities.Contains(ticker);

        if (judge.Action != TradeAction.Hold && !allowlisted)
        {
            state.JudgeJson = judgeJson;
            return Fail(state, "judge_ticker_not_allowlisted");
        }

        var instrumentType = etfs.Contains(ticker) ? InstrumentType.SectorEtf
            : equities.Contains(ticker) ? InstrumentType.Equity
            : judge.InstrumentType;

        var proposal = new TradeProposal(
            state.ProposalId,
            state.Request.Event.EventId,
            judge.Action,
            ticker,
            instrumentType,
            judge.Confidence,
            judge.ExpectedHorizonHours,
            judge.SuggestedStopLossPct,
            judge.SuggestedTakeProfitPct,
            judge.Rationale,
            judge.DissentSummary);

        // Hard Rule (Spec 003 §2, Spec 007 §2.1): SoftVeto always forces HOLD, independent of the Judge.
        if (softRisk.SoftVeto && proposal.Action != TradeAction.Hold)
        {
            _logger.LogInformation("SoftRiskAgent veto forced HOLD for event {EventId} (judge proposed {Action}).",
                state.Request.Event.EventId, proposal.Action);
            proposal = proposal with
            {
                Action = TradeAction.Hold,
                Rationale = "[SoftVeto override: HOLD] " + proposal.Rationale
            };
        }

        return Complete(state, proposal, judgeJson, fallbackUsed: false, failureReason: null);
    }

    private async Task<(T Value, string Json)> RunAgentAsync<T>(AgentRole role, LlmTier tier, LlmPrompt prompt,
        double temperature, Action<T> validate, DebateState state, CallScope scope) where T : class
    {
        var request = new LlmCompletionRequest(tier, role, prompt.SystemPrompt, prompt.UserPrompt, temperature);
        var lastReason = "unknown";

        for (var attempt = 0; attempt <= MaxRetriesPerAgent; attempt++)
        {
            if (scope.Caller.IsCancellationRequested)
            {
                scope.Caller.ThrowIfCancellationRequested();
            }

            if (scope.Budget.IsCancellationRequested)
            {
                throw state.Failed($"{role}: debate_budget_exhausted");
            }

            if (scope.Effective.IsCancellationRequested)
            {
                throw new AgentFailedException($"{role}: cancelled");
            }

            using var attemptTimeout = new CancellationTokenSource(PerAgentTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(scope.Effective, attemptTimeout.Token);
            try
            {
                var result = await _client.CompleteJsonAsync(request, linked.Token).ConfigureAwait(false);
                state.AddTokens(result?.TotalTokens ?? 0);
                var json = LlmHttp.ExtractJsonObject(result?.Json);
                var value = JsonSerializer.Deserialize<T>(json, SentiXJson.Options)
                            ?? throw new JsonException("Agent returned JSON null.");
                validate(value);
                return (value, json);
            }
            catch (OperationCanceledException) when (scope.Caller.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (scope.Budget.IsCancellationRequested)
            {
                throw state.Failed($"{role}: debate_budget_exhausted");
            }
            catch (OperationCanceledException) when (attemptTimeout.IsCancellationRequested)
            {
                lastReason = "timeout";
            }
            catch (OperationCanceledException)
            {
                throw new AgentFailedException($"{role}: cancelled");
            }
            catch (LlmResponseFormatException)
            {
                lastReason = "invalid_json";
            }
            catch (LlmException ex) when (ex.IsTransient)
            {
                lastReason = "transient_error";
            }
            catch (LlmException ex)
            {
                throw state.Failed($"{role}: llm_error ({ex.Message})");
            }
            catch (JsonException)
            {
                lastReason = "invalid_json";
            }
            catch (Exception ex) when (ex is not AgentFailedException)
            {
                throw state.Failed($"{role}: unexpected_error ({ex.GetType().Name})");
            }

            _logger.LogWarning("Decision-Swarm agent {Role} attempt {Attempt} failed: {Reason} (event {EventId}).",
                role, attempt + 1, lastReason, state.Request.Event.EventId);
        }

        throw state.Failed($"{role}: {lastReason} after {MaxRetriesPerAgent} retry");
    }

    private static async Task<(T Value, string Json)> CancelSiblingsOnFailure<T>(Task<(T Value, string Json)> task,
        CancellationTokenSource stage)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            await stage.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private DecisionDebateOutcome Fail(DebateState state, string reason)
    {
        _logger.LogWarning("Decision-Swarm failed closed to HOLD for event {EventId}: {Reason}.",
            state.Request.Event.EventId, reason);
        var hold = SyntheticHold(state, null, $"Fail-closed fallback: {reason}");
        return Complete(state, hold, Serialize(hold), fallbackUsed: true, failureReason: reason);
    }

    private static TradeProposal SyntheticHold(DebateState state, string? tickerHint, string rationale)
    {
        var fastPath = state.Request.FastPath;
        var ticker = !string.IsNullOrWhiteSpace(tickerHint) ? tickerHint.Trim().ToUpperInvariant()
            : fastPath.MatchedTickers.FirstOrDefault()
              ?? fastPath.MatchedSectorEtfs.FirstOrDefault()
              ?? state.Request.Event.SourceTickers.FirstOrDefault()
              ?? string.Empty;
        var instrumentType = fastPath.MatchedSectorEtfs.Contains(ticker, StringComparer.OrdinalIgnoreCase)
            ? InstrumentType.SectorEtf
            : InstrumentType.Equity;
        return new TradeProposal(state.ProposalId, state.Request.Event.EventId, TradeAction.Hold, ticker, instrumentType,
            Confidence: 0, ExpectedHorizonHours: 0, SuggestedStopLossPct: 0m, SuggestedTakeProfitPct: 0m,
            Rationale: rationale, DissentSummary: string.Empty);
    }

    private DecisionDebateOutcome Complete(DebateState state, TradeProposal proposal, string? judgeJson,
        bool fallbackUsed, string? failureReason)
    {
        var latency = (long)_time.GetElapsedTime(state.StartTimestamp).TotalMilliseconds;
        return new DecisionDebateOutcome(proposal, state.FactJson, state.BullJson, state.BearJson, state.SoftRiskJson,
            judgeJson ?? state.JudgeJson, _prompts.PromptVersion, state.TotalTokens, latency, fallbackUsed, failureReason);
    }

    private static string Serialize(TradeProposal proposal) => JsonSerializer.Serialize(proposal, SentiXJson.Options);

    private static void ValidateFact(FactAnalysis fact)
    {
        RequireUnit(fact.NoveltyScore, "noveltyScore");
        if (fact.IsActionable && fact.EventCategory != EventCategory.Noise && string.IsNullOrWhiteSpace(fact.PrimaryTicker))
        {
            throw new JsonException("primaryTicker is required for actionable events.");
        }
    }

    private static void ValidateBull(BullAnalysis bull)
    {
        RequireUnit(bull.Conviction, "conviction");
        if (bull.ExpectedHorizonHours < 0)
        {
            throw new JsonException("expectedHorizonHours must be >= 0.");
        }
    }

    private static void ValidateBear(BearAnalysis bear)
    {
        RequireUnit(bear.CounterConviction, "counterConviction");
        RequireUnit(bear.PricedInLikelihood, "pricedInLikelihood");
    }

    private static void ValidateJudge(JudgeOutput judge)
    {
        RequireUnit(judge.Confidence, "confidence");
        if (judge.ExpectedHorizonHours < 0)
        {
            throw new JsonException("expectedHorizonHours must be >= 0.");
        }

        if (judge.SuggestedStopLossPct is < 0m or > 1m || judge.SuggestedTakeProfitPct is < 0m or > 1m)
        {
            throw new JsonException("Stop-loss / take-profit must be fractions in [0, 1].");
        }

        if (string.IsNullOrWhiteSpace(judge.Ticker))
        {
            throw new JsonException("ticker is required.");
        }
    }

    private static void RequireUnit(double value, string name)
    {
        if (double.IsNaN(value) || value is < 0 or > 1)
        {
            throw new JsonException($"{name} must be within [0, 1].");
        }
    }

    /// <summary>JudgeAgent JSON contract; proposalId/eventId are ignored and re-derived server-side.</summary>
    internal sealed record JudgeOutput(
        TradeAction Action, string Ticker, InstrumentType InstrumentType, double Confidence, int ExpectedHorizonHours,
        decimal SuggestedStopLossPct, decimal SuggestedTakeProfitPct, string Rationale, string DissentSummary);

    private sealed record CallScope(CancellationToken Caller, CancellationToken Budget, CancellationToken Effective);

    private sealed class AgentFailedException(string reason) : Exception(reason)
    {
        public string Reason { get; } = reason;
    }

    private sealed class DebateState(DecisionDebateRequest request, string proposalId, long startTimestamp)
    {
        private int _totalTokens;
        private string? _firstFailure;

        public DecisionDebateRequest Request { get; } = request;
        public string ProposalId { get; } = proposalId;
        public long StartTimestamp { get; } = startTimestamp;
        public string? FactJson { get; set; }
        public string? BullJson { get; set; }
        public string? BearJson { get; set; }
        public string? SoftRiskJson { get; set; }
        public string? JudgeJson { get; set; }
        public int TotalTokens => Volatile.Read(ref _totalTokens);
        public string? FirstFailure => Volatile.Read(ref _firstFailure);

        public void AddTokens(int tokens)
        {
            if (tokens > 0)
            {
                Interlocked.Add(ref _totalTokens, tokens);
            }
        }

        /// <summary>Records the root-cause failure (first wins) and returns the exception to throw.</summary>
        public AgentFailedException Failed(string reason)
        {
            Interlocked.CompareExchange(ref _firstFailure, reason, null);
            return new AgentFailedException(reason);
        }
    }
}
