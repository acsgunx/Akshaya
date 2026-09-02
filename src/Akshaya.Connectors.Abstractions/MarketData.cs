using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Abstractions;

public sealed record Quote
{
    public required InstrumentKey Instrument { get; init; }

    public required Money LastPrice { get; init; }

    public Money? Open { get; init; }

    public Money? High { get; init; }

    public Money? Low { get; init; }

    public Money? PreviousClose { get; init; }

    public Money? BidPrice { get; init; }

    public Money? AskPrice { get; init; }

    public Quantity? BidQuantity { get; init; }

    public Quantity? AskQuantity { get; init; }

    public long? Volume { get; init; }

    public long? OpenInterest { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public Money? Change => PreviousClose is { } prev ? LastPrice - prev : null;

    public decimal? ChangePercent => PreviousClose is { } prev && prev.Amount != 0m
        ? (LastPrice.Amount - prev.Amount) / prev.Amount * 100m
        : null;
}

public sealed record Candle
{
    public required DateTimeOffset OpenTime { get; init; }

    public required decimal Open { get; init; }

    public required decimal High { get; init; }

    public required decimal Low { get; init; }

    public required decimal Close { get; init; }

    public required long Volume { get; init; }

    public long? OpenInterest { get; init; }
}

public sealed record CandleSeries
{
    public required InstrumentKey Instrument { get; init; }

    public required TimeFrame TimeFrame { get; init; }

    public required Currency Currency { get; init; }

    public required IReadOnlyList<Candle> Candles { get; init; }
}

public sealed record DepthLevel(Money Price, Quantity Quantity, int? Orders = null);

public sealed record MarketDepth
{
    public required InstrumentKey Instrument { get; init; }

    public required IReadOnlyList<DepthLevel> Bids { get; init; }

    public required IReadOnlyList<DepthLevel> Asks { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
}

public sealed record OptionChainRow
{
    public required decimal Strike { get; init; }

    public Quote? Call { get; init; }

    public Quote? Put { get; init; }

    public long? CallOpenInterest { get; init; }

    public long? PutOpenInterest { get; init; }
}

public sealed record OptionChain
{
    public required InstrumentKey Underlying { get; init; }

    public required DateOnly Expiry { get; init; }

    public required IReadOnlyList<OptionChainRow> Rows { get; init; }

    public Money? UnderlyingPrice { get; init; }
}

public sealed record HistoryRequest
{
    public required InstrumentKey Instrument { get; init; }

    public required TimeFrame TimeFrame { get; init; }

    public required DateTimeOffset From { get; init; }

    public required DateTimeOffset To { get; init; }
}

public interface IConnectorMarketData
{
    Task<Result<Quote>> GetQuoteAsync(InstrumentKey instrument, CancellationToken ct = default);

    Task<Result<IReadOnlyDictionary<InstrumentKey, Money>>> GetLtpAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default);

    Task<Result<IReadOnlyDictionary<InstrumentKey, Quote>>> GetQuotesAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default);

    Task<Result<CandleSeries>> GetHistoricalAsync(HistoryRequest request, CancellationToken ct = default);

    Task<Result<MarketDepth>> GetDepthAsync(InstrumentKey instrument, CancellationToken ct = default);

    Task<Result<OptionChain>> GetOptionChainAsync(
        InstrumentKey underlying,
        DateOnly expiry,
        CancellationToken ct = default);
}

/// <summary>
/// Instrument reference data. Separated from market data because the ingest is a bulk,
/// scheduled, streaming job with completely different rate limits and failure modes —
/// mStock's script master is a CSV of hundreds of thousands of rows.
/// </summary>
public interface IConnectorReference
{
    /// <summary>Streamed, not listed: instrument masters do not fit comfortably in memory.</summary>
    IAsyncEnumerable<InstrumentDefinition> GetInstrumentsAsync(
        Venue? venue = null,
        AssetClass? assetClass = null,
        CancellationToken ct = default);

    Task<Result<InstrumentDefinition>> ResolveAsync(InstrumentKey key, CancellationToken ct = default);

    Task<Result<IReadOnlyList<InstrumentDefinition>>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken ct = default);
}

/// <summary>
/// Canonical identity to the broker's own symbology and back. Lives entirely inside the
/// connector. A failed translation is InstrumentNotFound — never a guess, because a guessed
/// symbol is an order on the wrong instrument.
/// </summary>
public interface ISymbolTranslator
{
    Result<string> ToNative(InstrumentKey key);

    Result<InstrumentKey> ToCanonical(string nativeSymbol, string? nativeExchange = null);
}
