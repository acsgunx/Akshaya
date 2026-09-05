using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Fyers;

/// <summary>
/// Quotes, depth, candles and the option chain.
///
/// FYERS' data surface is genuinely good — one quote call covers fifty symbols, history reaches
/// back to July 2017, and the option chain carries greeks — but it has hard per-request limits
/// that are silent when exceeded rather than loud. Fifty-one symbols do not fail; the fifty-first
/// simply is not in the answer. Every batching decision in this class exists to keep that from
/// turning into a stale price on a watchlist nobody notices.
/// </summary>
public sealed class FyersMarketData : IConnectorMarketData
{
    private readonly FyersApi _api;
    private readonly FyersOptions _options;
    private readonly ISymbolTranslator _symbols;
    private readonly IClock _clock;

    internal FyersMarketData(
        FyersApi api,
        FyersOptions options,
        ISymbolTranslator symbols,
        IClock clock)
    {
        _api = api;
        _options = options;
        _symbols = symbols;
        _clock = clock;
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
        var quotes = await GetQuotesAsync(instruments, ct).ConfigureAwait(false);
        if (quotes.IsFailure)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Money>>.Failure(quotes.Error);
        }

        var prices = new Dictionary<InstrumentKey, Money>(quotes.Value.Count);
        foreach (var (key, quote) in quotes.Value)
        {
            prices[key] = quote.LastPrice;
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

        // Translate everything first, so an untradable instrument fails before any network call
        // rather than coming back as a missing entry the caller has to reason about.
        var bySymbol = new Dictionary<string, InstrumentKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in instruments)
        {
            var native = _symbols.ToNative(instrument);
            if (native.IsFailure)
            {
                return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(native.Error);
            }

            bySymbol[native.Value] = instrument;
        }

        foreach (var chunk in Chunk(bySymbol.Keys, _options.MaxQuoteSymbols))
        {
            ct.ThrowIfCancellationRequested();

            var response = await _api
                .GetAsync<FyersQuotesResponse>(
                    _options.QuotesPath,
                    new FyersQuery().Add("symbols", string.Join(',', chunk)),
                    ct)
                .ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(response.Error);
            }

