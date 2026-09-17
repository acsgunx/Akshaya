using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Moomoo;

/// <summary>
/// Positions, holdings and balances from OpenD.
///
/// OpenD returns ONE position list and the contract asks for two. They are a PARTITION, never a copy: a
/// long cash equity or ETF position is a holding, and everything else — options, and shorts of anything —
/// is a position. Reporting a stock in both would double its unrealised P&amp;L, because the blended
/// portfolio adds the two lists together.
///
/// The funds route answers in ONE currency. For a universal account OpenD requires the caller to name a
/// currency and converts the whole account into it; it does not report per-currency cash for securities
/// accounts. So exactly one balance is returned — in USD when the account trades the US, otherwise HKD —
/// rather than the same money reported twice in two currencies.
/// </summary>
public sealed class MoomooPortfolio : IConnectorPortfolio
{
    private readonly MoomooChannel _channel;
    private readonly MoomooInstrumentCache _cache;
    private readonly MoomooInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly ILogger _logger;

    internal MoomooPortfolio(
        MoomooChannel channel,
        MoomooInstrumentCache cache,
        MoomooInstrumentResolver resolver,
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

        foreach (var (market, record, row) in rows.Value)
        {
            if (IsHolding(record, row))
            {
                continue;
            }

            var isShort = row.PositionSide == MoomooMaps.PositionSideShort;
            var quantity = Math.Abs(MoomooNumber.DecimalOrZero(row.Qty, 4));

            positions.Add(new BrokerPosition
            {
                Instrument = record.Definition.Key,
                NetQuantity = new Quantity(isShort ? -quantity : quantity),
                PositionEffect = MoomooMaps.ToCanonicalPositionEffect(isShort),
                AveragePrice = new Money(CostPrice(row), market.Currency),
                LastPrice = MoneyOrNull(row.Price, market.Currency),
                UnrealisedPnl = MoneyOrNull(row.PlVal, market.Currency),
                RealisedPnl = MoneyOrNull(row.RealizedPl, market.Currency),
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

        foreach (var (market, record, row) in rows.Value)
        {
            if (!IsHolding(record, row))
            {
                continue;
            }

            holdings.Add(new BrokerHolding
            {
                Instrument = record.Definition.Key,
                Quantity = new Quantity(MoomooNumber.DecimalOrZero(row.Qty, 4)),
                AveragePrice = new Money(CostPrice(row), market.Currency),
                LastPrice = MoneyOrNull(row.Price, market.Currency),
                UnrealisedPnl = MoneyOrNull(row.PlVal, market.Currency),

                // OpenD reports what can be sold now (canSellQty), which is lowered by open sell orders as
                // well as by unsettled sales. Reading it as "unsettled" would misstate both, so it is not.
                PledgedQuantity = Quantity.Zero,

                // OpenD provides no ISIN.
                Isin = null,
            });
        }

        return Result<IReadOnlyList<BrokerHolding>>.Success(holdings);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerBalance>>> GetBalancesAsync(CancellationToken ct = default)
    {
        var account = MoomooAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(account.Error);
        }

        var market = account.Value.Trades(MoomooMarket.Us) ? MoomooMarket.Us : account.Value.Markets[0];

        var response = await _channel.RequestAsync<OpenDGetFundsC2S, OpenDGetFundsS2C>(
            MoomooProtoId.TrdGetFunds,
            new OpenDGetFundsC2S
            {
                Header = account.Value.HeaderFor(market),

                // Required for a universal account and ignored for a single-market one, whose funds are
                // already in its market's currency. Trd_Common.Currency: 2 USD, 1 HKD.
                Currency = market == MoomooMarket.Us ? 2 : 1,
            },
            ct).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(response.Error);
        }

        if (response.Value.Funds is not { } funds)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(MoomooErrors.MissingField(MoomooProtoId.TrdGetFunds, "funds"));
        }

        var currency = market.Currency;
        var cash = MoomooNumber.Decimal(funds.Cash);

        return Result<IReadOnlyList<BrokerBalance>>.Success(
        [
            new BrokerBalance
            {
                Currency = currency,

                // Cash buying power: what an order can be placed with without borrowing. Maximum buying
                // power, which includes margin, is AvailableMargin below.
                AvailableToTrade = new Money(MoomooNumber.Decimal(funds.NetCashPower) ?? cash ?? 0m, currency),
                CashBalance = cash is { } c ? new Money(c, currency) : null,
                AvailableMargin = MoneyOrNull(funds.Power, currency),
                UsedMargin = MoneyOrNull(funds.InitialMargin, currency),
                UnrealisedPnl = MoneyOrNull(funds.UnrealizedPl, currency),
                RealisedPnl = MoneyOrNull(funds.RealizedPl, currency),
            },
        ]);
    }

    /// <inheritdoc />
    public Task<Result> ConvertPositionAsync(ConvertPositionRequest request, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure(ConnectorErrors.NotSupported(
            "position conversion. moomoo has no product types to convert between: margin use is a property of the account")));

    /// <summary>Every position row across the account's markets, with its security resolved.</summary>
    private async Task<Result<List<(MoomooMarket Market, MoomooInstrumentRecord Record, OpenDPosition Row)>>> ReadAsync(CancellationToken ct)
    {
        var account = MoomooAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<List<(MoomooMarket, MoomooInstrumentRecord, OpenDPosition)>>.Failure(account.Error);
        }

        var raw = new List<(MoomooMarket Market, OpenDPosition Row)>();

        foreach (var market in account.Value.Markets)
        {
            var response = await _channel.RequestAsync<OpenDGetPositionListC2S, OpenDGetPositionListS2C>(
                MoomooProtoId.TrdGetPositionList,
                new OpenDGetPositionListC2S { Header = account.Value.HeaderFor(market) },
                ct).ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<List<(MoomooMarket, MoomooInstrumentRecord, OpenDPosition)>>.Failure(response.Error);
            }

            foreach (var row in response.Value.PositionList ?? [])
            {
                if (!string.IsNullOrWhiteSpace(row.Code) && MoomooNumber.DecimalOrZero(row.Qty, 4) != 0m)
                {
                    raw.Add((MoomooOrderMapper.MarketOf(row.SecMarket, row.TrdMarket, market), row));
                }
            }
        }

