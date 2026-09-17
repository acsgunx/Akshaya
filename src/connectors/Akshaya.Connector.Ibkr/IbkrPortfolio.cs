using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Ibkr;

/// <summary>
/// Positions, holdings and balances from the gateway's portfolio routes.
///
/// IBKR returns ONE position list, paged, and the contract asks for two. They are a PARTITION, never a copy: a long
/// stock is a holding, and everything else — options, and shorts of anything — is a position. Reporting a stock in
/// both would double its unrealised P&amp;L, because the blended portfolio adds the two lists together.
///
/// Balances come from the ledger, one row per currency the account holds, with the account-wide figures — buying
/// power, available funds, initial margin — from the summary, on the base currency's row only.
/// </summary>
public sealed class IbkrPortfolio : IConnectorPortfolio
{
    private readonly IbkrChannel _channel;
    private readonly IbkrOptions _options;
    private readonly IbkrInstrumentCache _cache;
    private readonly IbkrInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly ILogger _logger;

    internal IbkrPortfolio(
        IbkrChannel channel,
        IbkrOptions options,
        IbkrInstrumentCache cache,
        IbkrInstrumentResolver resolver,
        Func<Result<BrokerSession>> requireSession,
        ILogger logger)
    {
        _channel = channel;
        _options = options;
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

        return Result<IReadOnlyList<BrokerPosition>>.Success(
        [
            .. rows.Value.Where(r => !IsHolding(r)).Select(r => new BrokerPosition
            {
                Instrument = r.Record.Definition.Key,
                NetQuantity = new Quantity(r.Quantity),
                PositionEffect = r.Quantity < 0m ? PositionEffect.Delivery | PositionEffect.ShortSell : PositionEffect.Delivery,
                AveragePrice = new Money(r.AveragePrice, r.Currency),
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
                AveragePrice = new Money(r.AveragePrice, r.Currency),
                LastPrice = r.LastPrice,
                UnrealisedPnl = r.UnrealisedPnl,

                // IBKR's position rows say nothing about unsettled sales.
                PledgedQuantity = Quantity.Zero,
                Isin = null,
            }),
        ]);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerBalance>>> GetBalancesAsync(CancellationToken ct = default)
    {
        var account = IbkrAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(account.Error);
        }

        var accountId = account.Value.AccountId;

        var baseCurrency = await _channel.BaseCurrencyAsync(accountId, ct).ConfigureAwait(false);
        if (baseCurrency.IsFailure)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(baseCurrency.Error);
        }

