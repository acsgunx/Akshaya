using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Paper;

/// <summary>
/// Where the Paper connector's prices come from.
///
/// This is the seam that lets paper trading and backtesting be the SAME engine. A live paper
/// session injects a source backed by a real broker's feed; a backtest injects one that
/// replays a stored tick file as fast as it can read it. <see cref="MatchingEngine"/> cannot
/// tell the difference, which is the point: a fill that happened in a backtest happened for
/// exactly the reason it would have happened in paper trading.
///
/// Implementations must be thread-safe for the lookup members. <see cref="Ticks"/> is
/// enumerated exactly once, by the engine, which then fans out — see
/// <see cref="MatchingEngine.Subscribe"/>. Enumerating it in two places would give the engine
/// and the stream different halves of the tape.
/// </summary>
public interface IMarketDataSource
{
    /// <summary>
    /// Every instrument this source can price. Ordered, and stable across calls, because the
    /// reference facet enumerates it and a backtest that produced a different instrument
    /// ordering on a re-run would not be reproducible.
    /// </summary>
    IReadOnlyList<InstrumentDefinition> Instruments { get; }

    /// <summary>
    /// Definition for one instrument, or <see cref="ConnectorErrorCodes.InstrumentNotFound"/>.
    /// The engine needs lot size, tick size and contract multiplier to size fills and value
    /// positions; guessing any of them silently mis-prices a derivative by its multiplier.
    /// </summary>
    Result<InstrumentDefinition> Resolve(InstrumentKey key);

    /// <summary>
    /// Most recent traded price known to the source, in the instrument's own currency.
    ///
    /// Distinct from the engine's own last-tick cache: this answers before any tick has been
    /// consumed (a paper session that starts mid-day, a backtest asked to mark a position on
    /// its first bar), which is exactly when the engine's cache is empty.
    /// </summary>
    Result<Money> LastPrice(InstrumentKey key);

    /// <summary>
    /// Historical candles.
    ///
    /// On the interface rather than bolted onto the connector because the data lives here and
    /// nowhere else: a backtest source can aggregate its own tape, a live paper source proxies
    /// the broker it is shadowing, and neither can be faked by the engine. The manifest claims
    /// <c>historical: true</c>, so a source that cannot answer must say so with
    /// <see cref="ConnectorErrorCodes.NotSupported"/> rather than returning empty candles —
    /// an empty series reads as "the market did not trade".
    /// </summary>
    Result<CandleSeries> History(HistoryRequest request);

    /// <summary>
    /// The tape. Completes when the source is exhausted — a backtest reaching the end of its
    /// data — which the engine treats as end of session, not as an error.
    /// </summary>
    IAsyncEnumerable<Tick> Ticks(CancellationToken ct = default);
}
