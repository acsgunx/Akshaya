using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Moomoo;

/// <summary>
/// Quotes, candles, depth and option chains from OpenD.
///
/// Quotes come from the SNAPSHOT protocol rather than the basic-quote one. Snapshots need no
/// subscription, carry the best bid and ask that the basic quote lacks, and take four hundred securities
/// at once — so a watchlist refresh costs one request, not a subscription per name that OpenD would hold
/// for a minute. Depth is the exception: OpenD serves an order book only for a subscribed security, so a
/// depth read subscribes first, and that subscription holds a unit of the account's quota for the
/// connection's lifetime.
/// </summary>
public sealed class MoomooMarketData : IConnectorMarketData
{
    private readonly MoomooChannel _channel;
    private readonly MoomooOptions _options;
    private readonly MoomooInstrumentCache _cache;
    private readonly MoomooInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly IClock _clock;

    internal MoomooMarketData(
        MoomooChannel channel,
        MoomooOptions options,
        MoomooInstrumentCache cache,
        MoomooInstrumentResolver resolver,
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

        // Several canonical keys can name one OpenD security (ARCX:SPY and XNYS:SPY both reach US.SPY), so
        // the request is keyed by native symbol and each answer fanned back out to every key that asked.
        var requested = new Dictionary<string, (MoomooMarket Market, List<InstrumentKey> Keys)>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in instruments)
        {
            var target = await TargetAsync(key, ct).ConfigureAwait(false);
            if (target.IsFailure)
            {
                return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(target.Error);
            }

            var native = MoomooNative.Qualify(target.Value.Market, target.Value.Code);
            if (!requested.TryGetValue(native, out var entry))
            {
                entry = (target.Value.Market, []);
                requested[native] = entry;
            }

            entry.Keys.Add(key);
        }

