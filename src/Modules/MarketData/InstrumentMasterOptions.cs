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

    /// <summary>
    /// Whether an aged-out snapshot is served immediately while its replacement downloads
    /// behind it, instead of making the caller wait for the download.
    ///
    /// On by default, because the alternative is a cliff: for twelve hours the search box is
    /// instant, and then one unlucky trader's keystroke is the one that pays for a few hundred
    /// thousand rows to come down the wire. They did nothing different; they just arrived
    /// first after the interval elapsed. What they get instead is the list from twelve hours
    /// ago — which, for a search box, differs from the fresh one by a handful of new strikes —
    /// and the fresh one lands moments later for everyone.
    ///
    /// It does NOT apply to the first load. With nothing in memory there is nothing to serve,
    /// so that caller waits; <see cref="TimeSpan.Zero"/> here turns the behaviour off entirely.
    /// </summary>
    public bool ServeStaleWhileRefreshing { get; init; } = true;

    /// <summary>
    /// How far past <see cref="RefreshInterval"/> a snapshot may still be served while a
    /// refresh runs. Past this it is treated as no snapshot at all and the caller waits, so a
    /// broker whose master has been failing to download for days cannot leave the search box
    /// quietly answering from a list that predates half the contracts on it.
    /// </summary>
    public TimeSpan MaxStaleAge { get; init; } = TimeSpan.FromDays(3);
}
