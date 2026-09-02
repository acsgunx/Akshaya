using Akshaya.Modules.Portfolio.Ports;
using Akshaya.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Akshaya.Modules.Portfolio;

/// <summary>Composition root for the blended portfolio.</summary>
public static class PortfolioServiceCollectionExtensions
{
    /// <summary>
    /// Registers the blending service and the conservative default identity resolver.
    ///
    /// The caller supplies <see cref="IPortfolioLinkProvider"/> — which links to read — and
    /// <see cref="IFxRateProvider"/> — how to convert for display. Both are deliberately left
    /// out: link lifecycle belongs to the BrokerLink module and rates belong to a market data
    /// feed, and this module must not grow a dependency on either.
    /// </summary>
    public static IServiceCollection AddBlendedPortfolio(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Falls back to grouping by canonical instrument key until an instrument master exists.
        // Conservative: it may show two rows where one was possible, and it will never merge two
        // instruments that are not provably the same.
        services.TryAddSingleton<IInstrumentIdentityResolver>(NullInstrumentIdentityResolver.Instance);

        services.AddScoped<BlendedPortfolioService>();

        return services;
    }

    /// <summary>
    /// DEVELOPMENT ONLY. Registers the hand-typed FX table.
    ///
    /// See <see cref="StaticFxRateProvider"/> for exactly how wrong these numbers are. Phase 5
    /// replaces it with a real feed; this method is the line that gets deleted.
    /// </summary>
    public static IServiceCollection AddDevelopmentFxRates(
        this IServiceCollection services,
        Action<StaticFxRateProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<IFxRateProvider>(sp =>
        {
            var provider = new StaticFxRateProvider(sp.GetRequiredService<IClock>());
            configure(provider);
            return provider;
        });

        return services;
    }
}
