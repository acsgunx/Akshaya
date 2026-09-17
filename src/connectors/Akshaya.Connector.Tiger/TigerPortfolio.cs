using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Tiger;

/// <summary>
/// Positions, holdings and balances from Tiger's asset methods.
///
/// Tiger returns ONE position list and the contract asks for two. They are a PARTITION, never a copy: a long stock
/// is a holding, and everything else — options, and shorts of anything — is a position. Reporting a stock in both
/// would double its unrealised P&amp;L, because the blended portfolio adds the two lists together.
///
/// Balances come from the prime-assets view: one row per currency the securities segment holds, with the
/// segment-wide figures — buying power, initial margin, P&amp;L — on the segment's own currency row.
/// </summary>
public sealed class TigerPortfolio : IConnectorPortfolio
{
    /// <summary>The securities segment. A commodities segment belongs to futures, which this connector does not trade.</summary>
    private const string SecuritiesSegment = "S";

    private readonly TigerChannel _channel;
    private readonly TigerOptions _options;
    private readonly TigerInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly ILogger _logger;

    internal TigerPortfolio(
        TigerChannel channel,
        TigerOptions options,
        TigerInstrumentResolver resolver,
        Func<Result<BrokerSession>> requireSession,
        ILogger logger)
    {
        _channel = channel;
        _options = options;
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

        return Result<IReadOnlyList<BrokerPosition>>.Success(
        [
            .. rows.Value.Where(r => !IsHolding(r)).Select(r => new BrokerPosition
            {
                Instrument = r.Record.Definition.Key,
                NetQuantity = new Quantity(r.Quantity),
                PositionEffect = r.Quantity < 0m ? PositionEffect.Delivery | PositionEffect.ShortSell : PositionEffect.Delivery,
                AveragePrice = new Money(r.AverageCost, r.Currency),
                LastPrice = r.LastPrice,
                UnrealisedPnl = r.UnrealisedPnl,
                RealisedPnl = r.RealisedPnl,
            }),
        ]);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerHolding>>> GetHoldingsAsync(CancellationToken ct = default)
    {
        var rows = await ReadAsync(ct).ConfigureAwait(false);
        if (rows.IsFailure)
        {
            return Result<IReadOnlyList<BrokerHolding>>.Failure(rows.Error);
        }

        return Result<IReadOnlyList<BrokerHolding>>.Success(
        [
            .. rows.Value.Where(IsHolding).Select(r => new BrokerHolding
            {
                Instrument = r.Record.Definition.Key,
                Quantity = new Quantity(r.Quantity),
                AveragePrice = new Money(r.AverageCost, r.Currency),
                LastPrice = r.LastPrice,
                UnrealisedPnl = r.UnrealisedPnl,

                // Tiger's position rows say nothing about unsettled sales.
                PledgedQuantity = Quantity.Zero,
                Isin = null,
            }),
        ]);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerBalance>>> GetBalancesAsync(CancellationToken ct = default)
    {
        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(account.Error);
        }

        var api = _channel.Trading(account.Value.Credentials);

        var biz = TigerBiz.New()
            .Add("account", account.Value.AccountId)
            .Add("lang", _options.Language);

        var assets = await api
            .CallAsync<TigerPrimeAssets>(TigerMaps.MethodPrimeAssets, biz, account.Value.Credentials, isTradeWrite: false, ct)
            .ConfigureAwait(false);

        if (assets.IsFailure)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(assets.Error);
        }

        var balances = new List<BrokerBalance>();
        var seen = new HashSet<Currency>();

        foreach (var segment in assets.Value.Segments ?? [])
        {
            if (!string.Equals(segment.Category?.Trim(), SecuritiesSegment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var segmentCurrency = TigerMaps.ToCanonicalCurrency(segment.Currency);

            foreach (var asset in segment.CurrencyAssets ?? [])
            {
                var currency = TigerMaps.ToCanonicalCurrency(asset.Currency);
                if (currency.IsFailure)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "{ConnectorId}: skipping the {Currency} balance, outside the declared currencies.",
                            TigerAuth.ConnectorId,
                            asset.Currency);
                    }

                    continue;
                }

                if (!seen.Add(currency.Value))
                {
                    continue;
                }

                var isSegmentCurrency = segmentCurrency.IsSuccess && segmentCurrency.Value == currency.Value;
                var available = TigerNumber.Decimal(asset.CashAvailableForTrade) ?? TigerNumber.DecimalOrZero(asset.CashBalance);

                balances.Add(new BrokerBalance
                {
                    Currency = currency.Value,
                    AvailableToTrade = new Money(available, currency.Value),
                    CashBalance = MoneyOrNull(asset.CashBalance, currency.Value),

                    // Buying power and margin are figures for the whole segment, stated in its currency.
                    AvailableMargin = isSegmentCurrency ? MoneyOrNull(segment.BuyingPower, currency.Value) : null,
                    UsedMargin = isSegmentCurrency ? MoneyOrNull(segment.InitialMargin, currency.Value) : null,
                    UnrealisedPnl = MoneyOrNull(asset.UnrealisedPnl, currency.Value)
                                    ?? (isSegmentCurrency ? MoneyOrNull(segment.UnrealisedPnl, currency.Value) : null),
                    RealisedPnl = MoneyOrNull(asset.RealisedPnl, currency.Value)
                                  ?? (isSegmentCurrency ? MoneyOrNull(segment.RealisedPnl, currency.Value) : null),
                });
            }
        }

