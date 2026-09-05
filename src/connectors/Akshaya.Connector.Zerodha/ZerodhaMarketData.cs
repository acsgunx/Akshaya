using System.Globalization;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// Quotes, depth, candles and the option chain.
///
/// Kite's data surface is generous — one quote call covers five hundred instruments and carries
/// the full five-level book, and candles reach back years — but it is rate limited far harder than
/// the rest of the API: ONE quote per second and three historical calls per second, against ten
/// for everything else. Every batching decision in this class exists because of that ceiling; a
/// facet that made one call per instrument would exhaust a watchlist's budget before it finished
/// drawing.
/// </summary>
public sealed class ZerodhaMarketData : IConnectorMarketData
{
    private readonly ZerodhaApi _api;
    private readonly ZerodhaOptions _options;
    private readonly ISymbolTranslator _symbols;
    private readonly ZerodhaInstrumentCache _instruments;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _venueZone;

    internal ZerodhaMarketData(
        ZerodhaApi api,
        ZerodhaOptions options,
        ISymbolTranslator symbols,
        ZerodhaInstrumentCache instruments,
        IClock clock)
    {
        _api = api;
        _options = options;
        _symbols = symbols;
        _instruments = instruments;
        _clock = clock;
        _venueZone = ZerodhaTime.ResolveZone(options.VenueTimeZoneId);
    }

    /// <inheritdoc />
    public async Task<Result<Quote>> GetQuoteAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        var quotes = await GetQuotesAsync([instrument], ct).ConfigureAwait(false);
        if (quotes.IsFailure)
        {
            return Result<Quote>.Failure(quotes.Error);
        }

