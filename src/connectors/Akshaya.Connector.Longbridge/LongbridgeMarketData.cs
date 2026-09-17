using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Longbridge;

/// <summary>Where a key goes on the quote gateway: its symbol, market and price currency. Structural: no network.</summary>
internal sealed record LongbridgeTarget(string Native, LongbridgeMarket Market, Currency Currency, bool IsOption)
{
    public static Result<LongbridgeTarget> For(InstrumentKey key, LongbridgeInstrumentCache cache)
    {
        if (key.AssetClass == AssetClass.Option)
        {
            var option = LongbridgeNative.ForOptionKey(key);
            if (option.IsFailure)
            {
                return Result<LongbridgeTarget>.Failure(option.Error);
            }

            var optionCurrency = cache.TryGetByNative(option.Value, out var contract)
                ? contract.Definition.Currency
                : LongbridgeMarket.Us.Currency;

            return new LongbridgeTarget(option.Value, LongbridgeMarket.Us, optionCurrency, IsOption: true);
        }

        var cash = LongbridgeNative.ForCashKey(key);
        if (cash.IsFailure)
        {
            return Result<LongbridgeTarget>.Failure(cash.Error);
        }

        var currency = cache.TryGetByKey(key, out var record) ? record.Definition.Currency : cash.Value.Market.Currency;
        return new LongbridgeTarget(cash.Value.Native, cash.Value.Market, currency, IsOption: false);
    }
}

/// <summary>
/// Quotes, candles, depth and option chains from Longbridge's quote gateway.
///
/// Everything here is a protobuf request on the quote socket (see <see cref="LongbridgeChannel"/>), with prices
/// as decimal strings end to end. Quotes need no subscription: one pull request prices up to five hundred
/// symbols. Longbridge's pull quote has no best bid or ask — those live in the depth — so a Quote's bid and ask
/// are left empty rather than filled by a second request per symbol.
///
/// OpenAPI quote permissions are separate from the app's. A request for a market the account holds no OpenAPI
/// quote card for fails with Longbridge's permission error, surfaced as NotSupported.
/// </summary>
public sealed class LongbridgeMarketData : IConnectorMarketData
{
    private readonly LongbridgeChannel _channel;
    private readonly LongbridgeOptions _options;
    private readonly LongbridgeInstrumentCache _cache;
    private readonly LongbridgeInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly IClock _clock;

    internal LongbridgeMarketData(
        LongbridgeChannel channel,
        LongbridgeOptions options,
        LongbridgeInstrumentCache cache,
        LongbridgeInstrumentResolver resolver,
        Func<Result<BrokerSession>> requireSession,
        IClock clock)
    {
        _channel = channel;
        _options = options;
        _cache = cache;
        _resolver = resolver;
        _requireSession = requireSession;
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
        return quotes.IsFailure
            ? Result<IReadOnlyDictionary<InstrumentKey, Money>>.Failure(quotes.Error)
            : Result<IReadOnlyDictionary<InstrumentKey, Money>>.Success(
                quotes.Value.ToDictionary(pair => pair.Key, pair => pair.Value.LastPrice));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<InstrumentKey, Quote>>> GetQuotesAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(session.Error);
        }

