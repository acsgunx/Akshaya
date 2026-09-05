using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Fyers;

/// <summary>
/// Positions, holdings, balances and product conversion.
///
/// The FYERS shape worth knowing before changing anything here is the FUNDS route. It does not
/// return named fields; it returns a LEDGER — ten rows, each with a numeric id, a display title
/// and two amounts (one for the capital market, one for commodities). The mapping from ledger
/// row to <see cref="BrokerBalance"/> is therefore by ID and never by title, because the title is
/// display text: matching "Available Balance" would break silently, and as a zero balance, the
/// day FYERS improves its wording.
/// </summary>
public sealed class FyersPortfolio : IConnectorPortfolio
{
    private readonly FyersApi _api;
    private readonly FyersOptions _options;
    private readonly ISymbolTranslator _symbols;
    private readonly ILogger _logger;

    internal FyersPortfolio(
        FyersApi api,
        FyersOptions options,
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
            .GetAsync<FyersPositionsResponse>(_options.PositionsPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerPosition>>.Failure(response.Error);
        }

        var rows = response.Value.NetPositions ?? [];
        var positions = new List<BrokerPosition>(rows.Count);

        foreach (var row in rows)
        {
            if (IsOutOfScope(row.Symbol))
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
            .GetAsync<FyersHoldingsResponse>(_options.HoldingsPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerHolding>>.Failure(response.Error);
        }

        var rows = response.Value.Holdings ?? [];
        var holdings = new List<BrokerHolding>(rows.Count);

        foreach (var row in rows)
        {
            if (IsOutOfScope(row.Symbol))
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
            .GetAsync<FyersFundsResponse>(_options.FundsPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(response.Error);
        }

        var ledger = response.Value.FundLimit;
        if (ledger is null or { Count: 0 })
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(
                FyersErrors.MissingField(_options.FundsPath, "fund_limit"));
        }

        var byId = new Dictionary<int, decimal>();
        foreach (var line in ledger)
        {
            if (line.Id is { } id)
            {
                // The equity ledger only. This connector declares NSE and BSE, so the commodity
                // column describes funds it cannot spend; folding the two together would show a
                // trader buying power they cannot use on the venues this link reaches.
                byId[id] = line.EquityAmount ?? 0m;
            }
        }

        Money? Read(int id)
        {
            return byId.TryGetValue(id, out var amount) ? new Money(amount, Currency.Inr) : null;
        }

        // Available Balance is the one figure that answers "can I place this order". Clear
        // Balance is settled cash and can exceed it; Total Balance includes collateral. Choosing
        // any of the others here would overstate buying power on an account holding pledged
        // stock, which is the common case.
        var available = Read(FundLedger.AvailableBalance)
            ?? Read(FundLedger.ClearBalance)
            ?? Money.Zero(Currency.Inr);

        return Result<IReadOnlyList<BrokerBalance>>.Success(
        [
            new BrokerBalance
            {
                Currency = Currency.Inr,
                AvailableToTrade = available,
                CashBalance = Read(FundLedger.ClearBalance),
                UsedMargin = Read(FundLedger.UtilisedAmount),
                AvailableMargin = available,
                Collateral = Read(FundLedger.Collaterals),
                RealisedPnl = Read(FundLedger.RealisedProfitAndLoss),

                // FYERS' funds ledger has no unrealised line. It belongs to the positions
                // response, and the Portfolio module blends the two — inventing a number here
                // would be a second, disagreeing source of truth for the same figure.
                UnrealisedPnl = null,
            },
        ]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Conversion moves an open position between margin products; nothing trades and no fill is
    /// generated. FYERS refuses two of the four directions outright — a CNC or MTF position cannot
    /// be converted, and nothing can be converted INTO MTF — and those are checked here rather
    /// than being sent to be refused, so the UI can explain the rule instead of relaying an
    /// error code.
    ///
    /// The <c>overnight</c> flag is read from the position itself. FYERS documents it as "1 if the
    /// position is carried forward, 0 if it was taken today, irrespective of its product type",
    /// so it cannot be derived from the requested products — it is a fact about the position, and
    /// getting it wrong makes FYERS reject the conversion with a message about quantity.
    /// </remarks>
    public async Task<Result> ConvertPositionAsync(
        ConvertPositionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Quantity <= Quantity.Zero)
        {
            return FyersErrors.InvalidRequest("The quantity to convert must be positive.");
        }

        if (request.From == request.To)
        {
            return FyersErrors.InvalidRequest("The source and target products are the same.");
        }

        var from = FyersMaps.ToNativeProduct(request.From);
        if (from.IsFailure)
        {
            return Result.Failure(from.Error);
        }

        var to = FyersMaps.ToNativeProduct(request.To);
        if (to.IsFailure)
        {
            return Result.Failure(to.Error);
        }

        if (from.Value is FyersMaps.ProductCnc or FyersMaps.ProductMtf)
        {
            return ConnectorErrors.NotSupported(
                $"converting a {from.Value} position. FYERS allows conversion only out of INTRADAY and "
                + "MARGIN positions");
        }

        if (to.Value == FyersMaps.ProductMtf)
        {
            return ConnectorErrors.NotSupported(
                "converting a position into MTF. FYERS only opens margin-funded positions directly");
        }

        var symbol = _symbols.ToNative(request.Instrument);
        if (symbol.IsFailure)
        {
            return Result.Failure(symbol.Error);
        }

        var side = FyersMaps.ToNativeSide(request.Side);
        if (side.IsFailure)
        {
            return Result.Failure(side.Error);
        }

        var positionId = BuildPositionId(symbol.Value, from.Value);

        var overnight = await IsOvernightAsync(positionId, ct).ConfigureAwait(false);
        if (overnight.IsFailure)
        {
            return Result.Failure(overnight.Error);
        }

        var body = new FyersConvertPositionBody
        {
            Symbol = positionId,
            PositionSide = side.Value,
            ConvertQuantity = (int)decimal.Truncate(request.Quantity.Value),
            ConvertFrom = from.Value,
            ConvertTo = to.Value,
            Overnight = overnight.Value ? 1 : 0,
        };

        var response = await _api
            .PostJsonAsync<FyersConvertPositionResponse>(_options.PositionsPath, body, ct)
            .ConfigureAwait(false);

        return response.IsSuccess ? Result.Success() : Result.Failure(response.Error);
    }

    /// <summary>
    /// A FYERS position id: the symbol and the product joined by a hyphen —
    /// <c>NSE:SBIN-EQ-INTRADAY</c>. Built here rather than read from the positions response so a
    /// conversion does not depend on having listed positions first.
    /// </summary>
    internal static string BuildPositionId(string nativeSymbol, string nativeProduct) =>
        $"{nativeSymbol}-{nativeProduct}";

    private async Task<Result<bool>> IsOvernightAsync(string positionId, CancellationToken ct)
    {
        var response = await _api
            .GetAsync<FyersPositionsResponse>(_options.PositionsPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<bool>.Failure(response.Error);
        }

        var position = response.Value.NetPositions?.FirstOrDefault(p =>
            string.Equals(p.Id, positionId, StringComparison.OrdinalIgnoreCase));

        if (position is null)
        {
            return Result<bool>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"FYERS has no open position '{positionId}' to convert.",
                VendorCode: null,
                VendorMessage: null,
                Context: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["positionId"] = positionId,
                }));
        }

