using System.Globalization;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.MStock;

/// <summary>
/// Quotes, candles and option chains.
///
/// mStock's quote routes are keyed by <c>EXCHANGE:TRADINGSYMBOL</c> and take the key
/// repeatedly (<c>?i=NSE:INFY&amp;i=NSE:TCS</c>). Two consequences drive the code below:
///
/// * A key we send that mStock does not recognise comes back simply MISSING from the response
///   map, with no error. So every response is reconciled against what was asked for; a quote
///   that was requested and did not arrive is reported as InstrumentNotFound rather than
///   silently dropped, because a watchlist that quietly loses a row is worse than one that
///   shows an error on it.
/// * The rate limits here are brutal — one request per second on the data bucket — so batch
///   routes are always preferred to loops. <see cref="GetQuotesAsync"/> is one call, not N.
/// </summary>
public sealed class MStockMarketData : IConnectorMarketData
{
    private static readonly Currency Inr = Currency.Inr;

    private readonly MStockApi _api;
    private readonly MStockOptions _options;
    private readonly ISymbolTranslator _symbols;
    private readonly IMStockInstrumentLookup? _instruments;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _venueZone;

    /// <summary>Creates the market-data facet.</summary>
    public MStockMarketData(
        MStockApi api,
        MStockOptions options,
        ISymbolTranslator symbols,
        IClock clock,
        IMStockInstrumentLookup? instruments = null)
    {
        _api = api;
        _options = options;
        _symbols = symbols;
        _clock = clock;
        _instruments = instruments;
        _venueZone = MStockTime.ResolveZone(options.VenueTimeZoneId);
    }

