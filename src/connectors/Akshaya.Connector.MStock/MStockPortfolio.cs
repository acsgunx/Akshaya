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

    /// <summary>Creates the portfolio facet.</summary>
    internal MStockPortfolio(MStockApi api, MStockOptions options, ISymbolTranslator symbols)
    {
        _api = api;
        _options = options;
        _symbols = symbols;
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

    private Result<BrokerPosition> MapPosition(MStockPositionDto dto)
    {
        var instrument = Resolve(dto.TradingSymbol, dto.Exchange, _options.PositionsPath);
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
        var instrument = Resolve(dto.TradingSymbol, dto.Exchange, _options.HoldingsPath);
        if (instrument.IsFailure)
        {
            return Result<BrokerHolding>.Failure(instrument.Error);
        }

        var quantity = dto.Quantity ?? 0m;
        var t1 = dto.T1Quantity ?? 0m;

        return new BrokerHolding
        {
            Instrument = instrument.Value,

            // T1 stock is bought-but-unsettled and IS sellable in India, so it belongs in the
            // headline quantity. Leaving it out makes a holding bought yesterday look like it
            // vanished.
            Quantity = new Quantity(quantity + t1),
            AveragePrice = Rupees(dto.AveragePrice ?? 0m),
            LastPrice = RupeesOrNull(dto.LastPrice),
            UnrealisedPnl = RupeesOrNull(dto.Pnl),

            // Collateral stock is pledged against margin and cannot be sold until it is
            // unpledged; the sell ticket needs to know that before the exchange tells it.
            PledgedQuantity = new Quantity(dto.CollateralQuantity ?? 0m),
            Isin = dto.Isin,
        };
    }

    private Result<InstrumentKey> Resolve(string? tradingSymbol, string? exchange, string route)
    {
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
