namespace SentiX.DecisionSwarm;

/// <summary>
/// Routes each request to the provider client configured for its explicit <see cref="LlmTier"/>
/// (<c>Llm:FastProvider</c> / <c>Llm:JudgeProvider</c>).
/// </summary>
public sealed class TieredLlmClient : ILlmClient
{
    private readonly LlmOptions _options;
    private readonly ILlmClient _openAiCompatible;
    private readonly ILlmClient _anthropic;

    public TieredLlmClient(LlmOptions options, OpenAiCompatibleLlmClient openAiCompatible, AnthropicLlmClient anthropic)
        : this(options, (ILlmClient)openAiCompatible, anthropic)
    {
    }

    internal TieredLlmClient(LlmOptions options, ILlmClient openAiCompatible, ILlmClient anthropic)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _openAiCompatible = openAiCompatible ?? throw new ArgumentNullException(nameof(openAiCompatible));
        _anthropic = anthropic ?? throw new ArgumentNullException(nameof(anthropic));
    }

    public Task<LlmCompletionResult> CompleteJsonAsync(LlmCompletionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_options.TryGetTier(request.Tier, out var tier))
        {
            throw new LlmException($"Tier {request.Tier} is not configured.", isTransient: false);
        }

        var client = tier.Provider == LlmProviders.Anthropic ? _anthropic : _openAiCompatible;
        return client.CompleteJsonAsync(request, cancellationToken);
    }
}
