using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Paper;

/// <summary>
/// Quotes, last prices, candles and level-1 depth, served from the engine's book with the
/// injected <see cref="IMarketDataSource"/> behind it.
///
/// Two prices exist and the difference matters. The ENGINE's last tick is what orders are
/// filling against right now; the SOURCE's last price is what the feed knows, including before
/// the engine has consumed a single tick. Quotes prefer the engine, so what the UI shows and
/// what an order fills at cannot disagree, and fall back to the source so a session that has
/// just started is not blank.
/// </summary>
/// <param name="engine">The simulated venue.</param>
/// <param name="source">The tick source behind it.</param>
/// <param name="requireSession">
/// <c>ConnectorBase.RequireSession</c>. Market data is gated on the session like everything
/// else: a connector whose quotes keep working after its session dies gives a trader a live
/// screen and a dead order path, which is the most dangerous state to be in.
/// </param>
public sealed class PaperMarketData(
    MatchingEngine engine,
    IMarketDataSource source,
    Func<Result<BrokerSession>> requireSession) : IConnectorMarketData
{
    /// <inheritdoc />
    public Task<Result<Quote>> GetQuoteAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        var session = requireSession();
        return Task.FromResult(session.IsFailure
            ? Result<Quote>.Failure(session.Error)
            : QuoteFor(instrument));
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyDictionary<InstrumentKey, Money>>> GetLtpAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        var session = requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(
                Result<IReadOnlyDictionary<InstrumentKey, Money>>.Failure(session.Error));
        }

        var result = new Dictionary<InstrumentKey, Money>();

        foreach (var instrument in instruments)
        {
            // Unknown instruments are omitted rather than failing the whole batch: a
            // watchlist with one bad row must still render the other twenty-nine.
            var price = LastPrice(instrument);
            if (price is { } money)
            {
                result[instrument] = money;
            }
        }

        return Task.FromResult(
            Result<IReadOnlyDictionary<InstrumentKey, Money>>.Success(result));
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyDictionary<InstrumentKey, Quote>>> GetQuotesAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        var session = requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(
                Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(session.Error));
        }

        var result = new Dictionary<InstrumentKey, Quote>();

        foreach (var instrument in instruments)
        {
            var quote = QuoteFor(instrument);
            if (quote.IsSuccess)
            {
                result[instrument] = quote.Value;
            }
        }

        return Task.FromResult(
            Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Success(result));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegated to the source, which is the only thing that has history: a replay source
    /// aggregates its own tape, a live paper source proxies the broker it shadows. The engine
    /// cannot synthesise this and must not pretend to.
    /// </remarks>
    public Task<Result<CandleSeries>> GetHistoricalAsync(
        HistoryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<CandleSeries>.Failure(session.Error));
        }

        if (request.To < request.From)
        {
            return Task.FromResult(Result<CandleSeries>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "The history range ends before it starts.")));
        }

        return Task.FromResult(source.History(request));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Level 1 only, and the manifest says so (<c>depthLevels: 1</c>). A tick carries one bid
    /// and one ask; anything deeper would be invented, and invented depth is what makes a
    /// large-size strategy backtest as though it could be worked into the book.
    /// </remarks>
    public Task<Result<MarketDepth>> GetDepthAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        var session = requireSession();
        if (session.IsFailure)
        {
            return Task.FromResult(Result<MarketDepth>.Failure(session.Error));
        }

        var tick = engine.LastTick(instrument);
        if (tick.IsFailure)
        {
            return Task.FromResult(Result<MarketDepth>.Failure(tick.Error));
        }

        var value = tick.Value;

        // Tick carries prices but no bid/ask SIZE — see Abstractions/Streaming.cs. Zero is the
        // honest answer: "a price whose size this feed does not report". Inventing a size here
        // would let a strategy size itself against liquidity nobody ever quoted.
        var bids = new List<DepthLevel>(1);
        if (value.BidPrice is { } bid)
        {
            bids.Add(new DepthLevel(bid, Quantity.Zero));
        }

        var asks = new List<DepthLevel>(1);
        if (value.AskPrice is { } ask)
        {
            asks.Add(new DepthLevel(ask, Quantity.Zero));
        }

        return Task.FromResult(Result<MarketDepth>.Success(new MarketDepth
        {
            Instrument = instrument,
            Bids = bids,
            Asks = asks,
            Timestamp = value.Timestamp,
        }));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Declined, matching <c>marketData.optionChain: false</c>. The engine can trade options
    /// perfectly well — the manifest lists the Option asset class — but assembling a chain
    /// means knowing every listed strike and expiry for an underlying, which is reference data
    /// the tick source does not carry. Claiming it and returning a chain with the two strikes
    /// that happen to have ticked would be worse than saying no.
    /// </remarks>
    public Task<Result<OptionChain>> GetOptionChainAsync(
        InstrumentKey underlying,
        DateOnly expiry,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<OptionChain>(
            "option chains (the paper tick source carries no strike ladder)");

    private Money? LastPrice(InstrumentKey instrument)
    {
        var tick = engine.LastTick(instrument);
        if (tick.IsSuccess)
        {
            return tick.Value.LastPrice;
        }

        var fallback = source.LastPrice(instrument);
        return fallback.IsSuccess ? fallback.Value : null;
    }

    private Result<Quote> QuoteFor(InstrumentKey instrument)
    {
        var tick = engine.LastTick(instrument);

        if (tick.IsSuccess)
        {
            var value = tick.Value;

            return new Quote
            {
                Instrument = instrument,
                LastPrice = value.LastPrice,
                Open = value.Open,
                High = value.High,
                Low = value.Low,
                PreviousClose = value.PreviousClose,
                BidPrice = value.BidPrice,
                AskPrice = value.AskPrice,
                // Quote has BidQuantity/AskQuantity; Tick does not carry them. Left null
                // rather than zero-filled, because null means "not reported" and zero would
                // mean "nothing on the book" — a difference a depth-aware strategy acts on.
                Volume = value.Volume,
                OpenInterest = value.OpenInterest,
                Timestamp = value.Timestamp,
            };
        }

        // Nothing has ticked yet. A price with no book around it is still a useful quote; the
        // alternative is a blank watchlist at the start of every session.
        var fallback = source.LastPrice(instrument);
        if (fallback.IsFailure)
        {
            return Result<Quote>.Failure(fallback.Error);
        }

        return new Quote
        {
            Instrument = instrument,
            LastPrice = fallback.Value,
            Timestamp = engine.MarketTime,
        };
    }
}