    /// <inheritdoc />
    public async Task<Result<Quote>> GetQuoteAsync(
        InstrumentKey instrument,
        CancellationToken ct = default)
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
        var keys = BuildKeys(instruments);
        if (keys.IsFailure)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Money>>.Failure(keys.Error);
        }

        if (keys.Value.Count == 0)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Money>>.Success(
                new Dictionary<InstrumentKey, Money>());
        }

        var response = await _api.GetAsync<IReadOnlyDictionary<string, MStockQuoteDto>>(
                _options.LtpPath,
                new MStockQuery().AddAll("i", keys.Value.Keys),
                ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Money>>.Failure(response.Error);
        }

        var result = new Dictionary<InstrumentKey, Money>(keys.Value.Count);
        foreach (var (quoteKey, instrument) in keys.Value)
        {
            if (!response.Value.TryGetValue(quoteKey, out var dto) || dto.LastPrice is null)
            {
                return Result<IReadOnlyDictionary<InstrumentKey, Money>>.Failure(
                    NotQuoted(instrument, quoteKey, _options.LtpPath));
            }

            result[instrument] = new Money(dto.LastPrice.Value, Inr);
        }

        return Result<IReadOnlyDictionary<InstrumentKey, Money>>.Success(result);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<InstrumentKey, Quote>>> GetQuotesAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default)
    {
        var keys = BuildKeys(instruments);
        if (keys.IsFailure)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(keys.Error);
        }

        if (keys.Value.Count == 0)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Success(
                new Dictionary<InstrumentKey, Quote>());
        }

        // The OHLC route returns last price AND the day's open/high/low/close in one call, so
        // it is what backs GetQuotesAsync. Calling the LTP route and then the OHLC route would
        // spend two of the sixty data requests a minute allows.
        var response = await _api.GetAsync<IReadOnlyDictionary<string, MStockQuoteDto>>(
                _options.OhlcPath,
                new MStockQuery().AddAll("i", keys.Value.Keys),
                ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(response.Error);
        }

        var now = _clock.UtcNow;
        var result = new Dictionary<InstrumentKey, Quote>(keys.Value.Count);

        foreach (var (quoteKey, instrument) in keys.Value)
        {
            if (!response.Value.TryGetValue(quoteKey, out var dto) || dto.LastPrice is null)
            {
                return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(
                    NotQuoted(instrument, quoteKey, _options.OhlcPath));
            }

            result[instrument] = MapQuote(instrument, dto, now);
        }

        return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Success(result);
    }

    /// <inheritdoc />
    public async Task<Result<CandleSeries>> GetHistoricalAsync(
        HistoryRequest request,
        CancellationToken ct = default)
    {
        var interval = MStockMaps.ToNativeInterval(request.TimeFrame);
        if (interval.IsFailure)
        {
            return Result<CandleSeries>.Failure(interval.Error);
        }

        if (request.To < request.From)
        {
            return Result<CandleSeries>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "The history window ends before it begins."));
        }

        // The chart routes are addressed by numeric instrument token, not by trading symbol,
        // so the script master has to be loaded. Saying so beats a 404 with no explanation.
        if (_instruments is null || !_instruments.TryGetToken(request.Instrument, out var token))
        {
            return Result<CandleSeries>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                $"mStock's chart routes are addressed by instrument token and none is known for "
                + $"{request.Instrument}. Load the script master "
                + "(IConnectorReference.GetInstrumentsAsync) before requesting history."));
        }

        var path = interval.Value.Intraday
            ? string.Format(
                CultureInfo.InvariantCulture,
                _options.IntradayChartPathFormat,
                token.ToString(CultureInfo.InvariantCulture),
                interval.Value.Interval)
            : string.Format(
                CultureInfo.InvariantCulture,
                _options.HistoricalChartPathFormat,
                token.ToString(CultureInfo.InvariantCulture));

        var query = new MStockQuery()
            .Add("from", MStockTime.FormatDateTime(request.From, _venueZone))
            .Add("to", MStockTime.FormatDateTime(request.To, _venueZone))
            .Add("interval", interval.Value.Interval);

        var response = await _api.GetAsync<MStockCandlesData>(path, query, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result<CandleSeries>.Failure(response.Error);
        }

        var rows = response.Value.Candles ?? Array.Empty<IReadOnlyList<JsonElement>>();
        var candles = new List<Candle>(rows.Count);

        foreach (var row in rows)
        {
            var candle = MapCandle(row);
            if (candle.IsFailure)
            {
                return Result<CandleSeries>.Failure(candle.Error);
            }

            candles.Add(candle.Value);
        }

        return new CandleSeries
        {
            Instrument = request.Instrument,
            TimeFrame = request.TimeFrame,
            Currency = Inr,
            Candles = candles,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// mStock's Type A REST surface exposes LTP and OHLC but no full-depth quote route; the
    /// five-level book is only available on the websocket in <see cref="StreamMode.Full"/>.
    /// The manifest says so (<c>depthLevels: 5</c> alongside <c>streaming: true</c>), and this
    /// returns NotSupported rather than fabricating a one-level book from the best bid and
    /// ask — a depth ladder that is secretly a top-of-book is how a sizing algorithm ends up
    /// convinced there is liquidity that is not there.
    /// </remarks>
    public Task<Result<MarketDepth>> GetDepthAsync(
        InstrumentKey instrument,
        CancellationToken ct = default) =>
        Task.FromResult(Result<MarketDepth>.Failure(
            ConnectorErrors.NotSupported("market depth over REST; subscribe to the stream in Full mode")));

    /// <inheritdoc />
    public async Task<Result<OptionChain>> GetOptionChainAsync(
        InstrumentKey underlying,
        DateOnly expiry,
        CancellationToken ct = default)
    {
        var exchange = MStockMaps.ToNativeExchange(underlying.Venue, AssetClass.Option);
        if (exchange.IsFailure)
        {
            return Result<OptionChain>.Failure(exchange.Error);
        }

        var query = new MStockQuery()
            .Add("exchange", exchange.Value)
            .Add("symbol", underlying.Symbol.ToUpperInvariant())
            .Add("expiry", expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var response = await _api
            .GetAsync<MStockOptionChainData>(_options.OptionChainPath, query, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<OptionChain>.Failure(response.Error);
        }

        var now = _clock.UtcNow;
        var rows = new List<OptionChainRow>();

        foreach (var dto in response.Value.AllRows)
        {
            if (dto.Strike is not { } strike)
            {
                continue;
            }

            rows.Add(new OptionChainRow
            {
                Strike = strike,
                Call = MapChainLeg(underlying, expiry, strike, OptionRight.Call, dto.Call, now),
                Put = MapChainLeg(underlying, expiry, strike, OptionRight.Put, dto.Put, now),
                CallOpenInterest = dto.Call?.Oi,
                PutOpenInterest = dto.Put?.Oi,
            });
        }

        // Strike order is the only order an option chain is readable in, and mStock does not
        // guarantee one.
        rows.Sort(static (a, b) => a.Strike.CompareTo(b.Strike));

        return new OptionChain
        {
            Underlying = underlying,
            Expiry = expiry,
            Rows = rows,
            UnderlyingPrice = response.Value.UnderlyingValue is { } value
                ? new Money(value, Inr)
                : null,
        };
    }

    // --- mapping ----------------------------------------------------------------------------

    /// <summary>
    /// Builds the <c>EXCHANGE:TRADINGSYMBOL</c> keys for a batch, preserving the mapping back
    /// to the canonical instrument so the response can be reassembled.
    /// </summary>
    private Result<Dictionary<string, InstrumentKey>> BuildKeys(
        IReadOnlyCollection<InstrumentKey> instruments)
    {
        var keys = new Dictionary<string, InstrumentKey>(instruments.Count, StringComparer.Ordinal);

        foreach (var instrument in instruments)
        {
            var symbol = _symbols.ToNative(instrument);
            if (symbol.IsFailure)
            {
                return Result<Dictionary<string, InstrumentKey>>.Failure(symbol.Error);
            }

            var exchange = MStockMaps.ToNativeExchange(instrument.Venue, instrument.AssetClass);
            if (exchange.IsFailure)
            {
                return Result<Dictionary<string, InstrumentKey>>.Failure(exchange.Error);
            }

            keys[MStockQuoteKey.Build(exchange.Value, symbol.Value)] = instrument;
        }

        return keys;
    }

    private Quote MapQuote(InstrumentKey instrument, MStockQuoteDto dto, DateTimeOffset fallbackTime)
    {
        var ohlc = dto.Ohlc;
        var depth = dto.Depth;

        return new Quote
        {
            Instrument = instrument,
            LastPrice = new Money(dto.LastPrice ?? 0m, Inr),
            Open = Rupees(ohlc?.Open),
            High = Rupees(ohlc?.High),
            Low = Rupees(ohlc?.Low),

            // mStock's "close" on the OHLC route is the PREVIOUS session's close, not today's
            // last traded price. Mapping it to PreviousClose is what makes the change and
            // change-percent columns correct; treating it as today's close would make every
            // intraday move read as zero.
            PreviousClose = Rupees(ohlc?.Close),
            BidPrice = Rupees(depth?.Buy is { Count: > 0 } bids ? bids[0].Price : null),
            AskPrice = Rupees(depth?.Sell is { Count: > 0 } asks ? asks[0].Price : null),
            BidQuantity = depth?.Buy is { Count: > 0 } bidQty && bidQty[0].Quantity is { } bq
                ? (Quantity?)new Quantity(bq)
                : null,
            AskQuantity = depth?.Sell is { Count: > 0 } askQty && askQty[0].Quantity is { } aq
                ? (Quantity?)new Quantity(aq)
                : null,
            Volume = dto.Volume,
            OpenInterest = dto.OpenInterest,
            Timestamp = MStockTime.ParseOr(dto.Timestamp, fallbackTime),
        };
    }

    private Quote? MapChainLeg(
        InstrumentKey underlying,
        DateOnly expiry,
        decimal strike,
        OptionRight right,
        MStockOptionChainLegDto? leg,
        DateTimeOffset now)
    {
        if (leg?.Ltp is not { } last)
        {
            return null;
        }

        return new Quote
        {
            Instrument = new InstrumentKey(
                underlying.Venue,
                underlying.Symbol,
                AssetClass.Option,
                expiry,
                strike,
                right),
            LastPrice = new Money(last, Inr),
            BidPrice = Rupees(leg.BidPrice),
            AskPrice = Rupees(leg.AskPrice),
            Volume = leg.Volume,
            OpenInterest = leg.Oi,
            Timestamp = now,
        };
    }

    /// <summary>
    /// mStock sends candles as positional arrays, not objects:
    /// <c>[timestamp, open, high, low, close, volume, openInterest?]</c>. Reading them by
    /// index is unavoidable; validating the length before doing so is not.
    /// </summary>
    private Result<Candle> MapCandle(IReadOnlyList<JsonElement> row)
    {
        const int MinimumFields = 6;

        if (row.Count < MinimumFields)
        {
            return Result<Candle>.Failure(new Error(
                ConnectorErrorCodes.Unknown,
                $"mStock returned a candle with {row.Count} fields; at least {MinimumFields} "
                + "(timestamp, open, high, low, close, volume) are required."));
        }

        var timestamp = MStockTime.Parse(row[0].ValueKind == JsonValueKind.String ? row[0].GetString() : null);
        if (timestamp is null)
        {
            return Result<Candle>.Failure(new Error(
                ConnectorErrorCodes.Unknown,
                "mStock returned a candle whose timestamp could not be read."));
        }

        return new Candle
        {
            OpenTime = timestamp.Value,
            Open = ReadDecimal(row[1]),
            High = ReadDecimal(row[2]),
            Low = ReadDecimal(row[3]),
            Close = ReadDecimal(row[4]),
            Volume = ReadLong(row[5]),
            OpenInterest = row.Count > 6 ? ReadLong(row[6]) : null,
        };
    }

    private static decimal ReadDecimal(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDecimal(),
        JsonValueKind.String when decimal.TryParse(
            element.GetString(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed) => parsed,
        _ => 0m,
    };

    private static long ReadLong(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetInt64(out var value) ? value : (long)element.GetDouble(),
        JsonValueKind.String when long.TryParse(
            element.GetString(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed) => parsed,
        _ => 0L,
    };

    private static Money? Rupees(decimal? amount) => amount is { } value ? new Money(value, Inr) : null;

    private static Error NotQuoted(InstrumentKey instrument, string quoteKey, string route) => new(
        ConnectorErrorCodes.InstrumentNotFound,
        $"mStock returned no quote for {instrument}. It was requested as '{quoteKey}'; the broker "
        + "omits unknown keys from the response rather than reporting them, so this usually means "
        + "the trading symbol or exchange segment is wrong for this instrument.",
        VendorCode: quoteKey,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["route"] = route,
            ["quoteKey"] = quoteKey,
            ["instrument"] = instrument.ToString(),
        });
}