            foreach (var entry in response.Value.Quotes ?? [])
            {
                // Each entry carries its own status: one delisted symbol in a batch of fifty
                // fails only itself, and dropping it is the correct answer for a bulk read.
                if (!string.Equals(entry.Status, FyersJson.StatusOk, StringComparison.OrdinalIgnoreCase)
                    || entry.Values is not { } values
                    || entry.Name is not { Length: > 0 } name
                    || !bySymbol.TryGetValue(name, out var key))
                {
                    continue;
                }

                quotes[key] = MapQuote(key, values);
            }
        }

        return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Success(quotes);
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
            .GetAsync<FyersDepthResponse>(
                _options.DepthPath,
                new FyersQuery()
                    .Add("symbol", native.Value)
                    // Without this the OHLC and volume fields come back zero rather than absent,
                    // which reads as a market that opened at nothing.
                    .Add("ohlcv_flag", 1),
                ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<MarketDepth>.Failure(response.Error);
        }

        if (response.Value.Depth is not { } book
            || !book.TryGetValue(native.Value, out var depth))
        {
            return Result<MarketDepth>.Failure(ConnectorErrors.InstrumentNotFound(instrument));
        }

        return new MarketDepth
        {
            Instrument = instrument,
            Bids = MapLevels(depth.Bids),
            Asks = MapLevels(depth.Asks),
            Timestamp = depth.LastTradedTime is { } ltt
                ? FyersTime.FromEpoch(ltt) ?? _clock.UtcNow
                : _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// FYERS serves at most 100 days per request for minute resolutions and 366 for day, week and
    /// month. A longer range is REFUSED rather than truncated: a chart that silently starts three
    /// months late looks like a market that did not trade, and an indicator computed over it is
    /// wrong without being visibly wrong. The platform's own candle store answers longer ranges.
    /// </remarks>
    public async Task<Result<CandleSeries>> GetHistoricalAsync(
        HistoryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.To < request.From)
        {
            return Result<CandleSeries>.Failure(
                FyersErrors.InvalidRequest("The history range ends before it begins."));
        }

        var native = _symbols.ToNative(request.Instrument);
        if (native.IsFailure)
        {
            return Result<CandleSeries>.Failure(native.Error);
        }

        var resolution = FyersMaps.ToNativeResolution(request.TimeFrame);
        if (resolution.IsFailure)
        {
            return Result<CandleSeries>.Failure(resolution.Error);
        }

        var maxDays = resolution.Value.Intraday
            ? _options.MaxIntradayHistoryDays
            : _options.MaxDailyHistoryDays;

        var requestedDays = (request.To - request.From).TotalDays;
        if (requestedDays > maxDays)
        {
            return Result<CandleSeries>.Failure(FyersErrors.InvalidRequest(
                $"FYERS serves at most {maxDays.ToString(CultureInfo.InvariantCulture)} days of "
                + $"{request.TimeFrame} history per request; {Math.Ceiling(requestedDays).ToString("0", CultureInfo.InvariantCulture)} "
                + "were asked for. Narrow the range, or read the longer history from the platform's own store."));
        }

        var query = new FyersQuery()
            .Add("symbol", native.Value)
            .Add("resolution", resolution.Value.Resolution)

            // Epoch rather than yyyy-MM-dd. The date form has no time of day, so an intraday
            // range would silently widen to whole days at both ends.
            .Add("date_format", 0)
            .Add("range_from", FyersTime.EpochSeconds(request.From))
            .Add("range_to", FyersTime.EpochSeconds(request.To))
            .Add("cont_flag", 1);

        var response = await _api
            .GetAsync<FyersHistoryResponse>(_options.HistoryPath, query, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<CandleSeries>.Failure(response.Error);
        }

        var rows = response.Value.Candles ?? [];
        var candles = new List<Candle>(rows.Count);

        foreach (var row in rows)
        {
            // POSITIONAL, and the order is the contract: epoch, open, high, low, close, volume.
            // A short row is dropped rather than padded — a candle with a zero low prints as a
            // spike to nothing on every chart and every indicator that reads it.
            if (row.Length < 6)
            {
                continue;
            }

            if (FyersTime.FromEpoch((long)row[0]) is not { } openTime)
            {
                continue;
            }

            candles.Add(new Candle
            {
                OpenTime = openTime,
                Open = row[1],
                High = row[2],
                Low = row[3],
                Close = row[4],
                Volume = (long)row[5],
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
    /// Two calls, deliberately. FYERS returns the chain for its own choice of expiry unless it is
    /// given a timestamp, and that timestamp is only obtainable from the <c>expiryData</c> list
    /// the first call returns. Asking for a specific expiry and being handed a different one
    /// would be the worst possible outcome here — every strike would look plausible and every
    /// greek would belong to the wrong contract — so the expiry is always stated explicitly.
    /// </remarks>
    public async Task<Result<OptionChain>> GetOptionChainAsync(
        InstrumentKey underlying,
        DateOnly expiry,
        CancellationToken ct = default)
    {
        var native = _symbols.ToNative(underlying);
        if (native.IsFailure)
        {
            return Result<OptionChain>.Failure(native.Error);
        }

        var expiries = await _api
            .GetAsync<FyersOptionChainResponse>(
                _options.OptionChainPath,
                new FyersQuery()
                    .Add("symbol", native.Value)
                    // One strike is enough to be handed the expiry list, which is all this call
                    // is for. Asking for fifty would move several hundred rows we discard.
                    .Add("strikecount", 1),
                ct)
            .ConfigureAwait(false);

        if (expiries.IsFailure)
        {
            return Result<OptionChain>.Failure(expiries.Error);
        }

        var timestamp = FindExpiryTimestamp(expiries.Value.Data?.ExpiryData, expiry);
        if (timestamp is null)
        {
            return Result<OptionChain>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                $"FYERS lists no {expiry:yyyy-MM-dd} expiry for {underlying.Symbol}.",
                VendorCode: null,
                VendorMessage: null,
                Context: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["underlying"] = native.Value,
                    ["expiry"] = expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                }));
        }

        var chain = await _api
            .GetAsync<FyersOptionChainResponse>(
                _options.OptionChainPath,
                new FyersQuery()
                    .Add("symbol", native.Value)
                    .Add("strikecount", _options.OptionChainStrikeCount)
                    .Add("timestamp", timestamp),
                ct)
            .ConfigureAwait(false);

        if (chain.IsFailure)
        {
            return Result<OptionChain>.Failure(chain.Error);
        }

        return BuildChain(underlying, expiry, chain.Value.Data);
    }

    // --- mapping ---------------------------------------------------------------------------

    private Quote MapQuote(InstrumentKey instrument, FyersQuoteValues values) => new()
    {
        Instrument = instrument,
        LastPrice = new Money(values.LastPrice ?? 0m, Currency.Inr),
        Open = Price(values.Open),
        High = Price(values.High),
        Low = Price(values.Low),
        PreviousClose = Price(values.PreviousClose),
        BidPrice = Price(values.Bid),
        AskPrice = Price(values.Ask),

        // FYERS' quote payload has no bid or ask SIZE; only the depth route carries those. A
        // zero would read as "no size at the touch", which is a different and tradeable claim.
        BidQuantity = null,
        AskQuantity = null,
        Volume = values.Volume,
        Timestamp = FyersTime.ParseOr(values.Timestamp, _clock.UtcNow),
    };

    private static List<DepthLevel> MapLevels(List<FyersDepthLevel>? levels)
    {
        if (levels is null or { Count: 0 })
        {
            return [];
        }

        var mapped = new List<DepthLevel>(levels.Count);
        foreach (var level in levels)
        {
            // FYERS pads the book to five levels with zero-price rows when there is less depth
            // than that. A zero-priced level is not a level.
            if (level.Price is not > 0m)
            {
                continue;
            }

            mapped.Add(new DepthLevel(
                new Money(level.Price.Value, Currency.Inr),
                new Quantity(level.Volume ?? 0m),
                level.Orders));
        }

        return mapped;
    }

    private static string? FindExpiryTimestamp(List<FyersOptionExpiry>? expiries, DateOnly wanted)
    {
        foreach (var candidate in expiries ?? [])
        {
            if (string.IsNullOrWhiteSpace(candidate.Expiry))
            {
                continue;
            }

            // Match on the EPOCH converted to an IST date rather than on the printed date string.
            // The printed one is dd-MM-yyyy, which is indistinguishable from mm-dd-yyyy for the
            // first twelve days of any month — and being one contract out on an option chain is
            // not a rounding error.
            if (!long.TryParse(candidate.Expiry, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
                || FyersTime.FromEpoch(epoch) is not { } instant)
            {
                continue;
            }

            if (FyersTime.VenueDate(instant, FyersTime.India) == wanted)
            {
                return candidate.Expiry;
            }
        }

        return null;
    }

    private Result<OptionChain> BuildChain(InstrumentKey underlying, DateOnly expiry, FyersOptionChainData? data)
    {
        if (data?.OptionsChain is not { Count: > 0 } rows)
        {
            return Result<OptionChain>.Failure(
                FyersErrors.MissingField(_options.OptionChainPath, "optionsChain"));
        }

        Money? underlyingPrice = null;
        var byStrike = new SortedDictionary<decimal, (Quote? Call, Quote? Put, long? CallOi, long? PutOi)>();

        foreach (var row in rows)
        {
            // The first row is the UNDERLYING, marked by an empty option type and a strike of -1.
            // Reading it as a contract would put a phantom strike of minus one in the chain.
            if (string.IsNullOrWhiteSpace(row.OptionType) || row.StrikePrice is not { } strike || strike < 0m)
            {
                underlyingPrice ??= row.LastPrice is > 0m
                    ? new Money(row.LastPrice.Value, Currency.Inr)
                    : null;

                continue;
            }

            var right = FyersMaps.ToCanonicalOptionRight(row.OptionType);
            if (right.IsFailure)
            {
                return Result<OptionChain>.Failure(right.Error);
            }

            // The contract's key is built from what we already know — underlying, expiry, strike,
            // right — rather than by translating the row's symbol. Both are correct, but this one
            // cannot fail: a monthly contract's symbol carries no expiry DAY, so translating it
            // would need the symbol master, and an option chain is exactly the screen a user
            // opens before any master has been ingested.
            var contract = new InstrumentKey(
                underlying.Venue,
                underlying.Symbol,
                AssetClass.Option,
                expiry,
                strike,
                right.Value);

            var quote = new Quote
            {
                Instrument = contract,
                LastPrice = new Money(row.LastPrice ?? 0m, Currency.Inr),
                BidPrice = Price(row.Bid),
                AskPrice = Price(row.Ask),
                Volume = row.Volume,
                OpenInterest = row.OpenInterest,
                Timestamp = _clock.UtcNow,
            };

            byStrike.TryGetValue(strike, out var existing);

            byStrike[strike] = right.Value == OptionRight.Call
                ? (quote, existing.Put, row.OpenInterest, existing.PutOi)
                : (existing.Call, quote, existing.CallOi, row.OpenInterest);
        }

        var chainRows = new List<OptionChainRow>(byStrike.Count);
        foreach (var (strike, slot) in byStrike)
        {
            chainRows.Add(new OptionChainRow
            {
                Strike = strike,
                Call = slot.Call,
                Put = slot.Put,
                CallOpenInterest = slot.CallOi,
                PutOpenInterest = slot.PutOi,
            });
        }

        return new OptionChain
        {
            Underlying = underlying,
            Expiry = expiry,
            Rows = chainRows,
            UnderlyingPrice = underlyingPrice,
        };
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
}
