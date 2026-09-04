using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.MStock;

/// <summary>
/// Positions, holdings and balances.
///
/// mStock is a single-currency broker, so every figure here is INR. That is stated once, in
/// <see cref="Inr"/>, rather than assumed at twenty call sites — the day this connector grows
/// a second currency, the compiler will point at every place that needs revisiting.
/// </summary>
public sealed class MStockPortfolio : IConnectorPortfolio
{
    private static readonly Currency Inr = Currency.Inr;

    private readonly MStockApi _api;
    private readonly MStockOptions _options;
    private readonly ISymbolTranslator _symbols;
    private readonly IMStockInstrumentLookup? _instruments;

    /// <summary>Creates the portfolio facet.</summary>
    internal MStockPortfolio(
        MStockApi api,
        MStockOptions options,
        ISymbolTranslator symbols,
        IMStockInstrumentLookup? instruments = null)
    {
        _api = api;
        _options = options;
        _symbols = symbols;
        _instruments = instruments;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(
        CancellationToken ct = default)
    {
        var response = await _api
            .GetAsync<MStockPositionsData>(_options.PositionsPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerPosition>>.Failure(response.Error);
        }

        // mStock splits positions into "net" and "day". The day bucket only covers what was
        // traded today, so a carried-forward NRML future appears in net and not in day —
        // reading day would silently under-report the trader's real exposure. Net is the one
        // that answers "what am I holding".
        var rows = response.Value.Net ?? Array.Empty<MStockPositionDto>();
        var positions = new List<BrokerPosition>(rows.Count);

        foreach (var dto in rows)
        {
            var mapped = MapPosition(dto);
            if (mapped.IsFailure)
            {
                return Result<IReadOnlyList<BrokerPosition>>.Failure(mapped.Error);
            }

            positions.Add(mapped.Value);
        }

        return Result<IReadOnlyList<BrokerPosition>>.Success(positions);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerHolding>>> GetHoldingsAsync(
        CancellationToken ct = default)
    {
        var response = await _api
            .GetAsync<IReadOnlyList<MStockHoldingDto>>(_options.HoldingsPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerHolding>>.Failure(response.Error);
        }

        var holdings = new List<BrokerHolding>(response.Value.Count);
        foreach (var dto in response.Value)
        {
            var mapped = MapHolding(dto);
            if (mapped.IsFailure)
            {
                return Result<IReadOnlyList<BrokerHolding>>.Failure(mapped.Error);
            }

            holdings.Add(mapped.Value);
        }

        return Result<IReadOnlyList<BrokerHolding>>.Success(holdings);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<BrokerBalance>>> GetBalancesAsync(
        CancellationToken ct = default)
    {
        // An ARRAY of flat rows, one per segment — not the nested equity/commodity object the
        // Kite-lineage APIs return. See MStockFundRow.
        var response = await _api
            .GetAsync<IReadOnlyList<MStockFundRow>>(_options.FundsPath, query: null, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<BrokerBalance>>.Failure(response.Error);
        }

        if (response.Value.Count == 0)
        {
            // An account with no funding rows is a real state (a freshly opened account), not a
            // malformed response. An empty list is the honest answer; inventing a zero balance
            // would tell the risk gate the account has ₹0 to trade with, which is a different
            // and much more dangerous claim than "we do not know".
            return Result<IReadOnlyList<BrokerBalance>>.Success([]);
        }

        // The contract returns a LIST of balances because a Moomoo or IBKR account holds
        // several currencies at once. mStock reports one row per segment, all in INR — the
        // shape stays the same so the portfolio module needs no special case for India.
        var balances = new List<BrokerBalance>(response.Value.Count);

        foreach (var row in response.Value)
        {
            balances.Add(new BrokerBalance
            {
                Currency = Inr,

                // AVAILABLE_BALANCE is what the account can actually trade with. CLEAR_BALANCE
                // excludes collateral and unsettled payins the account can already trade
                // against, and showing that smaller number would have the trader believe
                // orders will bounce when they will not.
                AvailableToTrade = Rupees(row.AvailableBalance ?? row.ClearBalance ?? 0m),
                CashBalance = RupeesOrNull(row.ClearBalance),
                UsedMargin = RupeesOrNull(row.AmountUtilized),
                AvailableMargin = RupeesOrNull(row.AvailableBalance),
                Collateral = RupeesOrNull(row.Collaterals),
                RealisedPnl = RupeesOrNull(row.RealisedProfits),

                // mStock reports a COMBINED mark-to-market, not a separate unrealised figure.
                // Reporting the combined number as "unrealised" would double-count realised
                // profits, so this stays null: the portfolio blender treats null as "this
                // broker does not report it" and omits it rather than showing a wrong total.
                UnrealisedPnl = null,
            });
        }

        return Result<IReadOnlyList<BrokerBalance>>.Success(balances);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>POST /openapi/typea/portfolio/convertposition</c>, form-encoded.
    ///
    /// The route answers with an EMPTY 200 on success — no order id, no confirmation payload,
    /// nothing to bind. That is why this returns a bare <see cref="Result"/>: there is no ack
    /// to hand back, and inventing one would be a claim the broker never made. The caller's
    /// next positions read is what confirms the conversion actually happened.
    ///
    /// mStock accepts only <c>DAY</c> as the position type, which is the only kind of position
    /// that can be converted at all — an overnight position has already settled into its
    /// product.
    /// </remarks>
    public async Task<Result> ConvertPositionAsync(
        ConvertPositionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Quantity.Value <= 0m)
        {
            return Result.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "The quantity to convert must be positive."));
        }

        if (request.Quantity.IsFractional)
        {
            return Result.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"mStock trades whole units only; {request.Quantity} is fractional."));
        }

