using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Application;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Trading.Infrastructure;

/// <summary>
/// Builds a <see cref="RiskSnapshot"/> from the broker's own positions and balances, with a
/// short cache in front.
///
/// THE CACHE IS NOT AN OPTIMISATION, IT IS A CORRECTNESS CONSTRAINT. The risk gate sits on the
/// order path; a fresh positions call per order would add a broker round trip to every order's
/// latency and would burn the same rate-limit bucket the trader's own quotes need. A few
/// seconds of staleness in a position COUNT is acceptable — the count moves slowly — while a
/// second of extra latency on an order is not.
///
/// It reports <see cref="RiskSnapshot.IsPartial"/> honestly. Rules that must fail closed
/// (the daily loss limit) read that flag and refuse rather than judging on half the data.
/// </summary>
public sealed class ConnectorRiskSnapshotProvider(
    BrokerLinkResolver linkResolver,
    RiskSnapshotCache cache,
    IClock clock,
    ILogger<ConnectorRiskSnapshotProvider> logger) : IRiskSnapshotProvider
{
    public async Task<RiskSnapshot> GetAsync(
        string tenantId,
        string userId,
        string brokerLinkId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerLinkId);

        var now = clock.UtcNow;
        if (cache.TryGet(brokerLinkId, now, out var cached))
        {
            return cached;
        }

        var snapshot = await BuildAsync(tenantId, brokerLinkId, ct);
        cache.Set(brokerLinkId, now, snapshot);
        return snapshot;
    }

    private async Task<RiskSnapshot> BuildAsync(
        string tenantId,
        string brokerLinkId,
        CancellationToken ct)
    {
        var connectorResult = await linkResolver.ResolveAsync(tenantId, brokerLinkId, ct);
        if (connectorResult.IsFailure)
        {
            logger.LogWarning(
                "Could not build a risk snapshot for link {LinkId}: {Error}",
                brokerLinkId,
                connectorResult.Error);

            // Partial, not empty-and-confident. An empty snapshot that claimed completeness
            // would silently switch off the daily loss limit.
            return RiskSnapshot.Empty with { IsPartial = true };
        }

        await using var connector = connectorResult.Value;

        var positionsTask = connector.Portfolio.GetPositionsAsync(ct);
        var balancesTask = connector.Portfolio.GetBalancesAsync(ct);
        await Task.WhenAll(positionsTask, balancesTask);

        var positions = await positionsTask;
        var balances = await balancesTask;
        var partial = positions.IsFailure || balances.IsFailure;

        var netPositions = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var realised = new Dictionary<Currency, decimal>();
        var openCount = 0;

        if (positions.IsSuccess)
        {
            foreach (var position in positions.Value.Where(p => !p.IsFlat))
            {
                openCount++;

                var key = position.Instrument.ToString();
                netPositions[key] = netPositions.GetValueOrDefault(key) + position.NetQuantity.Value;

                if (position.RealisedPnl is { } pnl)
                {
                    // Per currency. Summing across currencies here would be exactly the bug the
                    // Money type exists to prevent.
                    realised[pnl.Currency] = realised.GetValueOrDefault(pnl.Currency) + pnl.Amount;
                }
            }
        }

        // Balances carry a broker-computed realised figure that includes closed positions the
        // positions call no longer returns. Preferred when present, because a day's realised
        // P&L that forgets the trades you have already closed out is not a day's realised P&L.
        var available = new List<Money>();
        if (balances.IsSuccess)
        {
            foreach (var balance in balances.Value)
            {
                available.Add(balance.AvailableToTrade);

                if (balance.RealisedPnl is { } pnl)
                {
                    realised[pnl.Currency] = pnl.Amount;
                }
            }
        }

        if (partial)
        {
            logger.LogWarning(
                "Risk snapshot for link {LinkId} is partial (positions ok={PositionsOk}, balances ok={BalancesOk}).",
                brokerLinkId,
                positions.IsSuccess,
                balances.IsSuccess);
        }

        return new RiskSnapshot
        {
            OpenPositionCount = openCount,
            RealisedPnlToday = [.. realised.Select(kv => new Money(kv.Value, kv.Key))],
            AvailableToTrade = available,
            NetPositions = netPositions,
            IsPartial = partial,
        };
    }
}
