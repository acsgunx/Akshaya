using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Longbridge;

/// <summary>
/// Positions, holdings and balances from Longbridge's asset routes.
///
/// Longbridge returns ONE position list, grouped by account channel, and the contract asks for two. They are a
/// PARTITION, never a copy: a long cash equity is a holding, and everything else — options, and shorts of
/// anything — is a position. Reporting a stock in both would double its unrealised P&amp;L, because the blended
/// portfolio adds the two lists together. A security held in more than one channel is combined into one row.
///
/// Longbridge reports no last price or P&amp;L on a position; those are left for the portfolio module to price.
/// </summary>
public sealed class LongbridgePortfolio : IConnectorPortfolio
{
    private const string StockPositionsPath = "/v1/asset/stock";
    private const string AccountBalancePath = "/v1/asset/account";

    private readonly LongbridgeChannel _channel;
    private readonly LongbridgeInstrumentCache _cache;
    private readonly LongbridgeInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly ILogger _logger;

    internal LongbridgePortfolio(
        LongbridgeChannel channel,
        LongbridgeInstrumentCache cache,
        LongbridgeInstrumentResolver resolver,
        Func<Result<BrokerSession>> requireSession,
        ILogger logger)
    {
        _channel = channel;
        _cache = cache;
        _resolver = resolver;
        _requireSession = requireSession;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(CancellationToken ct = default)
    {
        var rows = await ReadAsync(ct).ConfigureAwait(false);
        if (rows.IsFailure)
        {
            return Result<IReadOnlyList<BrokerPosition>>.Failure(rows.Error);
        }

        var positions = new List<BrokerPosition>();

        foreach (var row in rows.Value)
        {
            if (IsHolding(row))
            {
                continue;
            }

            positions.Add(new BrokerPosition
            {
                Instrument = row.Record.Definition.Key,
                NetQuantity = new Quantity(row.Quantity),
                PositionEffect = row.Quantity < 0m ? PositionEffect.Delivery | PositionEffect.ShortSell : PositionEffect.Delivery,
                AveragePrice = new Money(row.AverageCost, row.Currency),
            });
        }

        return Result<IReadOnlyList<BrokerPosition>>.Success(positions);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerHolding>>> GetHoldingsAsync(CancellationToken ct = default)
    {
        var rows = await ReadAsync(ct).ConfigureAwait(false);
        if (rows.IsFailure)
        {
            return Result<IReadOnlyList<BrokerHolding>>.Failure(rows.Error);
        }

        var holdings = new List<BrokerHolding>();

        foreach (var row in rows.Value)
        {
            if (!IsHolding(row))
            {
                continue;
            }

            holdings.Add(new BrokerHolding
            {
                Instrument = row.Record.Definition.Key,
                Quantity = new Quantity(row.Quantity),
                AveragePrice = new Money(row.AverageCost, row.Currency),

                // available_quantity is lowered by open sell orders as well as by unsettled sales; reading the
                // difference as "unsettled" would misstate both, so it is not.
                PledgedQuantity = Quantity.Zero,

                // Longbridge provides no ISIN.
                Isin = null,
            });
        }

        return Result<IReadOnlyList<BrokerHolding>>.Success(holdings);
    }

    /// <inheritdoc />
    /// <remarks>
    /// One balance per currency the account holds cash in, from the per-currency cash rows. Buying power and
    /// initial margin are account-wide figures stated in the account's base currency, so they appear on that
    /// currency's row only — putting them on every row would count the same margin once per currency.
    /// </remarks>
    public async Task<Result<IReadOnlyList<BrokerBalance>>> GetBalancesAsync(CancellationToken ct = default)
    {
        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(session.Error);
        }

        var response = await _channel.Api.GetAsync<LbAccountBalanceResponse>(AccountBalancePath, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(response.Error);
        }

        var balances = new List<BrokerBalance>();
        var seen = new HashSet<Currency>();

        foreach (var account in response.Value.List ?? [])
        {
            var baseCurrency = LongbridgeMaps.ToCanonicalCurrency(account.Currency);
            var cashInfos = account.CashInfos ?? [];

            foreach (var cash in cashInfos)
            {
                var currency = LongbridgeMaps.ToCanonicalCurrency(cash.Currency);
                if (currency.IsFailure)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("{ConnectorId}: skipping cash in {Currency}, outside the declared currencies.", LongbridgeAuth.ConnectorId, cash.Currency);
                    }

                    continue;
                }

                if (!seen.Add(currency.Value))
                {
                    continue;
                }

                var isBase = baseCurrency.IsSuccess && baseCurrency.Value == currency.Value;

                balances.Add(new BrokerBalance
                {
                    Currency = currency.Value,
                    AvailableToTrade = new Money(LongbridgeNumber.DecimalOrZero(cash.AvailableCash), currency.Value),
                    AvailableMargin = isBase ? MoneyOrNull(account.BuyPower, currency.Value) : null,
                    UsedMargin = isBase ? MoneyOrNull(account.InitMargin, currency.Value) : null,
                });
            }

            if (cashInfos.Count == 0 && baseCurrency.IsSuccess && seen.Add(baseCurrency.Value))
            {
                balances.Add(new BrokerBalance
                {
                    Currency = baseCurrency.Value,
                    AvailableToTrade = new Money(LongbridgeNumber.DecimalOrZero(account.TotalCash), baseCurrency.Value),
                    CashBalance = MoneyOrNull(account.TotalCash, baseCurrency.Value),
                    AvailableMargin = MoneyOrNull(account.BuyPower, baseCurrency.Value),
                    UsedMargin = MoneyOrNull(account.InitMargin, baseCurrency.Value),
                });
            }
        }

