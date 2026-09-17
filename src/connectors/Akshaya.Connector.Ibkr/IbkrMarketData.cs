using System.Globalization;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Ibkr;

/// <summary>
/// Quotes, candles and option chains from the gateway.
///
/// SNAPSHOTS NEED A PRE-FLIGHT. The first snapshot request for a conid only tells IBKR to start streaming it and
/// answers without prices. Rows that come back empty are asked for again, a moment later, a bounded number of times;
/// a conid still empty after that is left out of the answer rather than reported with a zero price.
///
/// Bid and ask sizes are not reported: IBKR scales them for US stocks in a way its documentation does not pin down,
/// and a size off by a factor of a hundred is worse than none.
/// </summary>
public sealed class IbkrMarketData : IConnectorMarketData
{
    private readonly IbkrChannel _channel;
    private readonly IbkrOptions _options;
    private readonly IbkrInstrumentCache _cache;
    private readonly IbkrInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly IClock _clock;

    internal IbkrMarketData(
        IbkrChannel channel,
        IbkrOptions options,
        IbkrInstrumentCache cache,
        IbkrInstrumentResolver resolver,
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
            : Result<Quote>.Failure(new Error(
                ConnectorErrorCodes.BrokerUnavailable,
                $"IBKR returned no price for {instrument}. The username may have no market data subscription for it, or its stream is still starting."));
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

        var records = new Dictionary<InstrumentKey, IbkrInstrumentRecord>();
        foreach (var key in instruments)
        {
            var record = await RecordForAsync(key, ct).ConfigureAwait(false);
            if (record.IsFailure)
            {
                return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(record.Error);
            }

            records[key] = record.Value;
        }

        var rows = await SnapshotAsync([.. records.Values.Select(r => r.Conid)], ct).ConfigureAwait(false);
        if (rows.IsFailure)
        {
            return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Failure(rows.Error);
        }

        var now = _clock.UtcNow;
        var quotes = new Dictionary<InstrumentKey, Quote>();

        foreach (var (key, record) in records)
        {
            if (rows.Value.TryGetValue(record.Conid, out var row)
                && ToQuote(key, record.Definition.Currency, field => row[field], now) is { } quote)
            {
                quotes[key] = quote;
            }
        }

        return Result<IReadOnlyDictionary<InstrumentKey, Quote>>.Success(quotes);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Regular-session trade bars, walked forward from the window's start one page at a time: IBKR answers a period
    /// divided into bars, and a page is kept to what one answer holds. Five history requests may run at once per
    /// username; this connector runs one.
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
            return Result<CandleSeries>.Failure(IbkrErrors.InvalidRequest("The history window must end after it starts."));
        }

        var bar = IbkrMaps.ToNativeBar(request.TimeFrame);
        if (bar.IsFailure)
        {
            return Result<CandleSeries>.Failure(bar.Error);
        }

        var record = await RecordForAsync(request.Instrument, ct).ConfigureAwait(false);
        if (record.IsFailure)
        {
            return Result<CandleSeries>.Failure(record.Error);
        }

