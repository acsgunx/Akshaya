using Akshaya.Modules.Trading.Application;
using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Domain.Rules;
using Akshaya.Modules.Trading.Infrastructure;
using Akshaya.Modules.Trading.Infrastructure.InMemory;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Akshaya.Modules.Trading;

/// <summary>Composition root for the trading core.</summary>
public static class TradingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the domain services, the risk rules and the order handlers.
    ///
    /// It does NOT register storage. The caller supplies <see cref="IOrderRepository"/>,
    /// <see cref="IEventBus"/>, <see cref="IFxConverter"/>, <see cref="IAuditSink"/>,
    /// <see cref="IBrokerLinkStore"/>, <see cref="IKillSwitchStore"/> and
    /// <see cref="IRiskPolicyStore"/> — see <see cref="AddDevelopmentTradingStores"/> for the
    /// in-memory set. It also expects <see cref="IClock"/>, <see cref="ITradingCalendar"/> and
    /// <see cref="Connectors.Abstractions.IConnectorFactory"/> to be registered already.
    ///
    /// EVERY RULE IS REGISTERED, ALWAYS. Which ones actually run is a per-tenant policy decision
    /// made at evaluation time, never a wiring decision made at startup: a rule that is not
    /// registered cannot be switched on by an operator during an incident.
    /// </summary>
    public static IServiceCollection AddTradingCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddValidatorsFromAssemblyContaining<PlaceOrderCommandValidator>(includeInternalTypes: false);

        services.AddScoped<BrokerLinkResolver>();

        // The capability rule is resolved twice — once as part of the gate, once directly by
        // PlaceOrderHandler for its pre-flight check — and both must be the same logic. One
        // registration, two consumers.
        // Rules are SCOPED, not singleton. They are stateless themselves, but several depend on
        // collaborators (the FX converter, the kill switch, the policy store) whose real
        // implementations are request-scoped once EF Core lands. A singleton rule holding a
        // scoped dependency is a captive-dependency bug that only appears in production.
        services.AddScoped<CapabilitySupportedRule>();
        services.AddScoped<IRiskRule>(sp => sp.GetRequiredService<CapabilitySupportedRule>());

        services.AddScoped<IRiskRule, KillSwitchRule>();
        services.AddScoped<IRiskRule, InstrumentAllowDenyRule>();
        services.AddScoped<IRiskRule, FractionalQuantityRule>();
        services.AddScoped<IRiskRule, MaxQuantityRule>();
        services.AddScoped<IRiskRule, VenueMarketHoursRule>();
        services.AddScoped<IRiskRule, MaxOpenPositionsRule>();
        services.AddScoped<IRiskRule, MaxOrderValueRule>();
        services.AddScoped<IRiskRule, DailyLossLimitRule>();
        services.AddScoped<IRiskRule, PriceBandSanityRule>();

        services.AddScoped<RiskGate>();

        services.AddScoped<IKillSwitch, KillSwitch>();
        services.TryAddSingleton<RiskSnapshotCache>();
        services.AddScoped<IRiskSnapshotProvider, ConnectorRiskSnapshotProvider>();

        services.AddScoped<PlaceOrderHandler>();
        services.AddScoped<ModifyOrderHandler>();
        services.AddScoped<CancelOrderHandler>();
        services.AddScoped<CancelAllHandler>();

        services.TryAddSingleton(new ReconciliationOptions());
        services.AddScoped<ReconciliationService>();

        return services;
    }

    /// <summary>
    /// DEVELOPMENT ONLY. Registers the in-memory stores so the API runs with no database.
    ///
    /// Each implementation documents its own limits at the top of its file; collectively they
    /// are not durable, not shared between instances and not an audit trail. EF Core
    /// implementations against PostgreSQL are Phase 5 work, and this method is the single line
    /// that gets deleted when they land.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="normalisationCurrency">
    /// Currency new tenants' risk limits are expressed in. Explicit rather than defaulted,
    /// because a platform that quietly assumes one market's currency is a platform that has
    /// assumed one market.
    /// </param>
    /// <param name="configureRates">Seeds the static FX table used by the risk gate.</param>
    public static IServiceCollection AddDevelopmentTradingStores(
        this IServiceCollection services,
        Currency normalisationCurrency,
        Action<StaticFxConverter>? configureRates = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IOrderRepository, InMemoryOrderRepository>();
        services.TryAddSingleton<IEventBus, InMemoryEventBus>();
        services.TryAddSingleton<IAuditSink, InMemoryAuditSink>();
        services.TryAddSingleton<IBrokerLinkStore, InMemoryBrokerLinkStore>();
        services.TryAddSingleton<IKillSwitchStore, InMemoryKillSwitchStore>();
        services.TryAddSingleton<IRiskPolicyStore>(_ => new InMemoryRiskPolicyStore(normalisationCurrency));

        services.TryAddSingleton<IFxConverter>(sp =>
        {
            var converter = new StaticFxConverter(sp.GetRequiredService<IClock>());
            configureRates?.Invoke(converter);
            return converter;
        });

        return services;
    }
}
