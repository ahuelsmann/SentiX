using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SentiX.Risk;

namespace SentiX.Execution;

/// <summary>DI registration helpers for <c>SentiX.Execution</c> (Spec 008).</summary>
public static class ExecutionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AlpacaPaperOptions"/>, <see cref="IAlpacaPaperBrokerClient"/> (with redirects disabled),
    /// a fail-closed default <see cref="ITradingStateProvider"/>, and <see cref="IPaperExecutionEngine"/>.
    /// </summary>
    public static IServiceCollection AddSentiXPaperExecution(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(_ => AlpacaPaperOptions.FromConfiguration(configuration));
        services.TryAddSingleton(TimeProvider.System);

        // Fail closed by default unless the host registers a persisted system_state provider.
        services.TryAddSingleton<ITradingStateProvider>(_ =>
            new StaticTradingStateProvider(new TradingStateReadResult(
                false,
                TradingState.HaltedSystemError,
                "No persisted ITradingStateProvider registered.")));

        services.AddHttpClient<IAlpacaPaperBrokerClient, AlpacaPaperBrokerClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });

        services.TryAddSingleton<IPaperExecutionEngine>(sp => new PaperExecutionEngine(
            sp.GetRequiredService<AlpacaPaperOptions>(),
            sp.GetRequiredService<ITradingStateProvider>(),
            sp.GetRequiredService<IAlpacaPaperBrokerClient>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<PaperExecutionEngine>>()));

        return services;
    }
}