        return Result<IReadOnlyList<BrokerBalance>>.Success(balances);
    }

    /// <inheritdoc />
    public Task<Result> ConvertPositionAsync(ConvertPositionRequest request, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure(ConnectorErrors.NotSupported(
            "position conversion. Longbridge has no product types to convert between: margin use is a property of the account")));

    private async Task<Result<List<Row>>> ReadAsync(CancellationToken ct)
    {
        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<List<Row>>.Failure(session.Error);
        }

        var response = await _channel.Api.GetAsync<LbStockPositionsResponse>(StockPositionsPath, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result<List<Row>>.Failure(response.Error);
        }

        var raw = (response.Value.List ?? [])
            .SelectMany(channel => channel.StockInfo ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Symbol) && LongbridgeNumber.DecimalOrZero(p.Quantity) != 0m)
            .ToList();

        var ensured = await _resolver.EnsureAsync(_channel, raw.Select(p => p.Symbol!), ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<List<Row>>.Failure(ensured.Error);
        }

        var combined = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);

        foreach (var position in raw)
        {
            var native = position.Symbol!.Trim().ToUpperInvariant();

            if (!_cache.TryGetByNative(native, out var record))
            {
                if (_cache.IsOutOfScope(native))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("{ConnectorId}: skipping a position in {Native}, outside the declared venues.", LongbridgeAuth.ConnectorId, native);
                    }

                    continue;
                }

                // A position the connector cannot describe is an exposure the risk engine cannot see. Failing
                // the read says so; silently dropping it would under-report the account.
                return Result<List<Row>>.Failure(LongbridgeNative.NotFound(native));
            }

            var quantity = LongbridgeNumber.DecimalOrZero(position.Quantity);
            var cost = LongbridgeNumber.DecimalOrZero(position.CostPrice);
            var currency = LongbridgeMaps.ToCanonicalCurrency(position.Currency) is { IsSuccess: true } listed
                ? listed.Value
                : record.Definition.Currency;

            if (combined.TryGetValue(native, out var existing))
            {
                var total = existing.Quantity + quantity;
                var weighted = total == 0m ? 0m : ((existing.AverageCost * existing.Quantity) + (cost * quantity)) / total;
                combined[native] = existing with { Quantity = total, AverageCost = weighted };
            }
            else
            {
                combined[native] = new Row(record, quantity, cost, currency);
            }
        }

        return Result<List<Row>>.Success([.. combined.Values.Where(r => r.Quantity != 0m)]);
    }

    private static bool IsHolding(Row row) =>
        row.Quantity > 0m && row.Record.Definition.Key.AssetClass is AssetClass.Equity or AssetClass.Etf;

    private static Money? MoneyOrNull(string? value, Currency currency) =>
        LongbridgeNumber.Decimal(value) is { } amount ? new Money(amount, currency) : null;

    private sealed record Row(LongbridgeInstrumentRecord Record, decimal Quantity, decimal AverageCost, Currency Currency);
}
