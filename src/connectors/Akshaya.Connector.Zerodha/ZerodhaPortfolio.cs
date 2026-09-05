using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// Positions, holdings, balances and product conversion.
///
/// Two Kite shapes are worth knowing before changing anything here.
///
/// THE POSITIONS ROUTE RETURNS TWO SETS. <c>net</c> is the actual current position; <c>day</c> is
/// that day's buying and selling alone. Reading <c>day</c> as the position would erase every
/// overnight carry-forward from the portfolio and tell the risk engine the account is flat when it
/// is not. This facet reads <c>net</c>.
///
/// THE FUNDS ROUTE IS SEGMENTED, not a single balance. <c>equity</c> and <c>commodity</c> are
/// separate ledgers with separate cash, and this connector declares NSE and BSE only — so it
/// reports the equity ledger and leaves the commodity one alone rather than adding two numbers
/// that cannot be spent in the same place.
/// </summary>
public sealed class ZerodhaPortfolio : IConnectorPortfolio
{
    private readonly ZerodhaApi _api;
    private readonly ZerodhaOptions _options;
    private readonly ISymbolTranslator _symbols;
    private readonly ILogger _logger;

    internal ZerodhaPortfolio(
        ZerodhaApi api,
        ZerodhaOptions options,
        ISymbolTranslator symbols,
        ILogger logger)
    {
        _api = api;
        _options = options;
        _symbols = symbols;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(CancellationToken ct = default)
    {
        var response = await _api
            .GetAsync<KitePositions>(_options.PositionsPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerPosition>>.Failure(response.Error);
        }

        var rows = response.Value.Net ?? [];
        var positions = new List<BrokerPosition>(rows.Count);

        foreach (var row in rows)
        {
            if (IsOutOfScope(row.Exchange))
            {
                continue;
            }

            var mapped = MapPosition(row);
            if (mapped.IsFailure)
            {
                return Result<IReadOnlyList<BrokerPosition>>.Failure(mapped.Error);
            }

            positions.Add(mapped.Value);
        }

        return Result<IReadOnlyList<BrokerPosition>>.Success(positions);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerHolding>>> GetHoldingsAsync(CancellationToken ct = default)
    {
        var response = await _api
            .GetAsync<List<KiteHolding>>(_options.HoldingsPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerHolding>>.Failure(response.Error);
        }

        var holdings = new List<BrokerHolding>(response.Value.Count);

        foreach (var row in response.Value)
        {
            if (IsOutOfScope(row.Exchange))
            {
                continue;
            }

            var mapped = MapHolding(row);
            if (mapped.IsFailure)
            {
                return Result<IReadOnlyList<BrokerHolding>>.Failure(mapped.Error);
            }

            holdings.Add(mapped.Value);
        }

        return Result<IReadOnlyList<BrokerHolding>>.Success(holdings);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerBalance>>> GetBalancesAsync(CancellationToken ct = default)
    {
        var response = await _api
            .GetAsync<KiteMargins>(_options.MarginsPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(response.Error);
        }

        var equity = response.Value.Equity;
        if (equity is null)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(
                ZerodhaErrors.MissingField(_options.MarginsPath, "equity"));
        }

        static Money? Read(decimal? amount)
        {
            return amount is { } value ? new Money(value, Currency.Inr) : null;
        }

        // `net` is Kite's own answer to "what can this account place an order with" — cash plus
        // collateral and adhoc margin, minus everything already blocked. Reporting `available.cash`
        // instead would ignore pledged collateral and understate the buying power of any account
        // that has pledged stock, which is the common case for anyone trading F&O.
        var available = Read(equity.Net) ?? Money.Zero(Currency.Inr);

        return Result<IReadOnlyList<BrokerBalance>>.Success(
        [
            new BrokerBalance
            {
                Currency = Currency.Inr,
                AvailableToTrade = available,
                CashBalance = Read(equity.Available?.Cash),
                UsedMargin = Read(equity.Utilised?.Debits),
                AvailableMargin = available,
                Collateral = Read(equity.Available?.Collateral),
                RealisedPnl = Read(equity.Utilised?.M2mRealised),
                UnrealisedPnl = Read(equity.Utilised?.M2mUnrealised),
            },
        ]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Conversion moves an open position between margin products; nothing trades and no fill is
    /// generated.
    ///
    /// The <c>position_type</c> field is read from the position itself rather than inferred from
    /// the requested products. Kite distinguishes an <c>overnight</c> position from a <c>day</c>
    /// one, and that is a fact about how the position was acquired, not about what it is being
    /// converted to — a CNC holding bought this morning and one carried in from last week convert
    /// with different values for the same pair of products.
    /// </remarks>
    public async Task<Result> ConvertPositionAsync(
        ConvertPositionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Quantity <= Quantity.Zero)
        {
            return ZerodhaErrors.InvalidRequest("The quantity to convert must be positive.");
        }

        if (request.From == request.To)
        {
            return ZerodhaErrors.InvalidRequest("The source and target products are the same.");
        }

        var native = _symbols.ToNative(request.Instrument);
        if (native.IsFailure)
        {
            return Result.Failure(native.Error);
        }

        if (!ZerodhaInstrument.TrySplit(native.Value, out var exchange, out var tradingSymbol))
        {
            return ZerodhaErrors.MissingField("symbol translation", "exchange");
        }

        var side = ZerodhaMaps.ToNativeSide(request.Side);
        if (side.IsFailure)
        {
            return Result.Failure(side.Error);
        }

        var from = ZerodhaMaps.ToNativeProduct(request.From);
        if (from.IsFailure)
        {
            return Result.Failure(from.Error);
        }

        var to = ZerodhaMaps.ToNativeProduct(request.To);
        if (to.IsFailure)
        {
            return Result.Failure(to.Error);
        }

        var positionType = await ResolvePositionTypeAsync(
            exchange,
            tradingSymbol,
            from.Value,
            ct).ConfigureAwait(false);

        if (positionType.IsFailure)
        {
            return Result.Failure(positionType.Error);
        }

        var response = await _api.PutFormAsync<System.Text.Json.JsonElement>(
            _options.PositionsPath,
            [
                new KeyValuePair<string, string>("exchange", exchange),
                new KeyValuePair<string, string>("tradingsymbol", tradingSymbol),
                new KeyValuePair<string, string>("transaction_type", side.Value),
                new KeyValuePair<string, string>("position_type", positionType.Value),
                new KeyValuePair<string, string>(
                    "quantity",
                    ZerodhaNumber.Integer((long)decimal.Truncate(request.Quantity.Value))),
                new KeyValuePair<string, string>("old_product", from.Value),
                new KeyValuePair<string, string>("new_product", to.Value),
            ],
            ct).ConfigureAwait(false);

        return response.IsSuccess ? Result.Success() : Result.Failure(response.Error);
    }

    /// <summary>
    /// Whether the position being converted was carried in overnight or opened today.
    ///
    /// Kite's conversion route needs told, and it will not work it out: sending <c>day</c> for an
    /// overnight position is rejected with a message about quantity, which is one of the less
    /// helpful ways to learn you sent the wrong flag.
    /// </summary>
    private async Task<Result<string>> ResolvePositionTypeAsync(
        string exchange,
        string tradingSymbol,
        string product,
        CancellationToken ct)
    {
        var response = await _api
            .GetAsync<KitePositions>(_options.PositionsPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<string>.Failure(response.Error);
        }

        foreach (var row in response.Value.Net ?? [])
        {
            if (string.Equals(row.Exchange, exchange, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.TradingSymbol, tradingSymbol, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.Product, product, StringComparison.OrdinalIgnoreCase))
            {
                return (row.OvernightQuantity ?? 0m) != 0m
                    ? ZerodhaMaps.PositionTypeOvernight
                    : ZerodhaMaps.PositionTypeDay;
            }
        }

        return Result<string>.Failure(new Error(
            ConnectorErrorCodes.InvalidRequest,
            $"Kite has no open {product} position in {exchange}:{tradingSymbol} to convert.",
            VendorCode: null,
            VendorMessage: null,
            Context: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["exchange"] = exchange,
                ["tradingsymbol"] = tradingSymbol,
                ["product"] = product,
            }));
    }

    private Result<BrokerPosition> MapPosition(KitePosition row)
    {
        if (string.IsNullOrWhiteSpace(row.TradingSymbol))
        {
            return Result<BrokerPosition>.Failure(
                ZerodhaErrors.MissingField(_options.PositionsPath, "tradingsymbol"));
        }

        var instrument = _symbols.ToCanonical(row.TradingSymbol, row.Exchange);
        if (instrument.IsFailure)
        {
            return Result<BrokerPosition>.Failure(instrument.Error);
        }

        var effect = ZerodhaMaps.ToCanonicalPositionEffect(row.Product);
        if (effect.IsFailure)
        {
            return Result<BrokerPosition>.Failure(effect.Error);
        }

        return new BrokerPosition
        {
            Instrument = instrument.Value,
            NetQuantity = new Quantity(row.Quantity ?? 0m),
            PositionEffect = effect.Value,
            AveragePrice = new Money(row.AveragePrice ?? 0m, Currency.Inr),
            LastPrice = row.LastPrice is > 0m ? new Money(row.LastPrice.Value, Currency.Inr) : null,

            // Kite reports both directly on the position, so they are taken rather than
            // recomputed. Deriving unrealised P&L from last price and average price would
            // disagree with the broker's own figure on any position with a carried-forward leg,
            // where the two use different cost bases.
            UnrealisedPnl = row.Unrealised is { } unrealised
                ? new Money(unrealised, Currency.Inr)
                : null,
            RealisedPnl = row.Realised is { } realised ? new Money(realised, Currency.Inr) : null,
            BuyQuantity = new Quantity(row.BuyQuantity ?? 0m),
            SellQuantity = new Quantity(row.SellQuantity ?? 0m),
        };
    }

    private Result<BrokerHolding> MapHolding(KiteHolding row)
    {
        if (string.IsNullOrWhiteSpace(row.TradingSymbol))
        {
            return Result<BrokerHolding>.Failure(
                ZerodhaErrors.MissingField(_options.HoldingsPath, "tradingsymbol"));
        }

        var instrument = _symbols.ToCanonical(row.TradingSymbol, row.Exchange);
        if (instrument.IsFailure)
        {
            return Result<BrokerHolding>.Failure(instrument.Error);
        }

        // `quantity` is what has settled into the demat account; `t1_quantity` is bought today and
        // not yet delivered. Both are the trader's, and both show in the Kite app's holdings, so
        // both are reported — a position that vanishes for two days after being bought would make
        // the portfolio wrong in the most alarming possible way.
        var settled = row.Quantity ?? 0m;
        var inTransit = row.T1Quantity ?? 0m;

        return new BrokerHolding
        {
            Instrument = instrument.Value,
            Quantity = new Quantity(settled + inTransit),
            AveragePrice = new Money(row.AveragePrice ?? 0m, Currency.Inr),
            LastPrice = row.LastPrice is > 0m ? new Money(row.LastPrice.Value, Currency.Inr) : null,
            UnrealisedPnl = row.Pnl is { } pnl ? new Money(pnl, Currency.Inr) : null,

            // Kite's collateral_quantity is stock pledged for margin. It is still owned but cannot
            // be sold without unpledging, which is exactly what the contract's PledgedQuantity is
            // for.
            PledgedQuantity = new Quantity(row.CollateralQuantity ?? 0m),
            Isin = string.IsNullOrWhiteSpace(row.Isin) ? null : row.Isin,
        };
    }

    /// <summary>
    /// Whether a row belongs to a venue this connector does not serve. See the identical guard in
    /// <see cref="ZerodhaOrders"/>: a Kite account may hold MCX or currency positions, and this
    /// connector declares NSE and BSE only.
    /// </summary>
    private bool IsOutOfScope(string? exchange)
    {
        if (string.IsNullOrWhiteSpace(exchange) || ZerodhaMaps.ToCanonicalVenue(exchange).IsSuccess)
        {
            return false;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "{ConnectorId}: skipping a row on {Exchange}; it is outside this connector's declared venues.",
                ZerodhaAuth.ConnectorId,
                exchange);
        }

        return true;
    }
}
