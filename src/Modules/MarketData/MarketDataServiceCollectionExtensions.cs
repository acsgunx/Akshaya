using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Akshaya.Modules.MarketData;

/// <summary>Composition root for the instrument master.</summary>
public static class MarketDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the instrument master as a SINGLETON — the whole point is that it outlives
    /// the request-scoped connectors that fill it.
    /// </summary>
    public static IServiceCollection AddInstrumentMaster(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<InstrumentMasterOptions>();
        services.TryAddSingleton<InstrumentMaster>();

        return services;
    }
}
