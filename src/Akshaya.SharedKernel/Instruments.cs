namespace Akshaya.SharedKernel;

/// <summary>
/// A trading venue, identified by its ISO 10383 MIC. Deliberately NOT an enum: an enum of
/// exchanges is how a platform accidentally becomes single-market. Adding SGX or NASDAQ must
/// be a row of reference data, not a recompile.
/// </summary>
public readonly record struct Venue
{
    public Venue(string mic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mic);
        Mic = mic.ToUpperInvariant();
    }

    public string Mic { get; }

    // Convenience handles for the venues we ship calendars for. Not an exhaustive list,
    // and nothing in the core may switch on these.
    public static readonly Venue Nse = new("XNSE");
    public static readonly Venue Bse = new("XBOM");
    public static readonly Venue Sgx = new("XSES");
    public static readonly Venue Nasdaq = new("XNAS");
    public static readonly Venue Nyse = new("XNYS");
    public static readonly Venue Hkex = new("XHKG");
    public static readonly Venue Tse = new("XTKS");
    public static readonly Venue Asx = new("XASX");

    public override string ToString() => Mic;
}

public enum AssetClass
{
    Equity,
    Etf,
    Future,
    Option,
    Index,
    Currency,
    Commodity,
    Bond,
    Fund,
    Crypto,
}

public enum OptionRight
{
    Call,
    Put,
}

/// <summary>
/// The canonical identity of a tradable instrument, independent of any broker. Connectors
/// translate this to and from their native symbology (INFY-EQ, a Kite instrument token, an
/// IBKR conid, HK.00700) via ISymbolTranslator — that translation is the connector's job
/// and never leaks upward.
/// </summary>
public readonly record struct InstrumentKey(
    Venue Venue,
    string Symbol,
    AssetClass AssetClass,
    DateOnly? Expiry = null,
    decimal? Strike = null,
    OptionRight? Right = null)
{
    public bool IsDerivative => AssetClass is AssetClass.Future or AssetClass.Option;

    /// <summary>Stable, human-readable, round-trippable. Used as a cache key and in the API.</summary>
    public override string ToString() => AssetClass switch
    {
        AssetClass.Option =>
            $"{Venue}:{Symbol}:OPT:{Expiry:yyyy-MM-dd}:{Strike}:{Right}",
        AssetClass.Future =>
            $"{Venue}:{Symbol}:FUT:{Expiry:yyyy-MM-dd}",
        _ => $"{Venue}:{Symbol}:{AssetClass}",
    };

    public static bool TryParse(string value, out InstrumentKey key)
    {
        key = default;
        var parts = value.Split(':');
        if (parts.Length < 3)
        {
            return false;
        }

        var venue = new Venue(parts[0]);
        var symbol = parts[1];

        switch (parts[2])
        {
            case "OPT" when parts.Length == 6
                            && DateOnly.TryParse(parts[3], out var optExpiry)
                            && decimal.TryParse(parts[4], out var strike)
                            && Enum.TryParse<OptionRight>(parts[5], out var right):
                key = new InstrumentKey(venue, symbol, AssetClass.Option, optExpiry, strike, right);
                return true;

            case "FUT" when parts.Length == 4 && DateOnly.TryParse(parts[3], out var futExpiry):
                key = new InstrumentKey(venue, symbol, AssetClass.Future, futExpiry);
                return true;

            default:
                if (!Enum.TryParse<AssetClass>(parts[2], out var assetClass))
                {
                    return false;
                }

                key = new InstrumentKey(venue, symbol, assetClass);
                return true;
        }
    }
}

/// <summary>
/// Everything the platform knows about an instrument, assembled from the canonical master
/// and enriched by connector ingests. ISIN and FIGI are what let a blended portfolio work
/// out that AAPL held at IBKR and AAPL held at Moomoo are the same position.
/// </summary>
public sealed record InstrumentDefinition
{
    public required InstrumentKey Key { get; init; }

    public required string Name { get; init; }

    public required Currency Currency { get; init; }

    public string? Isin { get; init; }

    public string? Figi { get; init; }

    /// <summary>Minimum tradable increment. 1 for most equities, the contract lot for F&amp;O.</summary>
    public decimal LotSize { get; init; } = 1m;

    /// <summary>Minimum price increment on the venue.</summary>
    public decimal TickSize { get; init; } = 0.01m;

    /// <summary>Contract multiplier: 1 for cash equity, the contract size for derivatives.</summary>
    public decimal Multiplier { get; init; } = 1m;

    /// <summary>Identifier into the trading-calendar service; venues have several session profiles.</summary>
    public string TradingHoursId { get; init; } = "default";

    public int SettlementDays { get; init; } = 1;

    public bool IsTradable { get; init; } = true;
}