        // Several keys can name one Longbridge symbol (ARCX:SPY and XNYS:SPY both reach SPY.US), so the request is
        // keyed by symbol and each answer fanned back out to every key that asked.
        var requested = new Dictionary<string, (LongbridgeTarget Target, List<InstrumentKey> Keys)>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in instruments)
        {
            var target = LongbridgeTarget.For(key, _cache);
            if (target.IsFailure)
            {
                return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(target.Error);
            }

            if (!requested.TryGetValue(target.Value.Native, out var entry))
            {
                entry = (target.Value, []);
                requested[target.Value.Native] = entry;
            }

            entry.Keys.Add(key);
        }

        var cash = await QuotesAsync(
            LongbridgeCommand.QuerySecurityQuote,
            [.. requested.Values.Where(r => !r.Target.IsOption).Select(r => r.Target.Native)],
            ct).ConfigureAwait(false);

        if (cash.IsFailure)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(cash.Error);
        }

        var contracts = await QuotesAsync(
            LongbridgeCommand.QueryOptionQuote,
            [.. requested.Values.Where(r => r.Target.IsOption).Select(r => r.Target.Native)],
            ct).ConfigureAwait(false);

        if (contracts.IsFailure)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(contracts.Error);
        }

        var now = _clock.UtcNow;
        var quotes = new Dictionary<InstrumentKey, Quote>();

        foreach (var (native, (target, keys)) in requested)
        {
            var source = target.IsOption ? contracts.Value : cash.Value;
            if (!source.TryGetValue(native, out var quote))
            {
                continue;
            }

            foreach (var key in keys)
            {
                quotes[key] = ToQuote(key, quote, target.Currency, now);
            }
        }

        return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Success(quotes);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unadjusted, regular-session candles, like every other connector here: prices that actually traded, which
    /// is what a fill is compared against. A date query answers up to the gateway's page cap; when its last candle
    /// falls short of the window's end, the rest is walked forward from that candle until the window is covered,
    /// a page adds nothing new, or the page limit is reached. Candles carry the timestamp Longbridge sends, read as
    /// the bar's open.
    /// </remarks>
    public async Task<Result<CandleSeries>> GetHistoricalAsync(HistoryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<CandleSeries>.Failure(session.Error);
        }

        if (request.To <= request.From)
        {
            return Result<CandleSeries>.Failure(LongbridgeErrors.InvalidRequest("The history window must end after it starts."));
        }

        var period = LongbridgeMaps.ToNativePeriod(request.TimeFrame);
        if (period.IsFailure)
        {
            return Result<CandleSeries>.Failure(period.Error);
        }

        var target = LongbridgeTarget.For(request.Instrument, _cache);
        if (target.IsFailure)
        {
            return Result<CandleSeries>.Failure(target.Error);
        }

        var native = target.Value.Native;
        var zone = target.Value.Market.Zone;
        var byTime = new SortedDictionary<long, LbCandle>();

        var first = await CandlesAsync(
            LbHistoryRequest.ByDate(
                native,
                period.Value,
                LongbridgeTime.Compact(LongbridgeTime.MarketDate(request.From, zone)),
                LongbridgeTime.Compact(LongbridgeTime.MarketDate(request.To, zone)),
                LongbridgeMaps.TradeSessionIntraday),
            ct).ConfigureAwait(false);

        if (first.IsFailure)
        {
            return Result<CandleSeries>.Failure(first.Error);
        }

        foreach (var candle in first.Value)
        {
            if (candle.Timestamp > 0)
            {
                byTime[candle.Timestamp] = candle;
            }
        }

        for (var page = 1; page < _options.MaxHistoryPages && byTime.Count > 0; page++)
        {
            var last = byTime.Keys.Last();
            if (LongbridgeTime.FromUnixSeconds(last) is not { } lastTime || lastTime >= request.To)
            {
                break;
            }

            var local = TimeZoneInfo.ConvertTime(lastTime, zone);
            var next = await CandlesAsync(
                LbHistoryRequest.ForwardFrom(
                    native,
                    period.Value,
                    local.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    local.ToString("HHmm", CultureInfo.InvariantCulture),
                    _options.HistoryPageSize,
                    LongbridgeMaps.TradeSessionIntraday),
                ct).ConfigureAwait(false);

            if (next.IsFailure)
            {
                return Result<CandleSeries>.Failure(next.Error);
            }

            var added = 0;
            foreach (var candle in next.Value)
            {
                if (candle.Timestamp > last && byTime.TryAdd(candle.Timestamp, candle))
                {
                    added++;
                }
            }

            if (added == 0)
            {
                break;
            }
        }

        // Day, week and month bars are stamped at the start of their period, before a window that opens at the
        // session start; they are kept by market date rather than by instant.
        var byDate = request.TimeFrame is TimeFrame.OneDay or TimeFrame.OneWeek or TimeFrame.OneMonth;
        var fromDate = LongbridgeTime.MarketDate(request.From, zone);
        var toDate = LongbridgeTime.MarketDate(request.To, zone);

        var candles = new List<Candle>(byTime.Count);
        foreach (var candle in byTime.Values)
        {
            if (LongbridgeTime.FromUnixSeconds(candle.Timestamp) is not { } openTime)
            {
                continue;
            }

            var inWindow = byDate
                ? LongbridgeTime.MarketDate(openTime, zone) is var date && date >= fromDate && date <= toDate
                : openTime >= request.From && openTime <= request.To;

            if (!inWindow)
            {
                continue;
            }

            candles.Add(new Candle
            {
                OpenTime = openTime,
                Open = LongbridgeNumber.DecimalOrZero(candle.Open),
                High = LongbridgeNumber.DecimalOrZero(candle.High),
                Low = LongbridgeNumber.DecimalOrZero(candle.Low),
                Close = LongbridgeNumber.DecimalOrZero(candle.Close),
                Volume = candle.Volume,
            });
        }

        return new CandleSeries
        {
            Instrument = request.Instrument,
            TimeFrame = request.TimeFrame,
            Currency = target.Value.Currency,
            Candles = candles,
        };
    }

    /// <inheritdoc />
    public async Task<Result<MarketDepth>> GetDepthAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<MarketDepth>.Failure(session.Error);
        }

        var target = LongbridgeTarget.For(instrument, _cache);
        if (target.IsFailure)
        {
            return Result<MarketDepth>.Failure(target.Error);
        }

        var response = await _channel
            .RequestAsync(LongbridgeCommand.QueryDepth, LbSecurityRequest.One(target.Value.Native), ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<MarketDepth>.Failure(response.Error);
        }

        try
        {
            return ToDepth(instrument, LbDepth.DecodeResponse(response.Value), target.Value.Currency, _clock.UtcNow);
        }
        catch (InvalidDataException ex)
        {
            return Result<MarketDepth>.Failure(LongbridgeErrorMapper.MapException(ex, "QueryDepth"));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// US underlyings only. Assembled from two requests: the chain names every strike's call and put for the
    /// expiry, and option quotes price them with open interest and the contract multiplier, which is recorded on
    /// each contract's definition. A contract whose symbol is not a standard one (an adjusted root such as
    /// <c>AAPL1</c>) is left out rather than decoded into the wrong key.
    /// </remarks>
    public async Task<Result<OptionChain>> GetOptionChainAsync(
        InstrumentKey underlying,
        DateOnly expiry,
        CancellationToken ct = default)
    {
        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<OptionChain>.Failure(session.Error);
        }

        var owner = await _resolver.ForKeyAsync(_channel, underlying, ct).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return Result<OptionChain>.Failure(owner.Error);
        }

        if (owner.Value.Definition.Key != underlying)
        {
            return Result<OptionChain>.Failure(ConnectorErrors.InstrumentNotFound(underlying));
        }

        if (owner.Value.Market.Suffix != LongbridgeMarket.Us.Suffix)
        {
            return Result<OptionChain>.Failure(ConnectorErrors.NotSupported(
                "option chains on non-US underlyings through this connector"));
        }

        var strikes = await _channel
            .RequestAsync(
                LongbridgeCommand.QueryOptionChainDateStrikeInfo,
                LbOptionChainRequest.Encode(owner.Value.Native, LongbridgeTime.Compact(expiry)),
                ct)
            .ConfigureAwait(false);

        if (strikes.IsFailure)
        {
            return Result<OptionChain>.Failure(strikes.Error);
        }

        List<LbStrike> rows;
        try
        {
            rows = LbStrike.DecodeResponse(strikes.Value);
        }
        catch (InvalidDataException ex)
        {
            return Result<OptionChain>.Failure(LongbridgeErrorMapper.MapException(ex, "QueryOptionChainDateStrikeInfo"));
        }

        var symbols = rows
            .SelectMany(r => new[] { r.CallSymbol, r.PutSymbol })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var quotes = await QuotesAsync(LongbridgeCommand.QueryOptionQuote, symbols, ct).ConfigureAwait(false);
        if (quotes.IsFailure)
        {
            return Result<OptionChain>.Failure(quotes.Error);
        }

        // The underlying's price is a convenience on the chain; a refusal for it must not cost the chain.
        var spot = await QuotesAsync(LongbridgeCommand.QuerySecurityQuote, [owner.Value.Native], ct).ConfigureAwait(false);

        var now = _clock.UtcNow;
        var chain = new SortedDictionary<decimal, OptionChainRow>();

        foreach (var symbol in symbols)
        {
            if (!LongbridgeNative.TryParseOption(symbol, out _, out var contractExpiry, out var right, out var strike)
                || contractExpiry != expiry)
            {
                continue;
            }

            quotes.Value.TryGetValue(symbol, out var quote);

            var record = LongbridgeInstrumentFactory.Option(
                symbol,
                owner.Value.Definition.Key,
                owner.Value.Definition.Currency,
                contractExpiry,
                right,
                strike,
                LongbridgeNumber.Decimal(quote?.ContractMultiplier));

            _cache.Add(record);

            var priced = quote is null ? null : ToQuote(record.Definition.Key, quote, record.Definition.Currency, now);
            var row = chain.GetValueOrDefault(strike) ?? new OptionChainRow { Strike = strike };

            chain[strike] = right == OptionRight.Call
                ? row with { Call = priced, CallOpenInterest = quote?.OpenInterest }
                : row with { Put = priced, PutOpenInterest = quote?.OpenInterest };
        }

        return new OptionChain
        {
            Underlying = underlying,
            Expiry = expiry,
            Rows = [.. chain.Values],
            UnderlyingPrice = spot.IsSuccess
                              && spot.Value.TryGetValue(owner.Value.Native, out var spotQuote)
                              && LongbridgeNumber.Decimal(spotQuote.LastDone) is { } price
                              && price > 0m
                ? new Money(price, owner.Value.Definition.Currency)
                : null,
        };
    }

    // --- shared with the stream ------------------------------------------------------------------------------

    internal static Quote ToQuote(InstrumentKey key, LbQuote quote, Currency currency, DateTimeOffset now) => new()
    {
        Instrument = key,
        LastPrice = new Money(LongbridgeNumber.DecimalOrZero(quote.LastDone), currency),
        Open = Positive(quote.Open, currency),
        High = Positive(quote.High, currency),
        Low = Positive(quote.Low, currency),
        PreviousClose = Positive(quote.PrevClose, currency),
        Volume = quote.Volume,
        OpenInterest = quote.OpenInterest,
        Timestamp = LongbridgeTime.FromUnixSeconds(quote.Timestamp) ?? now,
    };

    internal static MarketDepth ToDepth(InstrumentKey key, LbDepth depth, Currency currency, DateTimeOffset now) => new()
    {
        Instrument = key,
        Bids = Levels(depth.Bids, currency),
        Asks = Levels(depth.Asks, currency),
        Timestamp = now,
    };

    internal static Money? Positive(string? value, Currency currency) =>
        LongbridgeNumber.Decimal(value) is { } amount && amount > 0m ? new Money(amount, currency) : null;

    // --- plumbing ----------------------------------------------------------------------------------------------

    private static List<DepthLevel> Levels(List<LbDepthLevel> levels, Currency currency)
    {
        var mapped = new List<DepthLevel>(levels.Count);

        foreach (var level in levels.OrderBy(l => l.Position))
        {
            // A zero-priced level is padding, not liquidity.
            if (LongbridgeNumber.Decimal(level.Price) is { } price && price > 0m)
            {
                int? orders = level.OrderCount > 0 ? (int)Math.Min(level.OrderCount, int.MaxValue) : null;
                mapped.Add(new DepthLevel(new Money(price, currency), new Quantity(level.Volume), orders));
            }
        }

        return mapped;
    }

    /// <summary>Quotes keyed by symbol, chunked at the gateway's per-request symbol limit.</summary>
    private async Task<Result<Dictionary<string, LbQuote>>> QuotesAsync(byte command, IReadOnlyList<string> natives, CancellationToken ct)
    {
        var result = new Dictionary<string, LbQuote>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in natives.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(Math.Max(1, _options.MaxSymbolsPerRequest)))
        {
            var response = await _channel.RequestAsync(command, LbSecurityRequest.Many(chunk), ct).ConfigureAwait(false);
            if (response.IsFailure)
            {
                return Result<Dictionary<string, LbQuote>>.Failure(response.Error);
            }

            try
            {
                foreach (var quote in LbQuote.DecodeResponse(response.Value))
                {
                    if (!string.IsNullOrWhiteSpace(quote.Symbol))
                    {
                        result[quote.Symbol.Trim().ToUpperInvariant()] = quote;
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                return Result<Dictionary<string, LbQuote>>.Failure(LongbridgeErrorMapper.MapException(ex, LongbridgeCommand.Name(command)));
            }
        }

        return result;
    }

    private async Task<Result<List<LbCandle>>> CandlesAsync(byte[] body, CancellationToken ct)
    {
        var response = await _channel.RequestAsync(LongbridgeCommand.QueryHistoryCandlestick, body, ct).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result<List<LbCandle>>.Failure(response.Error);
        }

        try
        {
            return LbCandle.DecodeResponse(response.Value);
        }
        catch (InvalidDataException ex)
        {
            return Result<List<LbCandle>>.Failure(LongbridgeErrorMapper.MapException(ex, "QueryHistoryCandlestick"));
        }
    }
}
