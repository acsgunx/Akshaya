using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Tiger;

/// <summary>
/// Quotes, candles, depth and option chains from Tiger's quote methods.
///
/// Quotes come from two methods: stocks from the real-time quote, options from the option brief, which takes each
/// contract by underlying, expiry, right and strike. Both need the account's quote permissions for the market
/// asked about; a market without them answers Tiger's own permission error, surfaced as NotSupported.
/// </summary>
public sealed class TigerMarketData : IConnectorMarketData
{
    private readonly TigerChannel _channel;
    private readonly TigerOptions _options;
    private readonly TigerInstrumentCache _cache;
    private readonly TigerInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly IClock _clock;

    internal TigerMarketData(
        TigerChannel channel,
        TigerOptions options,
        TigerInstrumentCache cache,
        TigerInstrumentResolver resolver,
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

        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(account.Error);
        }

        var trading = _channel.Trading(account.Value.Credentials);
        var quoteApi = _channel.Quote(account.Value.Credentials);

        var records = new Dictionary<InstrumentKey, TigerInstrumentRecord>();
        foreach (var key in instruments)
        {
            var record = await RecordForAsync(trading, key, account.Value, ct).ConfigureAwait(false);
            if (record.IsFailure)
            {
                return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(record.Error);
            }

            records[key] = record.Value;
        }

        var now = _clock.UtcNow;
        var quotes = new Dictionary<InstrumentKey, Quote>();

        // --- stocks -------------------------------------------------------------------------------------------
        var stocks = records.Where(p => p.Key.AssetClass != AssetClass.Option).ToList();
        foreach (var chunk in stocks.Chunk(Math.Max(1, _options.MaxSymbolsPerRequest)))
        {
            var biz = TigerBiz.New()
                .Add("symbols", chunk.Select(p => p.Value.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                .Add("include_hour_trading", false)
                .Add("lang", _options.Language);

            var response = await quoteApi
                .CallAsync<TigerPage<TigerQuoteBrief>>(TigerMaps.MethodQuoteRealTime, biz, account.Value.Credentials, isTradeWrite: false, ct)
                .ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(response.Error);
            }

            var bySymbol = (response.Value.Items ?? [])
                .Where(b => !string.IsNullOrWhiteSpace(b.Symbol))
                .ToDictionary(b => b.Symbol!.Trim(), StringComparer.OrdinalIgnoreCase);

            foreach (var (key, record) in chunk)
            {
                if (bySymbol.TryGetValue(record.Symbol, out var brief) && ToQuote(key, brief, record.Definition.Currency, now) is { } quote)
                {
                    quotes[key] = quote;
                }
            }
        }

        // --- options ------------------------------------------------------------------------------------------
        var options = records.Where(p => p.Key.AssetClass == AssetClass.Option).ToList();
        if (options.Count > 0)
        {
            var basics = new List<object>();
            foreach (var (key, record) in options)
            {
                if (key is { Expiry: { } expiry, Strike: { } strike, Right: { } right })
                {
                    basics.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["symbol"] = record.Symbol,
                        ["expiry"] = ExpiryMilliseconds(expiry, record.Market),
                        ["right"] = right == OptionRight.Call ? "CALL" : "PUT",
                        ["strike"] = strike,
                    });
                }
            }

            var biz = TigerBiz.New()
                .Add("option_basics", basics)
                .Add("lang", _options.Language);

