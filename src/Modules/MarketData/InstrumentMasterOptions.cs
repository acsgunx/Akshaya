namespace Akshaya.Modules.MarketData;

/// <summary>Tuning for <see cref="InstrumentMaster"/>. Binds from <c>MarketData:InstrumentMaster</c>.</summary>
public sealed record InstrumentMasterOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "MarketData:InstrumentMaster";

    /// <summary>
    /// How long a loaded snapshot is served before it is reloaded.
    ///
    /// Instrument masters change once a day — new listings, new option strikes, yesterday's
    /// expiries gone — so this trades a few hours of staleness for not re-downloading a
    /// multi-hundred-megabyte CSV. Twelve hours means a session that starts before the open
    /// reloads once during the day rather than never.
    /// </summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Hard ceiling on rows held per connector. A guard against a broker returning something
    /// unbounded and taking the API process out with it, not a tuning knob: the real masters
    /// are in the low hundreds of thousands and the default is well clear of them.
    /// </summary>
    public int MaxInstruments { get; init; } = 2_000_000;

    /// <summary>
    /// Whether a stale snapshot may be served when a refresh fails.
    ///
    /// On by default, for the same reason the blended portfolio degrades rather than blanks:
    /// yesterday's instrument list is enormously more useful than an error page, and the rows
    /// a trader is searching for were almost certainly in it.
    /// </summary>
    public bool ServeStaleOnRefreshFailure { get; init; } = true;
}
