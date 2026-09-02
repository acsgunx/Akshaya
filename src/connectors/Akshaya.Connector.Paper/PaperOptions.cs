using Akshaya.Connector.Paper.Charges;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Paper;

/// <summary>
/// Everything about the simulation that a user or a backtest is allowed to tune.
///
/// All of it is data, none of it is ambient. Two runs given the same options, the same seed
/// and the same tick sequence produce byte-identical fills — see the determinism remarks on
/// <see cref="MatchingEngine"/>.
/// </summary>
public sealed record PaperOptions
{
    /// <summary>The account id stamped on the simulated session and every position it holds.</summary>
    public string AccountId { get; init; } = "PAPER";

    /// <summary>
    /// Opening cash, per currency. Multi-currency because the manifest claims four currencies
    /// and a Singapore fill must not be able to spend Indian rupees.
    /// </summary>
    public IReadOnlyDictionary<string, decimal> StartingCash { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["INR"] = 1_000_000m,
            ["SGD"] = 100_000m,
            ["USD"] = 100_000m,
            ["HKD"] = 500_000m,
        };

    /// <summary>How aggressively fills are modelled. See <see cref="PaperFillPolicy"/>.</summary>
    public PaperFillPolicy Fills { get; init; } = new();

    /// <summary>
    /// Charge schedules consulted, in order, for the venue of the instrument being traded.
    /// Empty means charges are not modelled and <c>EstimateChargesAsync</c> declines rather
    /// than returning a comfortable zero — a backtest that silently assumes zero costs is the
    /// single most common way a strategy looks profitable and is not.
    /// </summary>
    public IReadOnlyList<IChargeSchedule> ChargeSchedules { get; init; } =
    [
        new IndiaChargeSchedule(),
        new SingaporeChargeSchedule(),
        new UsChargeSchedule(),
    ];

    /// <summary>
    /// Fraction of notional demanded as margin for leveraged position effects. A crude model,
    /// deliberately: a real SPAN/portfolio-margin calculation belongs to the risk module, and
    /// pretending to it here would give a backtest false confidence about capital efficiency.
    /// </summary>
    public decimal MarginFraction { get; init; } = 0.20m;
}

/// <summary>
/// The fill model. Every field here exists because its default is a lie in some market, and a
/// backtest that cannot vary them is a backtest of one set of assumptions.
/// </summary>
public sealed record PaperFillPolicy
{
    /// <summary>
    /// Slippage in basis points of the reference price, applied against the trader: a buy
    /// pays more, a sell receives less. Zero models a perfectly liquid venue, which no venue
    /// is; one basis point is a deliberately modest default that still stops a strategy from
    /// harvesting a spread that does not exist.
    /// </summary>
    public decimal SlippageBps { get; init; } = 1m;

    /// <summary>
    /// Additional slippage expressed in whole ticks of the instrument, added on top of
    /// <see cref="SlippageBps"/>. Basis points alone under-model slippage on low-priced
    /// instruments where the tick IS the spread.
    /// </summary>
    public decimal SlippageTicks { get; init; }

    /// <summary>
    /// Largest fraction of an order's ORIGINAL quantity that may fill against a single tick.
    ///
    /// Without this an order for a hundred thousand shares fills in one print, which never
    /// happens and makes every large-size strategy look tradable. Set to 1 to disable and
    /// fill greedily. Fill-or-kill orders ignore this by construction: a FOK either takes the
    /// whole size at once or dies, so a per-tick cap has no meaning for it.
    /// </summary>
    public decimal MaxFillRatioPerTick { get; init; } = 0.25m;

    /// <summary>
    /// When a tick reports its print size, refuse to fill more than that. On feeds with no
    /// size the cap cannot be applied and this has no effect.
    /// </summary>
    public bool RespectTickQuantity { get; init; } = true;

    /// <summary>
    /// Random reduction applied to each fill slice, as a fraction in [0,1). 0.2 means a slice
    /// is between 80% and 100% of the cap. Driven entirely by <see cref="Seed"/>, so it is
    /// reproducible; it exists so a strategy cannot be tuned to an unrealistically regular
    /// fill cadence.
    /// </summary>
    public decimal FillJitter { get; init; }

    /// <summary>
    /// Seed for the jitter generator. Explicit and injected rather than time-derived: a
    /// backtest whose fills change between runs cannot be debugged, and a strategy whose
    /// results change between runs cannot be trusted.
    /// </summary>
    public int Seed { get; init; } = 20260901;

    /// <summary>
    /// Fill limit orders that merely TOUCH their price (the default), or require the market to
    /// trade strictly through it.
    ///
    /// Touch is the intuitive behaviour and the one a trader expects from a paper account.
    /// Requiring a strict cross is the pessimistic queue model — being AT the price means
    /// being behind everyone already there — and is the honest setting for a strategy whose
    /// edge depends on passive fills, because that is precisely the strategy the optimistic
    /// model flatters.
    /// </summary>
    public bool FillLimitOnTouch { get; init; } = true;
}
