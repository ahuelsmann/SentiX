using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace SentiX.DecisionSwarm;

public static class DecisionSwarmServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LlmOptions"/>, the provider clients, <see cref="TieredLlmClient"/> and
    /// <see cref="IDecisionSwarmOrchestrator"/>. No network traffic happens at registration time; when the options
    /// are unconfigured the orchestrator fails closed to <c>HOLD</c> without calling any provider.
    /// </summary>
    public static IServiceCollection AddSentiXDecisionSwarm(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(_ => LlmOptions.FromConfiguration(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => new PromptBuilder());
        services.TryAddSingleton<ILlmClient>(sp =>
        {
            var options = sp.GetRequiredService<LlmOptions>();
            var http = CreateHttpClient();
            return new TieredLlmClient(options, new OpenAiCompatibleLlmClient(http, options),
                new AnthropicLlmClient(http, options));
        });
        services.TryAddSingleton<IDecisionSwarmOrchestrator>(sp => new DecisionSwarmOrchestrator(
            sp.GetRequiredService<ILlmClient>(),
            sp.GetRequiredService<LlmOptions>(),
            sp.GetRequiredService<PromptBuilder>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<DecisionSwarmOrchestrator>>()));
        return services;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        // Per-call deadlines are enforced by the orchestrator (8 s / 20 s); this is only a safety net.
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
}
