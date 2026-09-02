using System.Diagnostics;
using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Portfolio.Models;
using Akshaya.Modules.Portfolio.Ports;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Portfolio;

/// <summary>Which parts of the portfolio to fetch. Fetching only what is asked for saves a broker round trip per part.</summary>
[Flags]
public enum PortfolioParts
{
    None = 0,
    Positions = 1 << 0,
    Holdings = 1 << 1,
    Balances = 1 << 2,
    All = Positions | Holdings | Balances,
}

/// <summary>
/// Assembles one portfolio from every broker the user has linked.
///
/// ═════════════════════════ THE FOUR RULES, AND WHY ═════════════════════════
///
/// 1. FETCH EVERY BROKER IN PARALLEL. Five brokers at 400ms each is 400ms, not two seconds.
///    A dashboard that takes two seconds is a dashboard people stop opening.
///
/// 2. AGGREGATE NATIVELY PER CURRENCY FIRST, CONVERT ONLY FOR DISPLAY. Every blended row holds
///    exactly one currency, and the converted totals are computed last, from the native
///    figures, with the rates recorded alongside. Converting early and summing afterwards
///    compounds rounding into the headline number and makes it impossible to say which rate
///    produced it.
///
/// 3. GROUP BY ISIN OR FIGI WHEN WE HAVE ONE, BY CANONICAL KEY OTHERWISE. That is what turns
///    a holding at one broker and the same holding at another into one line. The fallback is
///    deliberately conservative: it will show two rows where one was possible, but it will
///    never merge two instruments that are not provably the same — an over-merged portfolio
///    reports exposure the user does not have.
///
/// 4. ONE DEAD BROKER MUST NOT BLANK THE DASHBOARD. Every link's outcome is captured in a
///    <see cref="PortfolioSourceStatus"/> and the snapshot is returned with whatever succeeded.
///    A user whose main account is fine does not lose sight of it because a secondary broker is
///    having an outage — and they are told, in the same view, which account is missing.
/// ═══════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class BlendedPortfolioService(
    IPortfolioLinkProvider links,
    IConnectorFactory connectors,
    IFxRateProvider fx,
    IInstrumentIdentityResolver identities,
    IClock clock,
    ILogger<BlendedPortfolioService> logger)
{
    public async Task<PortfolioSnapshot> GetSnapshotAsync(
        string tenantId,
        string userId,
        Currency displayCurrency,
        PortfolioParts parts = PortfolioParts.All,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var accounts = await links.GetLinksAsync(tenantId, userId, ct);

        // Rule 1: every broker at once.
        var fetches = await Task.WhenAll(accounts.Select(link => FetchAsync(link, parts, ct)));

        var positions = await BlendPositionsAsync(fetches, ct);
        var holdings = await BlendHoldingsAsync(fetches, ct);
        var balances = BlendBalances(fetches);
        var pnl = await SummarisePnlAsync(fetches, displayCurrency, ct);

        return new PortfolioSnapshot
        {
            TenantId = tenantId,
            UserId = userId,
            AsOf = clock.UtcNow,
            DisplayCurrency = displayCurrency,
            Positions = positions,
            Holdings = holdings,
            Balances = balances,
            Pnl = pnl,
            Sources = [.. fetches.Select(f => f.Status)],
        };
    }

    // ───────────────────────────── fetching ─────────────────────────────

    private async Task<LinkFetch> FetchAsync(PortfolioLink link, PortfolioParts parts, CancellationToken ct)
    {
        var started = clock.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var connectorResult = await connectors.CreateAsync(link.ConnectorId, link.Session, ct);
        if (connectorResult.IsFailure)
        {
            return Failed(link, connectorResult.Error, started, stopwatch.Elapsed);
        }

        try
        {
            await using var connector = connectorResult.Value;

            // Within a link, the three calls are also concurrent. They hit different endpoints
            // and the connector's own rate limiter is the thing that decides whether they may
            // actually run at once — which is exactly where that decision belongs.
            var positionsTask = parts.HasFlag(PortfolioParts.Positions)
                ? connector.Portfolio.GetPositionsAsync(ct)
                : Task.FromResult(Result<IReadOnlyList<BrokerPosition>>.Success([]));

            var holdingsTask = parts.HasFlag(PortfolioParts.Holdings)
                ? connector.Portfolio.GetHoldingsAsync(ct)
                : Task.FromResult(Result<IReadOnlyList<BrokerHolding>>.Success([]));

            var balancesTask = parts.HasFlag(PortfolioParts.Balances)
                ? connector.Portfolio.GetBalancesAsync(ct)
                : Task.FromResult(Result<IReadOnlyList<BrokerBalance>>.Success([]));

            await Task.WhenAll(positionsTask, holdingsTask, balancesTask);

            var positions = await positionsTask;
            var holdings = await holdingsTask;
            var balances = await balancesTask;

            var firstError = FirstError(positions, holdings, balances);
            if (firstError is { } error)
            {
                logger.LogWarning(
                    "Portfolio fetch for link {LinkId} was partial: {Error}",
                    link.Id,
                    error);
            }

            return new LinkFetch(
                link,
                new PortfolioSourceStatus
                {
                    BrokerLinkId = link.Id,
                    ConnectorId = link.ConnectorId,
                    DisplayName = link.DisplayName,
                    PositionsOk = positions.IsSuccess,
                    HoldingsOk = holdings.IsSuccess,
                    BalancesOk = balances.IsSuccess,
                    Error = firstError,
                    FetchedAt = started,
                    Duration = stopwatch.Elapsed,
                },
                positions.ValueOr([]),
                holdings.ValueOr([]),
                balances.ValueOr([]));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Rule 4. A connector that throws instead of returning a failure must degrade this
            // one account, never the whole dashboard.
            logger.LogError(ex, "Portfolio fetch for link {LinkId} threw.", link.Id);

            return Failed(
                link,
                new Error(ConnectorErrorCodes.Unknown, $"This account could not be read: {ex.Message}"),
                started,
                stopwatch.Elapsed);
        }
    }

    private static LinkFetch Failed(PortfolioLink link, Error error, DateTimeOffset at, TimeSpan duration) =>
        new(
            link,
            new PortfolioSourceStatus
            {
                BrokerLinkId = link.Id,
                ConnectorId = link.ConnectorId,
                DisplayName = link.DisplayName,
                Error = error,
                FetchedAt = at,
                Duration = duration,
            },
            [],
            [],
            []);

    private static Error? FirstError(
        Result<IReadOnlyList<BrokerPosition>> positions,
        Result<IReadOnlyList<BrokerHolding>> holdings,
        Result<IReadOnlyList<BrokerBalance>> balances)
    {
        if (positions.IsFailure) { return positions.Error; }
        if (holdings.IsFailure) { return holdings.Error; }
        if (balances.IsFailure) { return balances.Error; }
        return null;
    }

    // ───────────────────────────── blending ─────────────────────────────

    private async Task<IReadOnlyList<BlendedPosition>> BlendPositionsAsync(
        IReadOnlyList<LinkFetch> fetches,
        CancellationToken ct)
    {
        var groups = new Dictionary<GroupKey, List<(PortfolioLink Link, BrokerPosition Position, InstrumentIdentity Identity)>>();

        foreach (var fetch in fetches)
        {
            foreach (var position in fetch.Positions.Where(p => !p.IsFlat))
            {
                var identity = await identities.ResolveAsync(position.Instrument, ct);

                // Rule 2 and Rule 3 meet here: identity decides WHICH rows merge, currency
                // decides whether they MAY.
                var key = MakeKey(position.Instrument, identity, position.AveragePrice.Currency);

                if (!groups.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    groups[key] = bucket;
                }

                bucket.Add((fetch.Link, position, identity));
            }
        }

        var blended = new List<BlendedPosition>(groups.Count);

        foreach (var (key, members) in groups)
        {
            var currency = key.Currency;
            var legs = members
                .Select(m => new BrokerPositionLeg(
                    m.Link.Id,
                    m.Link.ConnectorId,
                    m.Link.DisplayName,
                    m.Position.NetQuantity,
                    m.Position.AveragePrice,
                    m.Position.LastPrice,
                    MarketValue(m.Position.LastPrice, m.Position.NetQuantity),
                    m.Position.UnrealisedPnl,
                    m.Position.RealisedPnl,
                    m.Position.PositionEffect))
                .ToArray();

            var netQuantity = new Quantity(legs.Sum(l => l.NetQuantity.Value));
            var identity = members[0].Identity;

            blended.Add(new BlendedPosition
            {
                GroupKey = key.Identity,
                GroupedBy = key.Grouping,
                Instrument = members[0].Position.Instrument,
                Isin = identity.Isin,
                Figi = identity.Figi,
                Currency = currency,
                NetQuantity = netQuantity,
                AveragePrice = WeightedAverage(legs.Select(l => (l.NetQuantity.Value, l.AveragePrice)), currency),
                LastPrice = legs.Select(l => l.LastPrice).FirstOrDefault(p => p is not null),
                MarketValue = SumOrNull(legs.Select(l => l.MarketValue), currency),
                UnrealisedPnl = SumOrNull(legs.Select(l => l.UnrealisedPnl), currency),
                RealisedPnl = SumOrNull(legs.Select(l => l.RealisedPnl), currency),
                Legs = legs,
            });
        }

        return [.. blended.OrderBy(b => b.Instrument.Symbol, StringComparer.Ordinal).ThenBy(b => b.Currency.Code, StringComparer.Ordinal)];
    }

    private async Task<IReadOnlyList<BlendedHolding>> BlendHoldingsAsync(
        IReadOnlyList<LinkFetch> fetches,
        CancellationToken ct)
    {
        var groups = new Dictionary<GroupKey, List<(PortfolioLink Link, BrokerHolding Holding, InstrumentIdentity Identity)>>();

        foreach (var fetch in fetches)
        {
            foreach (var holding in fetch.Holdings.Where(h => h.Quantity.Value != 0m))
            {
                // A holding often carries its own ISIN. Prefer it over the resolver: it came
                // from the broker's own books about this exact line, which is better evidence
                // than a lookup keyed on a canonical symbol.
                var resolved = await identities.ResolveAsync(holding.Instrument, ct);
                var identity = holding.Isin is { Length: > 0 } isin
                    ? new InstrumentIdentity(isin, resolved.Figi)
                    : resolved;

                var key = MakeKey(holding.Instrument, identity, holding.AveragePrice.Currency);

                if (!groups.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    groups[key] = bucket;
                }

                bucket.Add((fetch.Link, holding, identity));
            }
        }

        var blended = new List<BlendedHolding>(groups.Count);

        foreach (var (key, members) in groups)
        {
            var currency = key.Currency;
            var legs = members
                .Select(m => new BrokerHoldingLeg(
                    m.Link.Id,
                    m.Link.ConnectorId,
                    m.Link.DisplayName,
                    m.Holding.Quantity,
                    m.Holding.AveragePrice,
                    m.Holding.LastPrice,
                    m.Holding.CurrentValue,
                    m.Holding.UnrealisedPnl,
                    m.Holding.PledgedQuantity))
                .ToArray();

            var identity = members[0].Identity;

            blended.Add(new BlendedHolding
            {
                GroupKey = key.Identity,
                GroupedBy = key.Grouping,
                Instrument = members[0].Holding.Instrument,
                Isin = identity.Isin,
                Figi = identity.Figi,
                Currency = currency,
                Quantity = new Quantity(legs.Sum(l => l.Quantity.Value)),
                AveragePrice = WeightedAverage(legs.Select(l => (l.Quantity.Value, l.AveragePrice)), currency),
                LastPrice = legs.Select(l => l.LastPrice).FirstOrDefault(p => p is not null),
                CurrentValue = SumOrNull(legs.Select(l => l.CurrentValue), currency),
                UnrealisedPnl = SumOrNull(legs.Select(l => l.UnrealisedPnl), currency),
                PledgedQuantity = new Quantity(legs.Sum(l => l.PledgedQuantity.Value)),
                Legs = legs,
            });
        }

        return [.. blended.OrderBy(b => b.Instrument.Symbol, StringComparer.Ordinal).ThenBy(b => b.Currency.Code, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Balances, grouped by currency and never across it. This is the one aggregation where a
    /// cross-currency sum would be most tempting and most wrong: "you have 12,000" is unusable
    /// when the order the user is about to place must be paid for in SGD.
    /// </summary>
    private static IReadOnlyList<CurrencyBalance> BlendBalances(IReadOnlyList<LinkFetch> fetches)
    {
        var groups = new Dictionary<Currency, List<(PortfolioLink Link, BrokerBalance Balance)>>();

        foreach (var fetch in fetches)
        {
            foreach (var balance in fetch.Balances)
            {
                if (!groups.TryGetValue(balance.Currency, out var bucket))
                {
                    bucket = [];
                    groups[balance.Currency] = bucket;
                }

                bucket.Add((fetch.Link, balance));
            }
        }

        var blended = new List<CurrencyBalance>(groups.Count);

        foreach (var (currency, members) in groups)
        {
            var legs = members
                .Select(m => new BrokerBalanceLeg(
                    m.Link.Id,
                    m.Link.ConnectorId,
                    m.Link.DisplayName,
                    m.Balance.AvailableToTrade,
                    m.Balance.CashBalance,
                    m.Balance.UsedMargin,
                    m.Balance.AvailableMargin))
                .ToArray();

            blended.Add(new CurrencyBalance
            {
                Currency = currency,
                AvailableToTrade = Sum(members.Select(m => m.Balance.AvailableToTrade), currency),
                CashBalance = SumOrNull(members.Select(m => m.Balance.CashBalance), currency),
                UsedMargin = SumOrNull(members.Select(m => m.Balance.UsedMargin), currency),
                AvailableMargin = SumOrNull(members.Select(m => m.Balance.AvailableMargin), currency),
                Collateral = SumOrNull(members.Select(m => m.Balance.Collateral), currency),
                RealisedPnl = SumOrNull(members.Select(m => m.Balance.RealisedPnl), currency),
                UnrealisedPnl = SumOrNull(members.Select(m => m.Balance.UnrealisedPnl), currency),
                Legs = legs,
            });
        }

        return [.. blended.OrderBy(b => b.Currency.Code, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Totals P&amp;L natively, then converts.
    ///
    /// Realised P&amp;L is taken from the BALANCE where the broker reports one there, and from
    /// the positions otherwise. Balances include trades that have already been closed out and
    /// no longer appear as positions, so preferring them is the difference between a day's
    /// realised P&amp;L and a day's realised P&amp;L that forgets everything you closed. Mixing
    /// both sources for one link and one currency would double-count, so it is one or the other,
    /// per link, per currency.
    /// </summary>
    private async Task<PnlSummary> SummarisePnlAsync(
        IReadOnlyList<LinkFetch> fetches,
        Currency displayCurrency,
        CancellationToken ct)
    {
        var unrealised = new Dictionary<Currency, decimal>();
        var realised = new Dictionary<Currency, decimal>();

        foreach (var fetch in fetches)
        {
            foreach (var money in fetch.Positions.Select(p => p.UnrealisedPnl).Concat(fetch.Holdings.Select(h => h.UnrealisedPnl)))
            {
                if (money is { } value)
                {
                    unrealised[value.Currency] = unrealised.GetValueOrDefault(value.Currency) + value.Amount;
                }
            }

            var fromBalances = fetch.Balances
                .Where(b => b.RealisedPnl is not null)
                .Select(b => b.RealisedPnl!.Value)
                .ToArray();

            IReadOnlyList<Money> contributions = fromBalances.Length > 0
                ? fromBalances
                : [.. fetch.Positions.Where(p => p.RealisedPnl is not null).Select(p => p.RealisedPnl!.Value)];

            foreach (var value in contributions)
            {
                realised[value.Currency] = realised.GetValueOrDefault(value.Currency) + value.Amount;
            }
        }

        var unrealisedNative = unrealised.Select(kv => new Money(kv.Value, kv.Key)).ToArray();
        var realisedNative = realised.Select(kv => new Money(kv.Value, kv.Key)).ToArray();

        var warnings = new List<string>();
        var rates = new Dictionary<Currency, AppliedFxRate>();

        var unrealisedConverted = await ConvertTotalAsync(unrealisedNative, displayCurrency, rates, warnings, ct);
        var realisedConverted = await ConvertTotalAsync(realisedNative, displayCurrency, rates, warnings, ct);

        return new PnlSummary
        {
            DisplayCurrency = displayCurrency,
            UnrealisedNative = unrealisedNative,
            RealisedNative = realisedNative,
            UnrealisedConverted = unrealisedConverted,
            RealisedConverted = realisedConverted,
            RatesUsed = [.. rates.Values],
            ConversionWarnings = warnings,
        };
    }

    /// <summary>
    /// Converts a set of native amounts into one display total.
    ///
    /// Returns NULL if any leg could not be converted. A total missing one currency is worse
    /// than no total at all: it looks authoritative, it is off by however much that currency
    /// was worth, and nothing on screen says so. The native figures remain, and the warning
    /// explains what is missing.
    /// </summary>
    private async Task<Money?> ConvertTotalAsync(
        IReadOnlyList<Money> native,
        Currency displayCurrency,
        Dictionary<Currency, AppliedFxRate> rates,
        List<string> warnings,
        CancellationToken ct)
    {
        if (native.Count == 0)
        {
            return Money.Zero(displayCurrency);
        }

        var total = Money.Zero(displayCurrency);

        foreach (var amount in native)
        {
            if (amount.Currency == displayCurrency)
            {
                total += amount;
                continue;
            }

            if (!rates.TryGetValue(amount.Currency, out var applied))
            {
                var rate = await fx.GetRateAsync(amount.Currency, displayCurrency, ct);
                if (rate.IsFailure)
                {
                    var warning =
                        $"{amount.Currency} amounts are not included in the {displayCurrency} total: {rate.Error.Message}";

                    if (!warnings.Contains(warning, StringComparer.Ordinal))
                    {
                        warnings.Add(warning);
                    }

                    return null;
                }

                applied = new AppliedFxRate(amount.Currency, displayCurrency, rate.Value.Rate, rate.Value.AsOf);
                rates[amount.Currency] = applied;
            }

            total += amount.ConvertTo(displayCurrency, applied.Rate);
        }

        return total.Round();
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static GroupKey MakeKey(InstrumentKey instrument, InstrumentIdentity identity, Currency currency)
    {
        if (identity.Isin is { Length: > 0 } isin)
        {
            return new GroupKey(isin, PositionGrouping.Isin, currency);
        }

        if (identity.Figi is { Length: > 0 } figi)
        {
            return new GroupKey(figi, PositionGrouping.Figi, currency);
        }

        return new GroupKey(instrument.ToString(), PositionGrouping.InstrumentKey, currency);
    }

    private static Money? MarketValue(Money? lastPrice, Quantity quantity) =>
        lastPrice is { } price ? new Money(price.Amount * quantity.Value, price.Currency) : null;

    /// <summary>
    /// Quantity-weighted average entry price. Weighted by ABSOLUTE quantity so that a long leg
    /// and a short leg of the same instrument do not cancel each other's weight to zero and
    /// produce a division by nothing.
    /// </summary>
    private static Money WeightedAverage(IEnumerable<(decimal Quantity, Money Price)> legs, Currency currency)
    {
        decimal weight = 0m;
        decimal weighted = 0m;
        Money? first = null;

        foreach (var (quantity, price) in legs)
        {
            first ??= price;
            var absolute = Math.Abs(quantity);
            weight += absolute;
            weighted += absolute * price.Amount;
        }

        return weight == 0m
            ? first ?? Money.Zero(currency)
            : new Money(weighted / weight, currency);
    }

    /// <summary>
    /// Sums optional amounts, returning null when every one of them was absent.
    ///
    /// The distinction is load-bearing: a broker that does not report unrealised P&amp;L must
    /// produce a blank cell, not a confident zero. A zero reads as "you are flat on this
    /// position", which is a different and much more comforting claim than "we do not know".
    /// </summary>
    private static Money? SumOrNull(IEnumerable<Money?> amounts, Currency currency)
    {
        var total = Money.Zero(currency);
        var any = false;

        foreach (var amount in amounts)
        {
            if (amount is not { } value)
            {
                continue;
            }

            any = true;
            total += value;
        }

        return any ? total : null;
    }

    private static Money Sum(IEnumerable<Money> amounts, Currency currency)
    {
        var total = Money.Zero(currency);
        foreach (var amount in amounts)
        {
            total += amount;
        }

        return total;
    }

    /// <summary>Identity plus currency. Two positions merge only when BOTH match — see rule 2.</summary>
    private readonly record struct GroupKey(string Identity, PositionGrouping Grouping, Currency Currency);

    private sealed record LinkFetch(
        PortfolioLink Link,
        PortfolioSourceStatus Status,
        IReadOnlyList<BrokerPosition> Positions,
        IReadOnlyList<BrokerHolding> Holdings,
        IReadOnlyList<BrokerBalance> Balances);
}
