using System.Runtime.CompilerServices;
using Akshaya.Connector.Paper;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.TestKit.TestDoubles;

/// <summary>
/// A tick tape held in a list.
///
/// This is the double that makes a backtest testable: the same
/// <see cref="Akshaya.Connector.Paper.MatchingEngine"/> that runs against a live feed runs
/// against this, and because the tape is a list rather than a timer, a test can assert on the
/// exact fills a known sequence of prices produces. Nothing here is asynchronous in substance
/// — <see cref="Ticks"/> is an async enumerable only because the interface is.
/// </summary>
public sealed class InMemoryMarketDataSource : IMarketDataSource
{
    private readonly List<InstrumentDefinition> _instruments = [];
    private readonly Dictionary<InstrumentKey, InstrumentDefinition> _byKey = [];
    private readonly Dictionary<InstrumentKey, Money> _lastPrices = [];
    private readonly List<Tick> _tape = [];

    /// <inheritdoc />
    public IReadOnlyList<InstrumentDefinition> Instruments => _instruments;

    /// <summary>The tape, in the order it will be replayed.</summary>
    public IReadOnlyList<Tick> Tape => _tape;

    /// <summary>Adds an instrument to the universe and seeds its last price.</summary>
    public InMemoryMarketDataSource WithInstrument(InstrumentDefinition definition, decimal lastPrice)
    {
        ArgumentNullException.ThrowIfNull(definition);

        _instruments.Add(definition);
        _byKey[definition.Key] = definition;
        _lastPrices[definition.Key] = new Money(lastPrice, definition.Currency);
        return this;
    }

    /// <summary>Appends a tick to the tape and updates the last price the source reports.</summary>
    public InMemoryMarketDataSource WithTick(Tick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        _tape.Add(tick);
        _lastPrices[tick.Instrument] = tick.LastPrice;
        return this;
    }

    /// <summary>Appends a simple last-price-only tick. The common case in a test.</summary>
    public InMemoryMarketDataSource WithTick(
        InstrumentKey instrument,
        decimal last,
        DateTimeOffset at,
        decimal? bid = null,
        decimal? ask = null,
        decimal? size = null)
    {
        var currency = _byKey.TryGetValue(instrument, out var definition)
            ? definition.Currency
            : Currency.Inr;

        return WithTick(new Tick
        {
            Instrument = instrument,
            LastPrice = new Money(last, currency),
            BidPrice = bid is { } b ? new Money(b, currency) : null,
            AskPrice = ask is { } a ? new Money(a, currency) : null,
            LastQuantity = size is { } s ? new Quantity(s) : null,
            Timestamp = at,
        });
    }

    /// <inheritdoc />
    public Result<InstrumentDefinition> Resolve(InstrumentKey key) =>
        _byKey.TryGetValue(key, out var definition)
            ? definition
            : Result<InstrumentDefinition>.Failure(ConnectorErrors.InstrumentNotFound(key));

    /// <inheritdoc />
    public Result<Money> LastPrice(InstrumentKey key) =>
        _lastPrices.TryGetValue(key, out var price)
            ? price
            : Result<Money>.Failure(ConnectorErrors.InstrumentNotFound(key));

    /// <inheritdoc />
    /// <remarks>
    /// Aggregated from the tape rather than stored separately, so a test cannot accidentally
    /// assert against history that disagrees with the ticks the engine actually saw. Real
    /// history and real ticks disagreeing is a genuine production problem; a test double that
    /// makes it impossible is a test double that will not catch it, but it is also one that
    /// will not produce false failures about it.
    /// </remarks>
    public Result<CandleSeries> History(HistoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_byKey.TryGetValue(request.Instrument, out var definition))
        {
            return Result<CandleSeries>.Failure(ConnectorErrors.InstrumentNotFound(request.Instrument));
        }

        var bucket = request.TimeFrame.ToTimeSpan();
        var candles = new List<Candle>();
        var open = 0m;
        var high = 0m;
        var low = 0m;
        var close = 0m;
        var volume = 0L;
        DateTimeOffset? bucketStart = null;

        foreach (var tick in _tape)
        {
            if (tick.Instrument != request.Instrument
                || tick.Timestamp < request.From
                || tick.Timestamp > request.To)
            {
                continue;
            }

            var start = Floor(tick.Timestamp, bucket);

            if (bucketStart is null)
            {
                bucketStart = start;
                open = high = low = close = tick.LastPrice.Amount;
                volume = 0L;
            }
            else if (start != bucketStart)
            {
                candles.Add(new Candle
                {
                    OpenTime = bucketStart.Value,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = volume,
                });

                bucketStart = start;
                open = high = low = close = tick.LastPrice.Amount;
                volume = 0L;
            }

            var price = tick.LastPrice.Amount;
            high = Math.Max(high, price);
            low = Math.Min(low, price);
            close = price;
            volume += (long)(tick.LastQuantity?.Value ?? 0m);
        }

        if (bucketStart is not null)
        {
            candles.Add(new Candle
            {
                OpenTime = bucketStart.Value,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
            });
        }

        return new CandleSeries
        {
            Instrument = request.Instrument,
            TimeFrame = request.TimeFrame,
            Currency = definition.Currency,
            Candles = candles,
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Tick> Ticks([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var tick in _tape)
        {
            ct.ThrowIfCancellationRequested();
            yield return tick;
        }

        await Task.CompletedTask;
    }

    private static DateTimeOffset Floor(DateTimeOffset at, TimeSpan bucket)
    {
        if (bucket <= TimeSpan.Zero)
        {
            return at;
        }

        var ticks = at.UtcTicks - (at.UtcTicks % bucket.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