        var snapshots = await SnapshotAsync(requested.Keys, ct).ConfigureAwait(false);
        if (snapshots.IsFailure)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(snapshots.Error);
        }

        var now = _clock.UtcNow;
        var quotes = new Dictionary<InstrumentKey, Quote>();

        foreach (var (native, (market, keys)) in requested)
        {
            if (!snapshots.Value.TryGetValue(native, out var snapshot) || snapshot.Basic is not { } basic)
            {
                continue;
            }

            foreach (var key in keys)
            {
                quotes[key] = ToQuote(key, basic, snapshot.OptionExData, market, now);
            }
        }

        return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Success(quotes);
    }

    /// <inheritdoc />
    public async Task<Result<CandleSeries>> GetHistoricalAsync(HistoryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<CandleSeries>.Failure(session.Error);
        }

        var klType = MoomooMaps.ToNativeKlType(request.TimeFrame);
        if (klType.IsFailure)
        {
            return Result<CandleSeries>.Failure(klType.Error);
        }

        var target = await TargetAsync(request.Instrument, ct).ConfigureAwait(false);
        if (target.IsFailure)
        {
            return Result<CandleSeries>.Failure(target.Error);
        }

        var (market, code) = target.Value;
        var security = new OpenDSecurity { Market = market.QotMarket, Code = code };

        // OpenD reads the window as a naive time in the market's zone.
        var begin = MoomooTime.FormatLocal(request.From, market.Zone);
        var end = MoomooTime.FormatLocal(request.To, market.Zone);

        var candles = new List<Candle>();
        string? nextKey = null;

        for (var page = 0; page < _options.MaxHistoryPages; page++)
        {
            var response = await _channel.RequestAsync<OpenDRequestHistoryKLC2S, OpenDRequestHistoryKLS2C>(
                MoomooProtoId.QotRequestHistoryKL,
                new OpenDRequestHistoryKLC2S
                {
                    // Unadjusted, like every other connector here: a chart of prices that actually traded,
                    // which is what a fill will be compared against.
                    RehabType = MoomooMaps.RehabNone,
                    KlType = klType.Value,
                    Security = security,
                    BeginTime = begin,
                    EndTime = end,
                    MaxAckKlNum = _options.HistoryPageSize,
                    NextReqKey = nextKey,
                },
                ct).ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<CandleSeries>.Failure(response.Error);
            }

            foreach (var line in response.Value.KlList ?? [])
            {
                if (line.IsBlank == true || MoomooTime.Best(line.Timestamp, line.Time, market.Zone) is not { } openTime)
                {
                    continue;
                }

                candles.Add(new Candle
                {
                    OpenTime = openTime,
                    Open = MoomooNumber.DecimalOrZero(line.OpenPrice),
                    High = MoomooNumber.DecimalOrZero(line.HighPrice),
                    Low = MoomooNumber.DecimalOrZero(line.LowPrice),
                    Close = MoomooNumber.DecimalOrZero(line.ClosePrice),
                    Volume = line.Volume ?? 0L,
                });
            }

            nextKey = response.Value.NextReqKey;
            if (string.IsNullOrEmpty(nextKey))
            {
                break;
            }
        }

        return new CandleSeries
        {
            Instrument = request.Instrument,
            TimeFrame = request.TimeFrame,
            Currency = market.Currency,
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

        var target = await TargetAsync(instrument, ct).ConfigureAwait(false);
        if (target.IsFailure)
        {
            return Result<MarketDepth>.Failure(target.Error);
        }

        var (market, code) = target.Value;
        var security = new OpenDSecurity { Market = market.QotMarket, Code = code };

        var subscribed = await _channel.RequestAsync<OpenDSubC2S, OpenDEmpty>(
            MoomooProtoId.QotSub,
            new OpenDSubC2S
            {
                SecurityList = [security],
                SubTypeList = [MoomooMaps.SubTypeOrderBook],
                IsSubOrUnSub = true,
            },
            ct).ConfigureAwait(false);

        if (subscribed.IsFailure)
        {
            return Result<MarketDepth>.Failure(subscribed.Error);
        }

        var book = await _channel.RequestAsync<OpenDGetOrderBookC2S, OpenDOrderBookS2C>(
            MoomooProtoId.QotGetOrderBook,
            new OpenDGetOrderBookC2S { Security = security, Num = _options.OrderBookLevels },
            ct).ConfigureAwait(false);

        return book.IsFailure
            ? Result<MarketDepth>.Failure(book.Error)
            : ToDepth(instrument, book.Value, market, _clock.UtcNow);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Assembled from two protocols: the option chain names every contract on the underlying for the
    /// expiry, and one snapshot request prices up to four hundred of them with open interest included.
    /// OpenD's chain does not serve expired expiries, so a past date yields an empty chain.
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

        var cash = MoomooNative.ForCashKey(underlying);
        if (cash.IsFailure)
        {
            return Result<OptionChain>.Failure(cash.Error);
        }

        var (market, code) = cash.Value;

        var loaded = await _resolver.EnsureOptionChainAsync(_channel, market, code, expiry, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<OptionChain>.Failure(loaded.Error);
        }

        var contracts = _cache.OptionsFor(market, code, expiry);
        var underlyingNative = MoomooNative.Qualify(market, code);

        var snapshots = await SnapshotAsync([underlyingNative, .. contracts.Select(c => c.Native)], ct).ConfigureAwait(false);
        if (snapshots.IsFailure)
        {
            return Result<OptionChain>.Failure(snapshots.Error);
        }

        var now = _clock.UtcNow;

        Quote? QuoteFor(MoomooInstrumentRecord? contract) =>
            contract is not null && snapshots.Value.TryGetValue(contract.Native, out var snapshot) && snapshot.Basic is { } basic
                ? ToQuote(contract.Definition.Key, basic, snapshot.OptionExData, market, now)
                : null;

        long? OpenInterestFor(MoomooInstrumentRecord? contract) =>
            contract is not null && snapshots.Value.TryGetValue(contract.Native, out var snapshot)
                ? snapshot.OptionExData?.OpenInterest
                : null;

        var rows = contracts
            .GroupBy(c => c.Definition.Key.Strike ?? 0m)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var call = g.FirstOrDefault(c => c.Definition.Key.Right == OptionRight.Call);
                var put = g.FirstOrDefault(c => c.Definition.Key.Right == OptionRight.Put);

                return new OptionChainRow
                {
                    Strike = g.Key,
                    Call = QuoteFor(call),
                    Put = QuoteFor(put),
                    CallOpenInterest = OpenInterestFor(call),
                    PutOpenInterest = OpenInterestFor(put),
                };
            })
            .ToList();

        return new OptionChain
        {
            Underlying = underlying,
            Expiry = expiry,
            Rows = rows,
            UnderlyingPrice = snapshots.Value.TryGetValue(underlyingNative, out var owner)
                              && MoomooNumber.Decimal(owner.Basic?.CurPrice) is { } price
                ? new Money(price, market.Currency)
                : null,
        };
    }

    // --- shared with the stream -------------------------------------------------------------------------

    internal static Quote ToQuote(
        InstrumentKey key,
        OpenDSnapshotBasicData basic,
        OpenDOptionSnapshotExData? option,
        MoomooMarket market,
        DateTimeOffset now)
    {
        var currency = market.Currency;

        return new Quote
        {
            Instrument = key,
            LastPrice = new Money(MoomooNumber.DecimalOrZero(basic.CurPrice), currency),
            Open = MoneyOrNull(basic.OpenPrice, currency),
            High = MoneyOrNull(basic.HighPrice, currency),
            Low = MoneyOrNull(basic.LowPrice, currency),
            PreviousClose = MoneyOrNull(basic.LastClosePrice, currency),

            // OpenD reports an empty side of the book as zero. A zero bid is not a bid.
            BidPrice = PositiveOrNull(basic.BidPrice, currency),
            AskPrice = PositiveOrNull(basic.AskPrice, currency),
            BidQuantity = basic.BidVol is > 0 ? new Quantity(basic.BidVol.Value) : null,
            AskQuantity = basic.AskVol is > 0 ? new Quantity(basic.AskVol.Value) : null,
            Volume = basic.Volume,
            OpenInterest = option?.OpenInterest,
            Timestamp = MoomooTime.Best(basic.UpdateTimestamp, basic.UpdateTime, market.Zone) ?? now,
        };
    }

    internal static MarketDepth ToDepth(InstrumentKey key, OpenDOrderBookS2C book, MoomooMarket market, DateTimeOffset now)
    {
        static List<DepthLevel> Levels(List<OpenDOrderBookLevel>? levels, Currency currency)
        {
            var mapped = new List<DepthLevel>(levels?.Count ?? 0);
            foreach (var level in levels ?? [])
            {
                // A zero-priced level is padding, not liquidity.
                if (MoomooNumber.Decimal(level.Price) is { } price && price > 0m)
                {
                    mapped.Add(new DepthLevel(new Money(price, currency), new Quantity(level.Volume ?? 0L), level.OrderCount));
                }
            }

            return mapped;
        }

        var received = new[] { MoomooTime.FromUnixSeconds(book.ServerReceivedBidTimestamp), MoomooTime.FromUnixSeconds(book.ServerReceivedAskTimestamp) }
            .Where(t => t is not null)
            .Max();

        return new MarketDepth
        {
            Instrument = key,
            Bids = Levels(book.OrderBookBidList, market.Currency),
            Asks = Levels(book.OrderBookAskList, market.Currency),
            Timestamp = received ?? now,
        };
    }

    // --- plumbing -----------------------------------------------------------------------------------------

    /// <summary>Market and code for a key: structural for cash, through the option chain for contracts.</summary>
    private async Task<Result<(MoomooMarket Market, string Code)>> TargetAsync(InstrumentKey key, CancellationToken ct)
    {
        if (key.AssetClass != AssetClass.Option)
        {
            return MoomooNative.ForCashKey(key);
        }

        var record = await _resolver.ForKeyAsync(_channel, key, ct).ConfigureAwait(false);
        return record.IsFailure
            ? Result<(MoomooMarket, string)>.Failure(record.Error)
            : Result<(MoomooMarket, string)>.Success((record.Value.Market, record.Value.Code));
    }

    /// <summary>Snapshots keyed by native symbol, chunked at OpenD's four-hundred-security limit.</summary>
    private async Task<Result<Dictionary<string, OpenDSnapshot>>> SnapshotAsync(IEnumerable<string> natives, CancellationToken ct)
    {
        var securities = new List<OpenDSecurity>();
        foreach (var native in natives.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (MoomooNative.TrySplit(native, out var market, out var code))
            {
                securities.Add(new OpenDSecurity { Market = market.QotMarket, Code = code });
            }
        }

        var result = new Dictionary<string, OpenDSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in securities.Chunk(Math.Max(1, _options.MaxSnapshotSecurities)))
        {
            var response = await _channel.RequestAsync<OpenDGetSecuritySnapshotC2S, OpenDGetSecuritySnapshotS2C>(
                MoomooProtoId.QotGetSecuritySnapshot,
                new OpenDGetSecuritySnapshotC2S { SecurityList = [.. chunk] },
                ct).ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<Dictionary<string, OpenDSnapshot>>.Failure(response.Error);
            }

            foreach (var snapshot in response.Value.SnapshotList ?? [])
            {
                if (snapshot.Basic?.Security is { } security
                    && MoomooMaps.MarketForQotMarket(security.Market) is { IsSuccess: true } market)
                {
                    result[MoomooNative.Qualify(market.Value, security.Code.Trim().ToUpperInvariant())] = snapshot;
                }
            }
        }

        return Result<Dictionary<string, OpenDSnapshot>>.Success(result);
    }

    private static Money? MoneyOrNull(double? value, Currency currency) =>
        MoomooNumber.Decimal(value) is { } amount ? new Money(amount, currency) : null;

    private static Money? PositiveOrNull(double? value, Currency currency) =>
        MoomooNumber.Decimal(value) is { } amount && amount > 0m ? new Money(amount, currency) : null;
}