        var api = await _channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsFailure)
        {
            return Result<CandleSeries>.Failure(api.Error);
        }

        var byTime = new SortedDictionary<long, IbkrBarRow>();
        var cursor = request.From;
        var conid = record.Value.Conid.ToString(CultureInfo.InvariantCulture);

        for (var page = 0; page < _options.MaxHistoryPages && cursor < request.To; page++)
        {
            var response = await api.Value
                .GetAsync<IbkrHistory>(
                    HttpConnectorPath.WithQuery(
                        "iserver/marketdata/history",
                        ("conid", conid),
                        ("bar", bar.Value.Bar),
                        ("period", bar.Value.Period),
                        ("startTime", IbkrTime.UtcStamp(cursor)),
                        ("direction", "1"),
                        ("outsideRth", "false")),
                    ct)
                .ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<CandleSeries>.Failure(response.Error);
            }

            long? latest = null;
            foreach (var row in response.Value.Data ?? [])
            {
                if (IbkrNumber.Integer(row.Time) is { } time and > 0)
                {
                    byTime[time] = row;
                    latest = latest is { } seen && seen > time ? seen : time;
                }
            }

            var next = latest is { } last ? DateTimeOffset.FromUnixTimeMilliseconds(last) + bar.Value.Length : cursor + bar.Value.PageSpan;
            cursor = next > cursor ? next : cursor + bar.Value.PageSpan;
        }

        // Day, week and month bars are stamped at the start of their period, before a window that opens at the session
        // start; they are kept by market date rather than by instant.
        var zone = record.Value.Market.Zone;
        var byDate = request.TimeFrame is TimeFrame.OneDay or TimeFrame.OneWeek or TimeFrame.OneMonth;
        var fromDate = IbkrTime.MarketDate(request.From, zone);
        var toDate = IbkrTime.MarketDate(request.To, zone);

        var candles = new List<Candle>(byTime.Count);
        foreach (var (time, row) in byTime)
        {
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(time);
            var inWindow = byDate
                ? IbkrTime.MarketDate(openTime, zone) is var date && date >= fromDate && date <= toDate
                : openTime >= request.From && openTime <= request.To;

            if (!inWindow || IbkrNumber.Decimal(row.Close) is not { } close)
            {
                continue;
            }

            candles.Add(new Candle
            {
                OpenTime = openTime,
                Open = IbkrNumber.Decimal(row.Open) ?? close,
                High = IbkrNumber.Decimal(row.High) ?? close,
                Low = IbkrNumber.Decimal(row.Low) ?? close,
                Close = close,
                Volume = IbkrNumber.Decimal(row.Volume) is { } volume ? (long)decimal.Truncate(volume) : 0L,
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
    public Task<Result<MarketDepth>> GetDepthAsync(InstrumentKey instrument, CancellationToken ct = default) =>
        Task.FromResult(Result<MarketDepth>.Failure(ConnectorErrors.NotSupported(
            "market depth. IBKR's Client Portal API serves the book only on its BookTrader websocket, which this connector does not use")));

    /// <inheritdoc />
    /// <remarks>
    /// US underlyings only, and WINDOWED: the strikes nearest the underlying's price, up to
    /// <see cref="IbkrOptions.MaxChainStrikes"/>. Every strike and right is one contract lookup against a
    /// ten-requests-a-second limit, so the full chain of a liquid underlying is not practical to load on request.
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

        var owner = await RecordForAsync(underlying, ct).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return Result<OptionChain>.Failure(owner.Error);
        }

        if (!owner.Value.Market.Is(IbkrMarket.Us) || owner.Value.Definition.Key.AssetClass != AssetClass.Equity)
        {
            return Result<OptionChain>.Failure(ConnectorErrors.NotSupported("option chains on anything but US stocks through this connector"));
        }

        var api = await _channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsFailure)
        {
            return Result<OptionChain>.Failure(api.Error);
        }

        var ownerConid = owner.Value.Conid.ToString(CultureInfo.InvariantCulture);
        var month = IbkrTime.ContractMonth(expiry);

        var strikes = await api.Value
            .GetAsync<IbkrStrikes>(
                HttpConnectorPath.WithQuery(
                    "iserver/secdef/strikes",
                    ("conid", ownerConid),
                    ("sectype", IbkrMaps.SecTypeOption),
                    ("month", month),
                    ("exchange", IbkrMaps.SmartRouting)),
                ct)
            .ConfigureAwait(false);

        if (strikes.IsFailure)
        {
            return Result<OptionChain>.Failure(strikes.Error);
        }

        var spotRows = await SnapshotAsync([owner.Value.Conid], ct).ConfigureAwait(false);
        decimal? spot = spotRows.IsSuccess && spotRows.Value.TryGetValue(owner.Value.Conid, out var spotRow)
            ? IbkrNumber.Price(spotRow["31"], out _)
            : null;

        var calls = (strikes.Value.Call ?? []).ToHashSet();
        var puts = (strikes.Value.Put ?? []).ToHashSet();
        var all = calls.Union(puts).Where(s => s > 0m).Order().ToList();

        var chosen = (spot is { } price
                ? all.OrderBy(s => Math.Abs(s - price)).Take(_options.MaxChainStrikes)
                : all.Skip(Math.Max(0, (all.Count - _options.MaxChainStrikes) / 2)).Take(_options.MaxChainStrikes))
            .Order()
            .ToList();

        var contracts = new List<(decimal Strike, OptionRight Right, long Conid)>();

        foreach (var strike in chosen)
        {
            foreach (var right in (OptionRight[])[OptionRight.Call, OptionRight.Put])
            {
                if (!(right == OptionRight.Call ? calls : puts).Contains(strike))
                {
                    continue;
                }

                var info = await api.Value.SendElementAsync(
                    HttpMethod.Get,
                    HttpConnectorPath.WithQuery(
                        "iserver/secdef/info",
                        ("conid", ownerConid),
                        ("sectype", IbkrMaps.SecTypeOption),
                        ("month", month),
                        ("exchange", IbkrMaps.SmartRouting),
                        ("strike", strike.ToString(CultureInfo.InvariantCulture)),
                        ("right", right == OptionRight.Call ? "C" : "P")),
                    body: null,
                    ct).ConfigureAwait(false);

                if (info.IsFailure)
                {
                    return Result<OptionChain>.Failure(info.Error);
                }

                if (IbkrInstrumentResolver.ReadContracts(info.Value)
                        .Where(c => IbkrTime.ParseDate(c.MaturityDate) == expiry)
                        .Select(c => IbkrNumber.Integer(c.Conid))
                        .FirstOrDefault(c => c is > 0) is { } conid)
                {
                    contracts.Add((strike, right, conid));
                }
            }
        }

        var ensured = await _resolver.EnsureConidsAsync(_channel, contracts.Select(c => c.Conid), ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<OptionChain>.Failure(ensured.Error);
        }

        var quotes = await SnapshotAsync([.. contracts.Select(c => c.Conid)], ct).ConfigureAwait(false);
        if (quotes.IsFailure)
        {
            return Result<OptionChain>.Failure(quotes.Error);
        }

        var now = _clock.UtcNow;
        var rows = new SortedDictionary<decimal, OptionChainRow>();

        foreach (var (strike, right, conid) in contracts)
        {
            if (!_cache.TryGetByConid(conid, out var record))
            {
                continue;
            }

            quotes.Value.TryGetValue(conid, out var fields);
            var quote = fields is null ? null : ToQuote(record.Definition.Key, record.Definition.Currency, field => fields[field], now);
            var openInterest = IbkrNumber.Integer(fields?["7638"]);
            var row = rows.GetValueOrDefault(strike) ?? new OptionChainRow { Strike = strike };

            rows[strike] = right == OptionRight.Call
                ? row with { Call = quote, CallOpenInterest = openInterest }
                : row with { Put = quote, PutOpenInterest = openInterest };
        }

        return new OptionChain
        {
            Underlying = underlying,
            Expiry = expiry,
            Rows = [.. rows.Values],
            UnderlyingPrice = spot is > 0m ? new Money(spot.Value, owner.Value.Definition.Currency) : null,
        };
    }

    // --- shared with the stream --------------------------------------------------------------------------------

    internal static Quote? ToQuote(InstrumentKey key, Currency currency, Func<string, string?> field, DateTimeOffset now)
    {
        if (IbkrNumber.Price(field("31"), out _) is not { } last || last <= 0m)
        {
            return null;
        }

        return new Quote
        {
            Instrument = key,
            LastPrice = new Money(last, currency),
            Open = Positive(field("7295"), currency),
            High = Positive(field("70"), currency),
            Low = Positive(field("71"), currency),
            PreviousClose = Positive(field("7741"), currency),
            BidPrice = Positive(field("84"), currency),
            AskPrice = Positive(field("86"), currency),
            Volume = Volume(field),
            OpenInterest = IbkrNumber.Integer(field("7638")),
            Timestamp = IbkrTime.FromUnixMilliseconds(field("_updated")) ?? now,
        };
    }

    internal static Tick? ToTick(InstrumentKey key, Currency currency, Func<string, string?> field, DateTimeOffset now)
    {
        if (IbkrNumber.Price(field("31"), out _) is not { } last || last <= 0m)
        {
            return null;
        }

        return new Tick
        {
            Instrument = key,
            LastPrice = new Money(last, currency),
            LastQuantity = IbkrNumber.Scaled(field("7059")) is { } size && size > 0m ? new Quantity(size) : null,
            Volume = Volume(field),
            BidPrice = Positive(field("84"), currency),
            AskPrice = Positive(field("86"), currency),
            Open = Positive(field("7295"), currency),
            High = Positive(field("70"), currency),
            Low = Positive(field("71"), currency),
            PreviousClose = Positive(field("7741"), currency),
            OpenInterest = IbkrNumber.Integer(field("7638")),
            Timestamp = IbkrTime.FromUnixMilliseconds(field("_updated")) ?? now,
        };
    }

    private static long? Volume(Func<string, string?> field) =>
        IbkrNumber.Integer(field("7762")) ?? (IbkrNumber.Scaled(field("87")) is { } formatted ? (long)decimal.Truncate(formatted) : null);

    private static Money? Positive(string? value, Currency currency) =>
        IbkrNumber.Price(value, out _) is { } amount && amount > 0m ? new Money(amount, currency) : null;

    // --- plumbing ----------------------------------------------------------------------------------------------

    private async Task<Result<IbkrInstrumentRecord>> RecordForAsync(InstrumentKey key, CancellationToken ct)
    {
        var record = await _resolver.ForKeyAsync(_channel, key, ct).ConfigureAwait(false);
        if (record.IsFailure)
        {
            return record;
        }

        return IbkrInstrumentResolver.Matches(key, record.Value.Definition.Key)
            ? record
            : Result<IbkrInstrumentRecord>.Failure(ConnectorErrors.InstrumentNotFound(key));
    }

    /// <summary>Snapshot rows keyed by conid, with the pre-flight retry. Only rows carrying a last price are returned.</summary>
    private async Task<Result<Dictionary<long, IbkrFieldRow>>> SnapshotAsync(IReadOnlyCollection<long> conids, CancellationToken ct)
    {
        var result = new Dictionary<long, IbkrFieldRow>();
        var pending = conids.Where(c => c > 0).Distinct().ToList();

        if (pending.Count == 0)
        {
            return result;
        }

        var api = await _channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsFailure)
        {
            return Result<Dictionary<long, IbkrFieldRow>>.Failure(api.Error);
        }

        for (var attempt = 0; attempt <= _options.SnapshotRetries && pending.Count > 0; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(_options.SnapshotRetryDelay, ct).ConfigureAwait(false);
            }

            foreach (var chunk in pending.Chunk(Math.Max(1, _options.MaxSnapshotConids)))
            {
                var response = await api.Value.SendElementAsync(
                    HttpMethod.Get,
                    HttpConnectorPath.WithQuery(
                        "iserver/marketdata/snapshot",
                        ("conids", string.Join(",", chunk.Select(c => c.ToString(CultureInfo.InvariantCulture)))),
                        ("fields", IbkrMaps.SnapshotFields)),
                    body: null,
                    ct).ConfigureAwait(false);

                if (response.IsFailure)
                {
                    return Result<Dictionary<long, IbkrFieldRow>>.Failure(response.Error);
                }

                if (response.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var element in response.Value.EnumerateArray())
                {
                    var row = IbkrFieldRow.From(element);
                    if (row.Conid is { } conid && row.Has("31"))
                    {
                        result[conid] = row;
                    }
                }
            }

            pending = [.. pending.Where(c => !result.ContainsKey(c))];
        }

        return result;
    }
}
