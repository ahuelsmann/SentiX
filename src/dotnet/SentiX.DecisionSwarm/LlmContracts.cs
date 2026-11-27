namespace SentiX.DecisionSwarm;

/// <summary>The five Decision-Swarm committee roles (Spec 003 §2).</summary>
public enum AgentRole { FactAnalyst, BullAgent, BearAgent, SoftRiskAgent, JudgeAgent }

/// <summary>Unified structured-JSON completion abstraction (Spec 007 §2).</summary>
public interface ILlmClient
{
    Task<LlmCompletionResult> CompleteJsonAsync(LlmCompletionRequest request, CancellationToken cancellationToken);
}

public sealed record LlmCompletionRequest(
    LlmTier Tier,
    AgentRole Role,
    string SystemPrompt,
    string UserPrompt,
    double Temperature,
    int MaxOutputTokens = 1024);

/// <summary>Completion result. <see cref="Json"/> is a single, already unwrapped and syntax-validated JSON object.</summary>
public sealed record LlmCompletionResult(string Json, int PromptTokens, int CompletionTokens, int TotalTokens, string Model);

/// <summary>LLM transport/provider failure. Messages never contain API keys or response bodies.</summary>
public class LlmException : Exception
{
    public LlmException(string message, bool isTransient, Exception? innerException = null)
        : base(message, innerException) => IsTransient = isTransient;

    /// <summary>True when a single retry is permitted (timeouts, 408/429/5xx, network errors, malformed output).</summary>
    public bool IsTransient { get; }
}

/// <summary>Provider returned an unusable payload (not JSON, empty, oversized). Retryable once.</summary>
public sealed class LlmResponseFormatException : LlmException
{
    public LlmResponseFormatException(string message, Exception? innerException = null)
        : base(message, isTransient: true, innerException)
    {
    }
}