        return quotes.Value.TryGetValue(instrument, out var quote)
            ? quote
            : Result<Quote>.Failure(ConnectorErrors.InstrumentNotFound(instrument));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<InstrumentKey, Money>>> GetLtpAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        var prices = new Dictionary<InstrumentKey, Money>(instruments.Count);
        if (instruments.Count == 0)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Money>>.Success(prices);
        }

        var keys = ResolveKeys(instruments);
        if (keys.IsFailure)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Money>>.Failure(keys.Error);
        }

        // The dedicated LTP route rather than the full quote: it costs the same one request per
        // second but moves a fraction of the bytes, which matters on a watchlist refresh.
        foreach (var chunk in Chunk(keys.Value.Keys, _options.MaxQuoteInstruments))
        {
            ct.ThrowIfCancellationRequested();

            var response = await _api
                .GetAsync<Dictionary<string, KiteQuote>>(
                    _options.LtpPath,
                    new ZerodhaQuery().AddAll("i", chunk),
                    ct)
                .ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<IReadOnlyDictionary<InstrumentKey, Money>>.Failure(response.Error);
            }

            foreach (var (native, quote) in response.Value)
            {
                if (keys.Value.TryGetValue(native, out var key) && quote.LastPrice is { } price)
                {
                    prices[key] = new Money(price, Currency.Inr);
                }
            }
        }

        return Result<IReadOnlyDictionary<InstrumentKey, Money>>.Success(prices);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<InstrumentKey, Quote>>> GetQuotesAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        var quotes = new Dictionary<InstrumentKey, Quote>(instruments.Count);
        if (instruments.Count == 0)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Success(quotes);
        }

        var keys = ResolveKeys(instruments);
        if (keys.IsFailure)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(keys.Error);
        }

        var fetched = await FetchQuotesAsync(keys.Value, ct).ConfigureAwait(false);
        return fetched.IsFailure
            ? Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(fetched.Error)
            : Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Success(fetched.Value);
    }

    /// <inheritdoc />
    public async Task<Result<MarketDepth>> GetDepthAsync(
        InstrumentKey instrument,
        CancellationToken ct = default)
    {
        var native = _symbols.ToNative(instrument);
        if (native.IsFailure)
        {
            return Result<MarketDepth>.Failure(native.Error);
        }

        var response = await _api
            .GetAsync<Dictionary<string, KiteQuote>>(
                _options.QuotePath,
                new ZerodhaQuery().Add("i", native.Value),
                ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<MarketDepth>.Failure(response.Error);
        }

        // Kite omits the key entirely when it has no data for an instrument rather than returning
        // an empty entry, so a miss here is InstrumentNotFound and not an empty book — an empty
        // book is a tradeable claim, and a wrong one.
        if (!response.Value.TryGetValue(native.Value, out var quote) || quote.Depth is null)
        {
            return Result<MarketDepth>.Failure(ConnectorErrors.InstrumentNotFound(instrument));
        }

        return new MarketDepth
        {
            Instrument = instrument,
            Bids = MapLevels(quote.Depth.Buy),
            Asks = MapLevels(quote.Depth.Sell),
            Timestamp = ZerodhaTime.ParseOr(quote.Timestamp ?? quote.LastTradeTime, _clock.UtcNow),
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Kite addresses candles by NUMERIC INSTRUMENT TOKEN, not by symbol, so this needs the
    /// instrument master. That is a hard dependency rather than a degradation — there is no
    /// symbol-based history route to fall back to — which is why the failure names the master
    /// explicitly instead of reporting the instrument as unknown.
    /// </remarks>
    public async Task<Result<CandleSeries>> GetHistoricalAsync(
        HistoryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.To < request.From)
        {
            return Result<CandleSeries>.Failure(
                ZerodhaErrors.InvalidRequest("The history range ends before it begins."));
        }

        var interval = ZerodhaMaps.ToNativeInterval(request.TimeFrame);
        if (interval.IsFailure)
        {
            return Result<CandleSeries>.Failure(interval.Error);
        }

        if (!_instruments.TryGetToken(request.Instrument, out var token))
        {
            return Result<CandleSeries>.Failure(_instruments.IsLoaded
                ? ConnectorErrors.InstrumentNotFound(request.Instrument)
                : ZerodhaErrors.MasterNotLoaded($"Charting {request.Instrument}"));
        }

        var path = string.Format(
            CultureInfo.InvariantCulture,
            _options.HistoricalPathFormat,
            token.ToString(CultureInfo.InvariantCulture),
            interval.Value);

        var query = new ZerodhaQuery()
            // Bounds are IST wall-clock, because that is the only thing Kite accepts. Formatting
            // them from UTC would shift every intraday window by five and a half hours.
            .Add("from", ZerodhaTime.FormatBound(request.From, _venueZone))
            .Add("to", ZerodhaTime.FormatBound(request.To, _venueZone))

            // Open interest is free to ask for and meaningless to omit on a derivative; on cash
            // instruments Kite simply leaves the column off.
            .Add("oi", request.Instrument.IsDerivative ? "1" : "0")

            // Continuous data stitches expired contracts behind a live one. Only requested for
            // derivatives, where it is the difference between one month of history and years of it.
            .Add("continuous", request.Instrument.IsDerivative ? "1" : "0");

        var response = await _api.GetAsync<KiteCandles>(path, query, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result<CandleSeries>.Failure(response.Error);
        }

        var rows = response.Value.Candles ?? [];
        var candles = new List<Candle>(rows.Count);

        foreach (var row in rows)
        {
            // POSITIONAL, and the order is the contract: timestamp, open, high, low, close,
            // volume, and open interest when it was asked for. A short row is dropped rather than
            // padded — a candle with a zero low prints as a spike to nothing on every chart and
            // every indicator that reads it.
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6)
            {
                continue;
            }

            if (ZerodhaTime.Parse(row[0].GetString()) is not { } openTime)
            {
                continue;
            }

            candles.Add(new Candle
            {
                OpenTime = openTime,
                Open = row[1].GetDecimal(),
                High = row[2].GetDecimal(),
                Low = row[3].GetDecimal(),
                Close = row[4].GetDecimal(),
                Volume = row[5].GetInt64(),
                OpenInterest = row.GetArrayLength() >= 7 ? row[6].GetInt64() : null,
            });
        }

        return new CandleSeries
        {
            Instrument = request.Instrument,
            TimeFrame = request.TimeFrame,
            Currency = Currency.Inr,
            Candles = candles,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// KITE PUBLISHES NO OPTION-CHAIN ENDPOINT. This one is assembled locally, and it is worth
    /// being explicit about why that is a faithful implementation rather than an invented
    /// capability.
    ///
    /// The contract's <see cref="OptionChainRow"/> asks for a strike, a call quote, a put quote
    /// and the two open-interest figures — nothing that has to come from a dedicated endpoint. The
    /// instrument master already lists every contract on an underlying for a given expiry, and one
    /// quote call covers five hundred instruments with open interest included. So the chain is
    /// exactly "the contracts, plus their quotes", and both halves are the broker's own data.
    ///
    /// What is genuinely absent is greeks — and the contract has no field for them, so nothing is
    /// lost. What this DOES depend on is the instrument master being ingested, which is why the
    /// failure says so rather than reporting the underlying as unknown.
    /// </remarks>
    public async Task<Result<OptionChain>> GetOptionChainAsync(
        InstrumentKey underlying,
        DateOnly expiry,
        CancellationToken ct = default)
    {
        var contracts = _instruments.OptionsFor(underlying, expiry);
        if (contracts.Count == 0)
        {
            return Result<OptionChain>.Failure(_instruments.IsLoaded
                ? new Error(
                    ConnectorErrorCodes.InstrumentNotFound,
                    $"Kite lists no {expiry:yyyy-MM-dd} option contracts on {underlying.Symbol}.",
                    VendorCode: null,
                    VendorMessage: null,
                    Context: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["underlying"] = underlying.Symbol,
                        ["expiry"] = expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    })
                : ZerodhaErrors.MasterNotLoaded($"The {underlying.Symbol} option chain"));
        }

        var wanted = new Dictionary<string, InstrumentKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var contract in contracts)
        {
            wanted[contract.QualifiedSymbol] = contract.Definition.Key;
        }

        // The underlying itself rides along in the same batch so its price costs no extra call.
        var underlyingNative = _symbols.ToNative(underlying);
        if (underlyingNative.IsSuccess)
        {
            wanted[underlyingNative.Value] = underlying;
        }

        var quotes = await FetchQuotesAsync(wanted, ct).ConfigureAwait(false);
        if (quotes.IsFailure)
        {
            return Result<OptionChain>.Failure(quotes.Error);
        }

        var byStrike = new SortedDictionary<decimal, ChainSlot>();

        foreach (var contract in contracts)
        {
            var key = contract.Definition.Key;
            if (key.Strike is not { } strike || key.Right is not { } right)
            {
                continue;
            }

            quotes.Value.TryGetValue(key, out var quote);

            var slot = byStrike.TryGetValue(strike, out var existing) ? existing : default;

            byStrike[strike] = right == OptionRight.Call
                ? slot with { Call = quote, CallOpenInterest = quote?.OpenInterest }
                : slot with { Put = quote, PutOpenInterest = quote?.OpenInterest };
        }

        var rows = new List<OptionChainRow>(byStrike.Count);
        foreach (var (strike, slot) in byStrike)
        {
            rows.Add(new OptionChainRow
            {
                Strike = strike,
                Call = slot.Call,
                Put = slot.Put,
                CallOpenInterest = slot.CallOpenInterest,
                PutOpenInterest = slot.PutOpenInterest,
            });
        }

        return new OptionChain
        {
            Underlying = underlying,
            Expiry = expiry,
            Rows = rows,
            UnderlyingPrice = quotes.Value.TryGetValue(underlying, out var spot) ? spot.LastPrice : null,
        };
    }

    // --- shared plumbing -----------------------------------------------------------------------

    /// <summary>
    /// Translates every instrument up front, so an untradable one fails BEFORE any network call
    /// rather than coming back as a missing entry the caller has to reason about.
    /// </summary>
    private Result<Dictionary<string, InstrumentKey>> ResolveKeys(IEnumerable<InstrumentKey> instruments)
    {
        var keys = new Dictionary<string, InstrumentKey>(StringComparer.OrdinalIgnoreCase);

        foreach (var instrument in instruments)
        {
            var native = _symbols.ToNative(instrument);
            if (native.IsFailure)
            {
                return Result<Dictionary<string, InstrumentKey>>.Failure(native.Error);
            }

            keys[native.Value] = instrument;
        }

        return keys;
    }

    private async Task<Result<Dictionary<InstrumentKey, Quote>>> FetchQuotesAsync(
        Dictionary<string, InstrumentKey> keys,
        CancellationToken ct)
    {
        var quotes = new Dictionary<InstrumentKey, Quote>(keys.Count);

        foreach (var chunk in Chunk(keys.Keys, _options.MaxQuoteInstruments))
        {
            ct.ThrowIfCancellationRequested();

            var response = await _api
                .GetAsync<Dictionary<string, KiteQuote>>(
                    _options.QuotePath,
                    new ZerodhaQuery().AddAll("i", chunk),
                    ct)
                .ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<Dictionary<InstrumentKey, Quote>>.Failure(response.Error);
            }

            foreach (var (native, quote) in response.Value)
            {
                // Kite omits instruments it has no data for rather than returning an error entry,
                // so a missing key is simply a quote that does not exist right now.
                if (keys.TryGetValue(native, out var key))
                {
                    quotes[key] = MapQuote(key, quote);
                }
            }
        }

        return quotes;
    }

    private Quote MapQuote(InstrumentKey instrument, KiteQuote quote)
    {
        // Kite's depth carries the touch. Reading bid and ask from it rather than leaving them
        // null is the difference between a spread the UI can show and one it cannot.
        var bestBid = quote.Depth?.Buy?.FirstOrDefault();
        var bestAsk = quote.Depth?.Sell?.FirstOrDefault();

        return new Quote
        {
            Instrument = instrument,
            LastPrice = new Money(quote.LastPrice ?? 0m, Currency.Inr),
            Open = Price(quote.Ohlc?.Open),
            High = Price(quote.Ohlc?.High),
            Low = Price(quote.Ohlc?.Low),

            // Kite's `ohlc.close` is the PREVIOUS day's close, not today's — today's has not
            // happened yet while the market is open. It is what the change and change-percent on
            // every Indian trading screen are computed against.
            PreviousClose = Price(quote.Ohlc?.Close),
            BidPrice = Price(bestBid?.Price),
            AskPrice = Price(bestAsk?.Price),
            BidQuantity = bestBid?.Quantity is { } bidQty ? new Quantity(bidQty) : null,
            AskQuantity = bestAsk?.Quantity is { } askQty ? new Quantity(askQty) : null,
            Volume = quote.Volume,
            OpenInterest = quote.OpenInterest,
            Timestamp = ZerodhaTime.ParseOr(quote.Timestamp ?? quote.LastTradeTime, _clock.UtcNow),
        };
    }

    private static List<DepthLevel> MapLevels(List<KiteDepthLevel>? levels)
    {
        if (levels is null or { Count: 0 })
        {
            return [];
        }

        var mapped = new List<DepthLevel>(levels.Count);
        foreach (var level in levels)
        {
            // Kite pads the book to five levels with zero-price rows when there is less depth than
            // that. A zero-priced level is not a level.
            if (level.Price is not > 0m)
            {
                continue;
            }

            mapped.Add(new DepthLevel(
                new Money(level.Price.Value, Currency.Inr),
                new Quantity(level.Quantity ?? 0m),
                level.Orders));
        }

        return mapped;
    }

    private static Money? Price(decimal? value) =>
        value is > 0m ? new Money(value.Value, Currency.Inr) : null;

    private static IEnumerable<List<string>> Chunk(IEnumerable<string> source, int size)
    {
        var batch = new List<string>(size);
        foreach (var item in source)
        {
            batch.Add(item);
            if (batch.Count == size)
            {
                yield return batch;
                batch = new List<string>(size);
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>One strike's two sides while the chain is being assembled.</summary>
    private readonly record struct ChainSlot(
        Quote? Call,
        Quote? Put,
        long? CallOpenInterest,
        long? PutOpenInterest);
}