        if (request.From == request.To)
        {
            return Result.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"The position is already held as {request.From}; there is nothing to convert."));
        }

        var symbol = _symbols.ToNative(request.Instrument);
        if (symbol.IsFailure)
        {
            return Result.Failure(symbol.Error);
        }

        var exchange = MStockMaps.ToNativeExchange(request.Instrument.Venue, request.Instrument.AssetClass);
        if (exchange.IsFailure)
        {
            return Result.Failure(exchange.Error);
        }

        var side = MStockMaps.ToNativeSide(request.Side);
        if (side.IsFailure)
        {
            return Result.Failure(side.Error);
        }

        var oldProduct = MStockMaps.ToNativeProduct(request.From);
        if (oldProduct.IsFailure)
        {
            return Result.Failure(oldProduct.Error);
        }

        var newProduct = MStockMaps.ToNativeProduct(request.To);
        if (newProduct.IsFailure)
        {
            return Result.Failure(newProduct.Error);
        }

        var body = new MStockConvertPositionRequest
        {
            TradingSymbol = symbol.Value,
            Exchange = exchange.Value,
            TransactionType = side.Value,
            PositionType = MStockMaps.PositionTypeDay,
            Quantity = MStockNumber.Quantity(request.Quantity.Value),
            OldProduct = oldProduct.Value,
            NewProduct = newProduct.Value,
        };

        // PostVoidFormAsync, not PostFormAsync: the success envelope carries no data member,
        // and demanding one would report every successful conversion as a malformed response —
        // the same fault that logout hit, written up in mstock-login-response.md.
        return await _api
            .PostVoidFormAsync(_options.ConvertPositionPath, body.ToForm(), ct)
            .ConfigureAwait(false);
    }

    private Result<BrokerPosition> MapPosition(MStockPositionDto dto)
    {
        var instrument = Resolve(dto.TradingSymbol, dto.Exchange, _options.PositionsPath, dto.InstrumentToken);
        if (instrument.IsFailure)
        {
            return Result<BrokerPosition>.Failure(instrument.Error);
        }

        var effect = MStockMaps.ToCanonicalPositionEffect(dto.Product ?? string.Empty);
        if (effect.IsFailure)
        {
            return Result<BrokerPosition>.Failure(effect.Error);
        }

        var net = dto.Quantity ?? 0m;

        // mStock reports "pnl" as the total and "unrealised"/"realised" as its parts, but not
        // every build sends all three. Derive the missing part rather than dropping it: a
        // position with a blank P&L column is a support ticket every single time.
        var realised = dto.Realised;
        var unrealised = dto.Unrealised
                         ?? (dto.Pnl is { } total && realised is { } r ? (decimal?)(total - r) : null);

        return new BrokerPosition
        {
            Instrument = instrument.Value,
            NetQuantity = new Quantity(net),
            PositionEffect = effect.Value,
            AveragePrice = Rupees(dto.AveragePrice ?? 0m),
            LastPrice = RupeesOrNull(dto.LastPrice),
            UnrealisedPnl = RupeesOrNull(unrealised),
            RealisedPnl = RupeesOrNull(realised),
            BuyQuantity = new Quantity(dto.BuyQuantity ?? 0m),
            SellQuantity = new Quantity(dto.SellQuantity ?? 0m),
        };
    }

    private Result<BrokerHolding> MapHolding(MStockHoldingDto dto)
    {
        var instrument = Resolve(dto.TradingSymbol, dto.Exchange, _options.HoldingsPath, dto.InstrumentToken);
        if (instrument.IsFailure)
        {
            return Result<BrokerHolding>.Failure(instrument.Error);
        }

        return new BrokerHolding
        {
            Instrument = instrument.Value,

            // `quantity` ALONE, and t1_quantity is deliberately NOT added to it.
            //
            // This previously computed `quantity + t1_quantity`, which is correct for Kite —
            // where the two are disjoint, `quantity` being settled demat stock and
            // `t1_quantity` the unsettled tranche. mStock does not split them that way: a real
            // account holding 400 shares, with its own web console reporting "Unsettled Qty 0,
            // DP Qty 400", was shown here as 800. Every figure derived from the quantity
            // doubled with it — value, and the return percentage (-6.82% against a true
            // -13.64%) — while the P&L, which comes straight from the broker's own `pnl`
            // field, stayed right. A position that disagrees with its own percentage is how
            // this was noticed.
            //
            // Using `quantity` alone reproduces that account exactly: 400 x 412.00 = 164,800
            // invested, 400 x 355.80 = 142,320 current, -22,480 = -13.64%.
            //
            // If some other mStock account does report a separate unsettled tranche, this
            // under-reports it, and that is the right way to be wrong: a trader who believes
            // they hold 800 and sells 800 goes short or gets rejected, whereas one who believes
            // they hold 400 and actually hold more has simply left stock unsold.
            Quantity = new Quantity(dto.Quantity ?? 0m),
            AveragePrice = Rupees(dto.AveragePrice ?? 0m),
            LastPrice = RupeesOrNull(dto.LastPrice),
            UnrealisedPnl = RupeesOrNull(dto.Pnl),

            // Collateral stock is pledged against margin and cannot be sold until it is
            // unpledged; the sell ticket needs to know that before the exchange tells it.
            PledgedQuantity = new Quantity(dto.CollateralQuantity ?? 0m),
            Isin = dto.Isin,
        };
    }

    private Result<InstrumentKey> Resolve(
        string? tradingSymbol,
        string? exchange,
        string route,
        long? instrumentToken = null)
    {
        // THE NUMERIC TOKEN FIRST, when the script master is loaded.
        //
        // Holdings do not send a trading symbol at all — they send the COMPANY NAME
        // ("BANK OF MAHARASHTRA", "RASHTRIYA CHEMICALS & FER") in the tradingsymbol field, with
        // "exchange": null beside it. Neither the master nor the structural fallback can
        // identify a position from that, so resolving by symbol alone failed for every holding
        // a user owns. instrument_token is unambiguous and is on every row.
        if (instrumentToken is > 0 and <= uint.MaxValue
            && _instruments is not null
            && _instruments.TryGetByToken((uint)instrumentToken.Value, out var byToken))
        {
            return byToken;
        }

        if (string.IsNullOrWhiteSpace(tradingSymbol))
        {
            return Result<InstrumentKey>.Failure(MStockErrors.MissingField(route, "tradingsymbol"));
        }

        var resolved = _symbols.ToCanonical(tradingSymbol, exchange);
        if (resolved.IsSuccess)
        {
            return resolved;
        }

        return Result<InstrumentKey>.Failure(new Error(
            resolved.Error.Code,
            $"mStock reported a position or holding in '{tradingSymbol}' "
            + $"({exchange ?? "no exchange"}) which this connector cannot identify. "
            + resolved.Error.Message,
            resolved.Error.VendorCode ?? tradingSymbol,
            resolved.Error.VendorMessage,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["route"] = route,
                ["tradingsymbol"] = tradingSymbol,
                ["exchange"] = exchange ?? string.Empty,
            }));
    }

    /// <summary>Wraps a raw vendor number in the connector's single currency.</summary>
    private static Money Rupees(decimal amount) => new(amount, Inr);

    private static Money? RupeesOrNull(decimal? amount) =>
        amount is { } value ? new Money(value, Inr) : null;
}