        return (position.CarriedForwardBuyQuantity ?? 0m) + (position.CarriedForwardSellQuantity ?? 0m) > 0m;
    }

    private Result<BrokerPosition> MapPosition(FyersPosition row)
    {
        if (string.IsNullOrWhiteSpace(row.Symbol))
        {
            return Result<BrokerPosition>.Failure(FyersErrors.MissingField(_options.PositionsPath, "symbol"));
        }

        var instrument = _symbols.ToCanonical(row.Symbol);
        if (instrument.IsFailure)
        {
            return Result<BrokerPosition>.Failure(instrument.Error);
        }

        var effect = FyersMaps.ToCanonicalPositionEffect(row.ProductType);
        if (effect.IsFailure)
        {
            return Result<BrokerPosition>.Failure(effect.Error);
        }

        // netAvg is the average of the open leg; avgPrice is the average of everything traded.
        // The open leg is what a position's cost basis means, so it wins where both are present.
        var averagePrice = row.NetAveragePrice is > 0m
            ? row.NetAveragePrice.Value
            : row.AveragePrice ?? 0m;

        return new BrokerPosition
        {
            Instrument = instrument.Value,
            NetQuantity = new Quantity(row.NetQuantity ?? 0m),
            PositionEffect = effect.Value,
            AveragePrice = new Money(averagePrice, Currency.Inr),
            LastPrice = row.LastPrice is > 0m ? new Money(row.LastPrice.Value, Currency.Inr) : null,

            // FYERS reports unrealised P&L directly on the position, so it is taken rather than
            // recomputed. Deriving it from ltp and average price would disagree with the broker's
            // own figure on any position with a carried-forward leg, where the two use different
            // cost bases.
            UnrealisedPnl = row.UnrealisedProfit is { } unrealised
                ? new Money(unrealised, Currency.Inr)
                : null,
            RealisedPnl = row.RealisedProfit is { } realised ? new Money(realised, Currency.Inr) : null,
            BuyQuantity = new Quantity(row.BuyQuantity ?? 0m),
            SellQuantity = new Quantity(row.SellQuantity ?? 0m),
        };
    }

    private Result<BrokerHolding> MapHolding(FyersHolding row)
    {
        if (string.IsNullOrWhiteSpace(row.Symbol))
        {
            return Result<BrokerHolding>.Failure(FyersErrors.MissingField(_options.HoldingsPath, "symbol"));
        }

        var instrument = _symbols.ToCanonical(row.Symbol);
        if (instrument.IsFailure)
        {
            return Result<BrokerHolding>.Failure(instrument.Error);
        }

        return new BrokerHolding
        {
            Instrument = instrument.Value,

            // remainingQuantity is quantity minus whatever was sold today — what can still be
            // sold. Reporting the morning's quantity instead would let a trader sell the same
            // shares twice and be short after settlement.
            Quantity = new Quantity(row.RemainingQuantity ?? row.Quantity ?? 0m),
            AveragePrice = new Money(row.CostPrice ?? 0m, Currency.Inr),
            LastPrice = row.LastPrice is > 0m ? new Money(row.LastPrice.Value, Currency.Inr) : null,
            UnrealisedPnl = row.ProfitAndLoss is { } pnl ? new Money(pnl, Currency.Inr) : null,
            PledgedQuantity = new Quantity(row.CollateralQuantity ?? 0m),
            Isin = string.IsNullOrWhiteSpace(row.Isin) ? null : row.Isin,
        };
    }

    /// <summary>
    /// Whether a row belongs to a venue this connector does not serve. See the identical guard in
    /// <see cref="FyersOrders"/>: a FYERS account may hold MCX positions, and this connector
    /// declares NSE and BSE only.
    /// </summary>
    private bool IsOutOfScope(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var separator = symbol.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || FyersMaps.ToCanonicalVenue(symbol[..separator]).IsSuccess)
        {
            return false;
        }

        // Guarded rather than left to the logging framework: this runs once per order-book row,
        // and the params array behind a structured-logging call is allocated whether or not the
        // level is enabled.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "{ConnectorId}: skipping {Symbol}; its venue is outside this connector's declared venues.",
                FyersAuth.ConnectorId,
                symbol);
        }

        return true;
    }

    /// <summary>
    /// The FYERS funds ledger, addressed by row id.
    ///
    /// The ids are stable and documented; the titles beside them are not a contract. Anything
    /// that reads this ledger reads it through these constants.
    /// </summary>
    private static class FundLedger
    {
        public const int TotalBalance = 1;
        public const int UtilisedAmount = 2;
        public const int ClearBalance = 3;
        public const int RealisedProfitAndLoss = 4;
        public const int Collaterals = 5;
        public const int AvailableBalance = 10;
    }
}

/// <summary>Position conversion answers with the standard envelope and no payload we read.</summary>
internal sealed class FyersConvertPositionResponse : FyersResponse;
