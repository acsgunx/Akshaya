using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Ports;

/// <summary>
/// The account facts the risk gate needs but does not own.
///
/// Realised P&amp;L is a LIST, one entry per currency, and never a single figure. Collapsing a
/// multi-currency account into one number requires a rate, and a rate chosen silently by
/// whichever component happened to need it first is how a "daily loss limit" ends up enforced
/// against a number nobody can reproduce.
/// </summary>
public sealed record RiskSnapshot
{
    /// <summary>Distinct instruments with a non-zero net position across the link.</summary>
    public required int OpenPositionCount { get; init; }

    /// <summary>Realised P&amp;L so far today, natively per currency. Negative is a loss.</summary>
    public required IReadOnlyList<Money> RealisedPnlToday { get; init; }

    /// <summary>Cash available to trade, natively per currency. Informational for the gate today.</summary>
    public IReadOnlyList<Money> AvailableToTrade { get; init; } = [];

    /// <summary>
    /// Signed net quantity per instrument, keyed by <see cref="InstrumentKey.ToString"/>.
    /// Negative is short. This is what lets the gate tell an OPENING order from a CLOSING one,
    /// which decides whether the position-count and daily-loss rules apply at all — blocking
    /// the exit is how a risk limit turns a bad day into an unmanageable one.
    /// </summary>
    public IReadOnlyDictionary<string, decimal> NetPositions { get; init; }
        = new Dictionary<string, decimal>(StringComparer.Ordinal);

    /// <summary>
    /// True when this snapshot is a best-effort answer assembled while at least one source was
    /// unavailable. Rules that would otherwise fail closed use it to say so precisely.
    /// </summary>
    public bool IsPartial { get; init; }

    public static readonly RiskSnapshot Empty = new()
    {
        OpenPositionCount = 0,
        RealisedPnlToday = [],
    };
}

/// <summary>
/// Supplies <see cref="RiskSnapshot"/> for one broker link. Implemented over the connector's
/// portfolio facet and a cache — the risk gate sits on the order path and cannot afford a
/// fresh round trip to the broker for every order.
/// </summary>
public interface IRiskSnapshotProvider
{
    /// <summary>
    /// The snapshot for one link, from cache when it is warm.
    /// </summary>
    /// <param name="tenantId">The tenant the caller is acting for.</param>
    /// <param name="userId">The caller's own user id within that tenant.</param>
    /// <param name="brokerLinkId">The link whose positions and balances the snapshot describes.</param>
    /// <param name="connector">
    /// A connector the CALLER already holds open for this link, when it has one. Supplying it
    /// is not an optimisation of style: the order path resolves a connector, and an
    /// implementation that activated a second one for the same link would open a second
    /// session, a second set of sockets and a second decorator chain to ask a question the
    /// caller's own connector could already answer. The caller keeps ownership — the provider
    /// must never dispose a connector it was handed.
    ///
    /// It sits BEFORE <paramref name="ct"/> because CA1068 requires the cancellation token to
    /// be the last parameter. Both are optional, so callers that pass neither are unaffected.
    /// </param>
    /// <param name="ct">Cancels the build. A cached snapshot is returned without observing it.</param>
    Task<RiskSnapshot> GetAsync(
        string tenantId,
        string userId,
        string brokerLinkId,
        IBrokerConnector? connector = null,
        CancellationToken ct = default);
}
