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

    /// <summary>mStock's documented ceiling on candles returned by one chart request.</summary>
    private const int MaxCandlesPerRequest = 1000;

    private readonly MStockApi _api;
    private readonly MStockOptions _options;
    private readonly ISymbolTranslator _symbols;
    private readonly IMStockInstrumentLookup? _instruments;
    private readonly Func<CancellationToken, Task<Result>>? _ensureInstruments;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _venueZone;

    /// <summary>Creates the market-data facet.</summary>
    /// <param name="api">The connector's HTTP client.</param>
    /// <param name="options">Endpoint and timeout configuration.</param>
    /// <param name="symbols">Canonical keys to mStock's quote keys.</param>
    /// <param name="clock">Stamps quotes.</param>
    /// <param name="instruments">The script master, for the numeric tokens the chart routes take.</param>
    /// <param name="ensureInstruments">Loads the script master if this process has not yet.
    /// Without it a cold process could never chart anything, because nothing else loads it.</param>
    internal MStockMarketData(
        MStockApi api,
        MStockOptions options,
        ISymbolTranslator symbols,
        IClock clock,
        IMStockInstrumentLookup? instruments = null,
        Func<CancellationToken, Task<Result>>? ensureInstruments = null)
    {
        _api = api;
        _options = options;
        _symbols = symbols;
        _clock = clock;
        _instruments = instruments;
        _ensureInstruments = ensureInstruments;
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

        var resolved = await ResolveTokenAsync(request.Instrument, ct).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Result<CandleSeries>.Failure(resolved.Error);
        }

        var exchange = MStockMaps.ToNativeExchange(request.Instrument.Venue, request.Instrument.AssetClass);
        if (exchange.IsFailure)
        {
            return Result<CandleSeries>.Failure(exchange.Error);
        }

        var token = resolved.Value.ToString(CultureInfo.InvariantCulture);
        var from = ClampToBarLimit(request.From, request.To, interval.Value.BarsPerSession);

        // mStock serves a window in two halves: everything before today from the historical
        // route, today from the intraday one. The historical route alone — all this used to
        // call — ends at the previous close, so a chart opened mid-session showed yesterday.
        var venueToday = TimeZoneInfo.ConvertTime(_clock.UtcNow, _venueZone).Date;
        var todayStart = new DateTimeOffset(venueToday, _venueZone.GetUtcOffset(venueToday));

        var candles = new List<Candle>();
        var lastCall = (DateTimeOffset?)null;

        if (from < todayStart)
        {
            var historyTo = request.To < todayStart ? request.To : todayStart.AddSeconds(-1);
            var historical = await FetchCandlesAsync(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        _options.HistoricalChartPathFormat,
                        exchange.Value,
                        token,
                        interval.Value.Interval),
                    // The window is the only query: no "interval" parameter, which the route
                    // does not take.
                    new MStockQuery()
                        .Add("from", MStockTime.FormatDateTime(from, _venueZone))
                        .Add("to", MStockTime.FormatDateTime(historyTo, _venueZone)),
                    ct)
                .ConfigureAwait(false);

            if (historical.IsFailure)
            {
                return Result<CandleSeries>.Failure(historical.Error);
            }

            candles.AddRange(historical.Value);
            lastCall = DateTimeOffset.UtcNow;
        }

        if (request.To >= todayStart)
        {
            var code = MStockMaps.ToIntradayExchangeCode(exchange.Value);
            if (code.IsFailure)
            {
                return Result<CandleSeries>.Failure(code.Error);
            }

            // One data request a second is mStock's documented limit. The historical call has
            // only just returned, so wait out the rest of its second rather than have this one
            // refused.
            if (lastCall is { } previous)
            {
                var wait = TimeSpan.FromSeconds(1.05) - (DateTimeOffset.UtcNow - previous);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
            }

            var intraday = await FetchCandlesAsync(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        _options.IntradayChartPathFormat,
                        code.Value.ToString(CultureInfo.InvariantCulture),
                        token,
                        interval.Value.Interval),
                    new MStockQuery(),
                    ct)
                .ConfigureAwait(false);

            if (intraday.IsFailure)
            {
                return Result<CandleSeries>.Failure(intraday.Error);
            }

            // The route answers with the whole session; keep the part the caller asked for.
            candles.AddRange(intraday.Value.Where(c => c.OpenTime >= from && c.OpenTime <= request.To));
        }

        // Oldest first, whatever order they arrived in: the historical route lists candles
        // oldest first, the intraday one newest first. A STABLE sort, so that among candles
        // sharing a time the intraday route's — added last, and fresher — stays last.
        var ordered = candles.OrderBy(static candle => candle.OpenTime).ToList();

        // One candle per time. mStock's historical route repeats some daily candles verbatim
        // (a year of TCS daily bars came back with five days listed twice, identical OHLCV),
        // and a chart library that requires strictly ascending time refuses the whole series
        // over one repeat. Last wins, for the ordering reason above.
        candles = new List<Candle>(ordered.Count);
        foreach (var candle in ordered)
        {
            if (candles.Count > 0 && candles[^1].OpenTime == candle.OpenTime)
            {
                candles[^1] = candle;
            }
            else
            {
                candles.Add(candle);
            }
        }

        return new CandleSeries
        {
            Instrument = request.Instrument,
            TimeFrame = request.TimeFrame,
            Currency = Inr,
            Candles = candles,
        };
    }

    /// <summary>One chart route's candles, mapped. Both routes answer with the same shape.</summary>
    private async Task<Result<List<Candle>>> FetchCandlesAsync(string path, MStockQuery query, CancellationToken ct)
    {
        var response = await _api.GetAsync<MStockCandlesData>(path, query, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result<List<Candle>>.Failure(response.Error);
        }

        var rows = response.Value.Candles ?? Array.Empty<IReadOnlyList<JsonElement>>();
        var candles = new List<Candle>(rows.Count);

        foreach (var row in rows)
        {
            var candle = MapCandle(row);
            if (candle.IsFailure)
            {
                return Result<List<Candle>>.Failure(candle.Error);
            }

            candles.Add(candle.Value);
        }

        return candles;
    }

    /// <summary>
    /// Moves <paramref name="from"/> forward, if it has to, so the window holds at most
    /// <see cref="MaxCandlesPerRequest"/> candles.
    ///
    /// mStock returns no more than 1000 candles per request, and a chart asking for five days of
    /// one-minute candles wants nearly 1900. Splitting the window into several requests is not an
    /// option: the data limit is one request a second, enforced per operation, so the second
    /// request would be refused. Keeping the MOST RECENT candles is the useful half of the
    /// window. Counting only weekdays means a Monday-morning chart still reaches back into last
    /// week; exchange holidays make the real count lower, never higher.
    /// </summary>
    private DateTimeOffset ClampToBarLimit(DateTimeOffset from, DateTimeOffset to, int barsPerSession)
    {
        var sessions = Math.Max(1, MaxCandlesPerRequest / Math.Max(1, barsPerSession));

        var day = TimeZoneInfo.ConvertTime(to, _venueZone).Date;
        var counted = 0;
        while (true)
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && ++counted == sessions)
            {
                break;
            }

            day = day.AddDays(-1);
        }

        var earliest = new DateTimeOffset(day, _venueZone.GetUtcOffset(day));
        return from > earliest ? from : earliest;
    }

    /// <summary>
    /// The numeric token mStock's chart routes are addressed by — they take nothing else.
    ///
    /// A miss on a cold process is not an answer: it means the script master has not been loaded
    /// yet, so it is loaded (once, for the whole process) and the lookup retried. Only a miss
    /// against a loaded master means mStock genuinely does not list the instrument.
    /// </summary>
    private async Task<Result<uint>> ResolveTokenAsync(InstrumentKey instrument, CancellationToken ct)
    {
        if (_instruments is not null && _instruments.TryGetToken(instrument, out var token))
        {
            return token;
        }

        if (_instruments is not null && _ensureInstruments is not null)
        {
            var loaded = await _ensureInstruments(ct).ConfigureAwait(false);
            if (loaded.IsFailure)
            {
                // The broker's own reason — an unregistered IP, an expired key — is the useful
                // part, and it is already worded for the user.
                return Result<uint>.Failure(loaded.Error);
            }

            if (_instruments.TryGetToken(instrument, out token))
            {
                return token;
            }
        }

        // "NSE", not the MIC "XNSE": this sentence is read by a trader, not a developer.
        var exchange = MStockMaps.ToNativeExchange(instrument.Venue, instrument.AssetClass);
        var where = exchange.IsSuccess ? $" on {exchange.Value}" : string.Empty;

        return Result<uint>.Failure(new Error(
            ConnectorErrorCodes.InstrumentNotFound,
            $"mStock does not list {instrument.Symbol}{where}, so it has no price history for it.",
            VendorCode: null,
            VendorMessage: null,
            Context: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instrument"] = instrument.ToString(),
            }));
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

    private static Quote MapQuote(InstrumentKey instrument, MStockQuoteDto dto, DateTimeOffset fallbackTime)
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

    private static Quote? MapChainLeg(
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
    private static Result<Candle> MapCandle(IReadOnlyList<JsonElement> row)
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