        var api = _channel.Api();
        if (api.IsFailure)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(api.Error);
        }

        var escaped = Uri.EscapeDataString(accountId);

        var ledger = await api.Value.GetAsync<Dictionary<string, IbkrLedgerEntry>>($"portfolio/{escaped}/ledger", ct).ConfigureAwait(false);
        if (ledger.IsFailure)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(ledger.Error);
        }

        // The summary adds buying power and available funds. A ledger without it is still worth reporting.
        var summary = await api.Value.GetAsync<Dictionary<string, IbkrSummaryValue>>($"portfolio/{escaped}/summary", ct).ConfigureAwait(false);
        var figures = summary.IsSuccess ? summary.Value : new Dictionary<string, IbkrSummaryValue>(StringComparer.Ordinal);

        var balances = new List<BrokerBalance>();

        foreach (var (code, entry) in ledger.Value)
        {
            if (string.Equals(code, "BASE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var currency = IbkrMaps.ToCanonicalCurrency(entry.Currency ?? code);
            if (currency.IsFailure)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("{ConnectorId}: skipping the {Currency} ledger, outside the declared currencies.", IbkrAuth.ConnectorId, code);
                }

                continue;
            }

            var isBase = string.Equals(code, baseCurrency.Value, StringComparison.OrdinalIgnoreCase);
            var cash = IbkrNumber.Decimal(entry.CashBalance);

            balances.Add(new BrokerBalance
            {
                Currency = currency.Value,
                AvailableToTrade = new Money(isBase && Figure(figures, "availablefunds") is { } funds ? funds : cash ?? 0m, currency.Value),
                CashBalance = cash is { } balance ? new Money(balance, currency.Value) : null,
                AvailableMargin = isBase && Figure(figures, "buyingpower") is { } power ? new Money(power, currency.Value) : null,
                UsedMargin = isBase && Figure(figures, "initmarginreq") is { } initial ? new Money(initial, currency.Value) : null,
                UnrealisedPnl = MoneyOrNull(entry.UnrealisedPnl, currency.Value),
                RealisedPnl = MoneyOrNull(entry.RealisedPnl, currency.Value),
            });
        }

        return Result<IReadOnlyList<BrokerBalance>>.Success(balances);
    }

    /// <inheritdoc />
    public Task<Result> ConvertPositionAsync(ConvertPositionRequest request, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure(ConnectorErrors.NotSupported(
            "position conversion. IBKR has no product types to convert between: margin use is a property of the account")));

    private async Task<Result<List<Row>>> ReadAsync(CancellationToken ct)
    {
        var account = IbkrAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<List<Row>>.Failure(account.Error);
        }

        // IBKR requires the accounts list before any other portfolio route.
        var primed = await _channel.PortfolioAccountsAsync(ct).ConfigureAwait(false);
        if (primed.IsFailure)
        {
            return Result<List<Row>>.Failure(primed.Error);
        }

        var api = _channel.Api();
        if (api.IsFailure)
        {
            return Result<List<Row>>.Failure(api.Error);
        }

        var raw = new List<IbkrPosition>();
        var escaped = Uri.EscapeDataString(account.Value.AccountId);

        for (var page = 0; page < _options.MaxPositionPages; page++)
        {
            var response = await api.Value.GetAsync<List<IbkrPosition>>($"portfolio/{escaped}/positions/{page}", ct).ConfigureAwait(false);
            if (response.IsFailure)
            {
                return Result<List<Row>>.Failure(response.Error);
            }

            raw.AddRange(response.Value);
            if (response.Value.Count < _options.PositionsPageSize)
            {
                break;
            }
        }

        var open = raw
            .Where(p => IbkrNumber.Decimal(p.Quantity) is { } quantity && quantity != 0m && IbkrNumber.Integer(p.Conid) is > 0)
            .ToList();

        var ensured = await _resolver.EnsureConidsAsync(_channel, open.Select(p => IbkrNumber.Integer(p.Conid) ?? 0), ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<List<Row>>.Failure(ensured.Error);
        }

        var rows = new List<Row>(open.Count);

        foreach (var position in open)
        {
            var conid = IbkrNumber.Integer(position.Conid)!.Value;

            if (!_cache.TryGetByConid(conid, out var record))
            {
                if (_cache.IsOutOfScope(conid))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "{ConnectorId}: skipping a {AssetClass} position in conid {Conid}, outside the declared venues and asset classes.",
                            IbkrAuth.ConnectorId,
                            position.AssetClass,
                            conid);
                    }

                    continue;
                }

                // A position the connector cannot describe is an exposure the risk engine cannot see. Failing the read
                // says so; silently dropping it would under-report the account.
                return Result<List<Row>>.Failure(IbkrOrderMapper.NotDescribed(conid));
            }

            var currency = IbkrMaps.ToCanonicalCurrency(position.Currency) is { IsSuccess: true } listed ? listed.Value : record.Definition.Currency;

            rows.Add(new Row(
                record,
                IbkrNumber.Decimal(position.Quantity)!.Value,

                // avgPrice is per share, or per unit of the underlying for an option — the scale a quote uses.
                IbkrNumber.Decimal(position.AveragePrice) ?? 0m,
                currency,
                IbkrNumber.Decimal(position.MarketPrice) is { } price && price > 0m ? new Money(price, currency) : null,
                MoneyOrNull(position.UnrealisedPnl, currency),
                MoneyOrNull(position.RealisedPnl, currency)));
        }

        return rows;
    }

    private static decimal? Figure(Dictionary<string, IbkrSummaryValue> figures, string name) =>
        figures.TryGetValue(name, out var value) && value.IsNull != true ? IbkrNumber.Decimal(value.Amount) : null;

    private static bool IsHolding(Row row) =>
        row.Quantity > 0m && row.Record.Definition.Key.AssetClass is AssetClass.Equity or AssetClass.Etf;

    private static Money? MoneyOrNull(string? value, Currency currency) =>
        IbkrNumber.Decimal(value) is { } amount ? new Money(amount, currency) : null;

    private sealed record Row(
        IbkrInstrumentRecord Record,
        decimal Quantity,
        decimal AveragePrice,
        Currency Currency,
        Money? LastPrice,
        Money? UnrealisedPnl,
        Money? RealisedPnl);
}