            var response = await quoteApi
                .CallAsync<List<TigerOptionQuote>>(TigerMaps.MethodOptionBrief, biz, account.Value.Credentials, isTradeWrite: false, ct)
                .ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(response.Error);
            }

            var byIdentifier = response.Value
                .Where(o => !string.IsNullOrWhiteSpace(o.Identifier))
                .ToDictionary(o => o.Identifier!.Trim(), StringComparer.OrdinalIgnoreCase);

            foreach (var (key, record) in options)
            {
                if (record.Identifier is { Length: > 0 } identifier
                    && byIdentifier.TryGetValue(identifier, out var brief)
                    && ToQuote(key, brief, record.Definition.Currency, now) is { } quote)
                {
                    quotes[key] = quote;
                }
            }
        }

        return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Success(quotes);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unadjusted bars — prices that actually traded, which is what a fill is compared against — walked through
    /// Tiger's page tokens.
    /// </remarks>
    public async Task<Result<CandleSeries>> GetHistoricalAsync(HistoryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<CandleSeries>.Failure(account.Error);
        }

        if (request.To <= request.From)
        {
            return Result<CandleSeries>.Failure(TigerErrors.InvalidRequest("The history window must end after it starts."));
        }

        var period = TigerMaps.ToNativePeriod(request.TimeFrame);
        if (period.IsFailure)
        {
            return Result<CandleSeries>.Failure(period.Error);
        }

        var trading = _channel.Trading(account.Value.Credentials);
        var record = await RecordForAsync(trading, request.Instrument, account.Value, ct).ConfigureAwait(false);
        if (record.IsFailure)
        {
            return Result<CandleSeries>.Failure(record.Error);
        }

        var quoteApi = _channel.Quote(account.Value.Credentials);
        var byTime = new SortedDictionary<long, TigerBar>();
        string? pageToken = null;

        for (var page = 0; page < _options.MaxHistoryPages; page++)
        {
            var biz = TigerBiz.New()
                .Add("symbols", new List<string> { record.Value.Symbol })
                .Add("period", period.Value)
                .Add("begin_time", request.From.ToUnixTimeMilliseconds())
                .Add("end_time", request.To.ToUnixTimeMilliseconds())
                .Add("right", TigerMaps.QuoteRightNone)
                .Add("limit", (long)_options.HistoryPageSize)
                .Add("page_token", pageToken)
                .Add("lang", _options.Language);

            var response = await quoteApi
                .CallAsync<List<TigerBarSeries>>(TigerMaps.MethodKline, biz, account.Value.Credentials, isTradeWrite: false, ct)
                .ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<CandleSeries>.Failure(response.Error);
            }

            var series = response.Value.FirstOrDefault(s =>
                string.Equals(s.Symbol?.Trim(), record.Value.Symbol, StringComparison.OrdinalIgnoreCase));

            var added = 0;
            foreach (var bar in series?.Items ?? [])
            {
                if (TigerNumber.Integer(bar.Time) is { } time and > 0 && byTime.TryAdd(time, bar))
                {
                    added++;
                }
            }

            pageToken = series?.NextPageToken;
            if (added == 0 || string.IsNullOrWhiteSpace(pageToken))
            {
                break;
            }
        }

        var zone = record.Value.Market.Zone;
        var byDate = request.TimeFrame is TimeFrame.OneDay or TimeFrame.OneWeek or TimeFrame.OneMonth;
        var fromDate = TigerTime.MarketDate(request.From, zone);
        var toDate = TigerTime.MarketDate(request.To, zone);

        var candles = new List<Candle>(byTime.Count);
        foreach (var (time, bar) in byTime)
        {
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(time);
            var inWindow = byDate
                ? TigerTime.MarketDate(openTime, zone) is var date && date >= fromDate && date <= toDate
                : openTime >= request.From && openTime <= request.To;

            if (!inWindow || TigerNumber.Decimal(bar.Close) is not { } close)
            {
                continue;
            }

            candles.Add(new Candle
            {
                OpenTime = openTime,
                Open = TigerNumber.Decimal(bar.Open) ?? close,
                High = TigerNumber.Decimal(bar.High) ?? close,
                Low = TigerNumber.Decimal(bar.Low) ?? close,
                Close = close,
                Volume = TigerNumber.Integer(bar.Volume) ?? 0L,
            });
        }

        return new CandleSeries
        {
            Instrument = request.Instrument,
            TimeFrame = request.TimeFrame,
            Currency = record.Value.Definition.Currency,
            Candles = candles,
        };
    }

    /// <inheritdoc />
    public async Task<Result<MarketDepth>> GetDepthAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<MarketDepth>.Failure(account.Error);
        }

        var trading = _channel.Trading(account.Value.Credentials);
        var record = await RecordForAsync(trading, instrument, account.Value, ct).ConfigureAwait(false);
        if (record.IsFailure)
        {
            return Result<MarketDepth>.Failure(record.Error);
        }

        var biz = TigerBiz.New()
            .Add("symbols", new List<string> { record.Value.Symbol })
            .Add("market", record.Value.Market.Code)
            .Add("lang", _options.Language);

        var response = await _channel.Quote(account.Value.Credentials)
            .CallAsync<List<TigerDepthBook>>(TigerMaps.MethodQuoteDepth, biz, account.Value.Credentials, isTradeWrite: false, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<MarketDepth>.Failure(response.Error);
        }

        var book = response.Value.FirstOrDefault(b =>
            string.Equals(b.Symbol?.Trim(), record.Value.Symbol, StringComparison.OrdinalIgnoreCase));

        if (book is null)
        {
            return Result<MarketDepth>.Failure(ConnectorErrors.InstrumentNotFound(instrument));
        }

        var currency = record.Value.Definition.Currency;

        return new MarketDepth
        {
            Instrument = instrument,
            Bids = Levels(book.Bids, currency),
            Asks = Levels(book.Asks, currency),
            Timestamp = _clock.UtcNow,
        };
    }

    /// <inheritdoc />
    /// <remarks>One request per expiry: Tiger's chain answers every strike's call and put together.</remarks>
    public async Task<Result<OptionChain>> GetOptionChainAsync(
        InstrumentKey underlying,
        DateOnly expiry,
        CancellationToken ct = default)
    {
        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<OptionChain>.Failure(account.Error);
        }

        var trading = _channel.Trading(account.Value.Credentials);
        var owner = await RecordForAsync(trading, underlying, account.Value, ct).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return Result<OptionChain>.Failure(owner.Error);
        }

        if (owner.Value.Definition.Key.AssetClass == AssetClass.Option)
        {
            return Result<OptionChain>.Failure(TigerErrors.InvalidRequest("An option chain is asked for by its underlying."));
        }

        var contracts = new List<object>
        {
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["symbol"] = owner.Value.Symbol,
                ["expiry"] = ExpiryMilliseconds(expiry, owner.Value.Market),
            },
        };

        var biz = TigerBiz.New()
            .Add("contracts", contracts)
            .Add("market", owner.Value.Market.Code)
            .Add("lang", _options.Language);

        var response = await _channel.Quote(account.Value.Credentials)
            .CallAsync<List<TigerOptionChainGroup>>(
                TigerMaps.MethodOptionChain,
                biz,
                account.Value.Credentials,
                isTradeWrite: false,
                ct,
                _options.OptionChainVersion)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<OptionChain>.Failure(response.Error);
        }

        var now = _clock.UtcNow;
        var currency = owner.Value.Definition.Currency;
        var rows = new SortedDictionary<decimal, OptionChainRow>();

        foreach (var group in response.Value)
        {
            foreach (var pair in group.Items ?? [])
            {
                foreach (var (_, contract) in pair)
                {
                    if (TigerNumber.Decimal(contract.Strike) is not { } strike || strike <= 0m)
                    {
                        continue;
                    }

                    var right = string.Equals(contract.Right?.Trim(), "PUT", StringComparison.OrdinalIgnoreCase)
                        ? OptionRight.Put
                        : OptionRight.Call;

                    var key = new InstrumentKey(
                        owner.Value.Definition.Key.Venue,
                        owner.Value.Definition.Key.Symbol,
                        AssetClass.Option,
                        expiry,
                        strike,
                        right);

                    var quote = ToQuote(key, contract, currency, now);
                    var openInterest = TigerNumber.Integer(contract.OpenInterest ?? contract.OpenInt);
                    var row = rows.GetValueOrDefault(strike) ?? new OptionChainRow { Strike = strike };

                    rows[strike] = right == OptionRight.Call
                        ? row with { Call = quote, CallOpenInterest = openInterest }
                        : row with { Put = quote, PutOpenInterest = openInterest };
                }
            }
        }

        var spot = await GetQuoteAsync(underlying, ct).ConfigureAwait(false);

        return new OptionChain
        {
            Underlying = underlying,
            Expiry = expiry,
            Rows = [.. rows.Values],
            UnderlyingPrice = spot.IsSuccess ? spot.Value.LastPrice : null,
        };
    }

    // --- plumbing ----------------------------------------------------------------------------------------------

    private async Task<Result<TigerInstrumentRecord>> RecordForAsync(
        TigerApi api,
        InstrumentKey key,
        TigerAccount account,
        CancellationToken ct)
    {
        var record = await _resolver.ForKeyAsync(api, key, account.Credentials, ct).ConfigureAwait(false);
        if (record.IsFailure)
        {
            return record;
        }

        return TigerInstrumentResolver.Matches(key, record.Value.Definition.Key)
            ? record
            : Result<TigerInstrumentRecord>.Failure(ConnectorErrors.InstrumentNotFound(key));
    }

    private static Quote? ToQuote(InstrumentKey key, TigerQuoteBrief brief, Currency currency, DateTimeOffset now)
    {
        if (TigerNumber.Decimal(brief.LatestPrice) is not { } last || last <= 0m)
        {
            return null;
        }

        return new Quote
        {
            Instrument = key,
            LastPrice = new Money(last, currency),
            Open = Positive(brief.Open, currency),
            High = Positive(brief.High, currency),
            Low = Positive(brief.Low, currency),
            PreviousClose = Positive(brief.PreviousClose, currency),
            BidPrice = Positive(brief.BidPrice, currency),
            AskPrice = Positive(brief.AskPrice, currency),
            BidQuantity = TigerNumber.Decimal(brief.BidSize) is { } bid && bid > 0m ? new Quantity(bid) : null,
            AskQuantity = TigerNumber.Decimal(brief.AskSize) is { } ask && ask > 0m ? new Quantity(ask) : null,
            Volume = TigerNumber.Integer(brief.Volume),
            Timestamp = TigerTime.FromUnixMilliseconds(brief.LatestTime ?? brief.Timestamp) ?? now,
        };
    }

    private static Quote? ToQuote(InstrumentKey key, TigerOptionQuote contract, Currency currency, DateTimeOffset now)
    {
        if (TigerNumber.Decimal(contract.LatestPrice) is not { } last || last <= 0m)
        {
            return null;
        }

        return new Quote
        {
            Instrument = key,
            LastPrice = new Money(last, currency),
            Open = Positive(contract.Open, currency),
            High = Positive(contract.High, currency),
            Low = Positive(contract.Low, currency),
            PreviousClose = Positive(contract.PreviousClose, currency),
            BidPrice = Positive(contract.BidPrice, currency),
            AskPrice = Positive(contract.AskPrice, currency),
            Volume = TigerNumber.Integer(contract.Volume),
            OpenInterest = TigerNumber.Integer(contract.OpenInterest ?? contract.OpenInt),
            Timestamp = TigerTime.FromUnixMilliseconds(contract.LatestTime ?? contract.Timestamp) ?? now,
        };
    }

    private static List<DepthLevel> Levels(List<TigerDepthLevel>? levels, Currency currency)
    {
        var mapped = new List<DepthLevel>(levels?.Count ?? 0);

        foreach (var level in levels ?? [])
        {
            // A zero-priced level is padding, not liquidity.
            if (TigerNumber.Decimal(level.Price) is { } price && price > 0m)
            {
                int? orders = TigerNumber.Integer(level.Count) is { } count && count is > 0 and <= int.MaxValue ? (int)count : null;
                mapped.Add(new DepthLevel(new Money(price, currency), new Quantity(TigerNumber.DecimalOrZero(level.Volume)), orders));
            }
        }

        return mapped;
    }

    /// <summary>An expiry as the epoch milliseconds Tiger's option methods take: the market's own midnight.</summary>
    private static long ExpiryMilliseconds(DateOnly expiry, TigerMarket market)
    {
        var midnight = expiry.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, market.Zone.GetUtcOffset(midnight)).ToUnixTimeMilliseconds();
    }

    private static Money? Positive(string? value, Currency currency) =>
        TigerNumber.Decimal(value) is { } amount && amount > 0m ? new Money(amount, currency) : null;
}