        return Result<IReadOnlyList<BrokerBalance>>.Success(balances);
    }

    /// <inheritdoc />
    public Task<Result> ConvertPositionAsync(ConvertPositionRequest request, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure(ConnectorErrors.NotSupported(
            "position conversion. Tiger has no product types to convert between: margin use is a property of the account")));

    private async Task<Result<List<Row>>> ReadAsync(CancellationToken ct)
    {
        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<List<Row>>.Failure(account.Error);
        }

        var api = _channel.Trading(account.Value.Credentials);

        var biz = TigerBiz.New()
            .Add("account", account.Value.AccountId)
            .Add("lang", _options.Language);

        var response = await api
            .CallAsync<TigerPage<TigerPosition>>(TigerMaps.MethodPositions, biz, account.Value.Credentials, isTradeWrite: false, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<List<Row>>.Failure(response.Error);
        }

        var rows = new List<Row>();

        foreach (var position in response.Value.Items ?? [])
        {
            var quantity = TigerNumber.DecimalOrZero(position.Quantity);
            if (quantity == 0m || string.IsNullOrWhiteSpace(position.Symbol))
            {
                continue;
            }

            var record = await _resolver.ForContractAsync(api, position.Contract(), account.Value.Credentials, ct).ConfigureAwait(false);
            if (record.IsFailure)
            {
                if (record.Error.Code == ConnectorErrorCodes.NotSupported)
                {
                    // A future or a warrant: outside the declared scope, and reported as such rather than silently priced.
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "{ConnectorId}: skipping a position in {Symbol}, outside the declared venues and asset classes.",
                            TigerAuth.ConnectorId,
                            position.Symbol);
                    }

                    continue;
                }

                // A position the connector cannot describe is an exposure the risk engine cannot see.
                return Result<List<Row>>.Failure(record.Error);
            }

            var currency = TigerMaps.ToCanonicalCurrency(position.Currency) is { IsSuccess: true } listed
                ? listed.Value
                : record.Value.Definition.Currency;

            rows.Add(new Row(
                record.Value,
                quantity,
                TigerNumber.DecimalOrZero(position.AverageCost),
                currency,
                MoneyOrNull(position.LatestPrice, currency),
                MoneyOrNull(position.UnrealisedPnl, currency),
                MoneyOrNull(position.RealisedPnl, currency)));
        }

        return rows;
    }

    private static bool IsHolding(Row row) =>
        row.Quantity > 0m && row.Record.Definition.Key.AssetClass is AssetClass.Equity or AssetClass.Etf;

    private static Money? MoneyOrNull(string? value, Currency currency) =>
        TigerNumber.Decimal(value) is { } amount ? new Money(amount, currency) : null;

    private sealed record Row(
        TigerInstrumentRecord Record,
        decimal Quantity,
        decimal AverageCost,
        Currency Currency,
        Money? LastPrice,
        Money? UnrealisedPnl,
        Money? RealisedPnl);
}
