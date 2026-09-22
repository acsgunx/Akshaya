using Akshaya.Modules.Trading.Application;

namespace Akshaya.Api.Infrastructure;

/// <summary>
/// Runs <see cref="ReconciliationService"/>'s polling loop for the life of the process.
///
/// That service is what keeps the platform's order book honest against every broker's — and,
/// just as importantly, what ADOPTS orders the platform never placed: an order entered in the
/// broker's own app or website appears here only because a reconciliation pass found it in the
/// broker's book (<see cref="ReconciliationOptions.AdoptBrokerOnlyOrders"/>). Its doc comment
/// has always said "the API wraps <c>ExecuteAsync</c> in a hosted service"; until this class
/// existed nothing did, so the blotter only ever showed orders placed through Akshaya.
///
/// One scope for the whole loop, not one per pass: the service is scoped only because its
/// stores may be, and the trading stores this host registers are in-process singletons.
/// <see cref="ReconciliationService.ExecuteAsync"/> never throws out of itself, so there is no
/// crash path here to take the host down.
/// </summary>
internal sealed class ReconciliationHostedService(IServiceScopeFactory scopes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var reconciliation = scope.ServiceProvider.GetRequiredService<ReconciliationService>();

        await reconciliation.ExecuteAsync(stoppingToken);
    }
}