        var ensured = await _resolver.EnsureAsync(
            _channel,
            raw.Select(r => new OpenDSecurity { Market = r.Market.QotMarket, Code = r.Row.Code!.Trim().ToUpperInvariant() }),
            ct).ConfigureAwait(false);

        if (ensured.IsFailure)
        {
            return Result<List<(MoomooMarket, MoomooInstrumentRecord, OpenDPosition)>>.Failure(ensured.Error);
        }

        var rows = new List<(MoomooMarket Market, MoomooInstrumentRecord Record, OpenDPosition Row)>(raw.Count);

        foreach (var (market, row) in raw)
        {
            var native = MoomooNative.Qualify(market, row.Code!.Trim().ToUpperInvariant());

            if (_cache.TryGetByNative(native, out var record))
            {
                rows.Add((market, record, row));
                continue;
            }

            if (_cache.IsOutOfScope(native))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("{ConnectorId}: skipping a position in {Native}, outside the declared venues.", MoomooAuth.ConnectorId, native);
                }

                continue;
            }

            // A position the connector cannot describe is an exposure the risk engine cannot see. Failing
            // the read says so; silently dropping it would under-report the account.
            return Result<List<(MoomooMarket, MoomooInstrumentRecord, OpenDPosition)>>.Failure(MoomooNative.NotFound(native));
        }

        return Result<List<(MoomooMarket, MoomooInstrumentRecord, OpenDPosition)>>.Success(rows);
    }

    private static bool IsHolding(MoomooInstrumentRecord record, OpenDPosition row) =>
        row.PositionSide != MoomooMaps.PositionSideShort
        && record.Definition.Key.AssetClass is AssetClass.Equity or AssetClass.Etf;

    /// <summary>Average cost is the documented replacement for the deprecated costPrice; diluted cost is the fallback.</summary>
    private static decimal CostPrice(OpenDPosition row) =>
        MoomooNumber.Decimal(row.AverageCostPrice)
        ?? MoomooNumber.Decimal(row.DilutedCostPrice)
        ?? MoomooNumber.Decimal(row.CostPrice)
        ?? 0m;

    private static Money? MoneyOrNull(double? value, Currency currency) =>
        MoomooNumber.Decimal(value) is { } amount ? new Money(amount, currency) : null;
}
