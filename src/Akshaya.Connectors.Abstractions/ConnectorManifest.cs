using System.Text.Json.Serialization;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Abstractions;

public enum ConnectorHosting
{
    /// <summary>Loaded into the API process in its own AssemblyLoadContext.</summary>
    InProcess,

    /// <summary>A separate process or container speaking gRPC. Can be written in any language.</summary>
    OutOfProcess,

    /// <summary>Needs a supervised vendor daemon (Moomoo OpenD, IBKR Client Portal Gateway).</summary>
    Gateway,
}

/// <summary>
/// The declarative description of what a broker can do. This is the mechanism that keeps the
/// core and the UI broker-agnostic: the order ticket renders from this, the risk gate
/// validates against it, the link wizard builds its form from it, and the conformance suite
/// checks the connector actually behaves the way its manifest claims.
///
/// The rule that makes plug-and-play real: if the core or the UI needs to know something
/// about a broker, it becomes a field here. It never becomes an `if (connectorId == ...)`.
/// </summary>
public sealed record ConnectorManifest
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Vendor { get; init; }

    /// <summary>
    /// Which version of the connector CONTRACT this was built against. The host accepts the
    /// current major and one behind, so a third-party connector does not break on our deploy.
    /// </summary>
    public required string ContractVersion { get; init; }

    public required string ConnectorVersion { get; init; }

    public ConnectorHosting Hosting { get; init; } = ConnectorHosting.InProcess;

    public GatewaySpec? Gateway { get; init; }

    /// <summary>ISO 3166 country codes this broker operates in. Drives the compliance surface.</summary>
    public required IReadOnlyList<string> Jurisdictions { get; init; }

    /// <summary>MIC codes. The UI uses this to show a trader which brokers can reach which venue.</summary>
    public required IReadOnlyList<string> Venues { get; init; }

    public required IReadOnlyList<string> Currencies { get; init; }

    public required IReadOnlyList<AssetClass> AssetClasses { get; init; }

    public required AuthSpec Auth { get; init; }

    public required OrderSpec Orders { get; init; }

    public required MarketDataSpec MarketData { get; init; }

    public IReadOnlyList<RateLimitSpec> RateLimits { get; init; } = [];

    public SandboxSpec? Sandbox { get; init; }

    public ComplianceSpec Compliance { get; init; } = new();

    [JsonIgnore]
    public IReadOnlyList<Venue> ParsedVenues => Venues.Select(v => new Venue(v)).ToList();

    [JsonIgnore]
    public IReadOnlyList<Currency> ParsedCurrencies => Currencies.Select(c => new Currency(c)).ToList();

    public bool SupportsVenue(Venue venue) => Venues.Contains(venue.Mic, StringComparer.OrdinalIgnoreCase);

    public bool SupportsAssetClass(AssetClass assetClass) => AssetClasses.Contains(assetClass);
}

public sealed record GatewaySpec
{
    /// <summary>Stable identifier used to address a running gateway instance.</summary>
    public required string Id { get; init; }

    /// <summary>Container image the host supervises, when we run it ourselves.</summary>
    public string? Image { get; init; }

    public int? Port { get; init; }

    /// <summary>
    /// True when every user needs their own gateway process (Moomoo OpenD, IBKR CP Gateway).
    /// This has a direct per-user infrastructure cost and the pricing model must know it.
    /// </summary>
    public bool PerCredential { get; init; } = true;

    public string? HealthEndpoint { get; init; }

    public string? SetupInstructionsUrl { get; init; }
}

public sealed record AuthSpec
{
    public required AuthModel Model { get; init; }

    public IReadOnlyList<ChallengeKind> Challenges { get; init; } = [];

    /// <summary>Nominal session lifetime. Ignored when ExpiresAtVenueMidnight is true and sooner.</summary>
    public TimeSpan? SessionLifetime { get; init; }

    /// <summary>
    /// True for most Indian brokers: the token dies at midnight in the venue's timezone no
    /// matter when it was issued. Modelled explicitly because assuming a rolling lifetime
    /// here silently kills sessions mid-session.
    /// </summary>
    public bool ExpiresAtVenueMidnight { get; init; }

    public string? VenueMidnightTimeZone { get; init; }

    public bool RefreshSupported { get; init; }

    /// <summary>Non-null for brokers that drop idle sessions (IBKR's /tickle, roughly 1/sec).</summary>
    public TimeSpan? KeepAliveInterval { get; init; }

    /// <summary>What the link wizard asks for. The wizard is generic; this is its form definition.</summary>
    public required IReadOnlyList<CredentialField> CredentialFields { get; init; }
}

public sealed record CredentialField
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public bool Secret { get; init; }

    public bool Optional { get; init; }

    public string? Placeholder { get; init; }

    public string? Help { get; init; }

    /// <summary>Client-side validation pattern. Server-side validation is never skipped.</summary>
    public string? Pattern { get; init; }
}

public sealed record OrderSpec
{
    public required IReadOnlyList<OrderType> Types { get; init; }

    public required IReadOnlyList<TimeInForce> TimeInForce { get; init; }

    public required IReadOnlyList<PositionEffect> PositionEffects { get; init; }

    public IReadOnlyList<OrderVariety> Varieties { get; init; } = [OrderVariety.Regular];

    /// <summary>Which fields a modify can change. Brokers differ; the UI disables the rest.</summary>
    public IReadOnlyList<string> Modifiable { get; init; } = [];

    public bool FractionalQuantity { get; init; }

    public bool ShortSellEquity { get; init; }

    public BasketSpec Basket { get; init; } = new();

    public bool Bracket { get; init; }

    public bool Cover { get; init; }

    public bool Gtt { get; init; }

    public bool MarginEstimate { get; init; }

    public bool ChargesEstimate { get; init; }

    public bool CancelAll { get; init; }

    public bool Supports(OrderType type) => Types.Contains(type);

    public bool Supports(TimeInForce tif) => TimeInForce.Contains(tif);

    public bool Supports(PositionEffect effect) => PositionEffects.Contains(effect);
}

public sealed record BasketSpec
{
    public bool Supported { get; init; }

    public int MaxLegs { get; init; }

    /// <summary>
    /// False when the connector implements a basket by looping single orders. The UI must warn
    /// that partial execution is possible — a trader assuming atomicity can end up half-hedged.
    /// </summary>
    public bool Atomic { get; init; }
}

public sealed record MarketDataSpec
{
    public bool Streaming { get; init; }

    public IReadOnlyList<StreamMode> StreamModes { get; init; } = [];

    public int DepthLevels { get; init; }

    public bool Historical { get; init; }

    public IReadOnlyList<TimeFrame> HistoricalTimeFrames { get; init; } = [];

    public bool OptionChain { get; init; }

    /// <summary>Hard cap the fan-out layer must respect, or the broker drops the connection.</summary>
    public int MaxStreamSubscriptions { get; init; } = int.MaxValue;

    /// <summary>How far back history goes. Beyond this we serve from our own Timescale store.</summary>
    public int? HistoryDays { get; init; }
}

public sealed record RateLimitSpec
{
    /// <summary>"orders" | "data" | "quotes" | "global". Buckets are per credential, not per app.</summary>
    public required string Scope { get; init; }

    public int? PerSecond { get; init; }

    public int? PerMinute { get; init; }

    public int? PerDay { get; init; }
}

public sealed record SandboxSpec
{
    public bool Available { get; init; }

    public string? BaseUrl { get; init; }

    public string? Notes { get; init; }
}

public sealed record ComplianceSpec
{
    /// <summary>
    /// True where automated order flow needs the broker's/exchange's blessing before it may
    /// run — India's SEBI rules being the case we hit first. When true, the platform keeps
    /// live automation behind a per-tenant flag that an operator must deliberately turn on.
    /// </summary>
    public bool AlgoApprovalRequired { get; init; }

    public string? Regulator { get; init; }

    /// <summary>Set when the broker requires automated orders to carry an identifier.</summary>
    public bool AlgoIdRequired { get; init; }

    public string? Notes { get; init; }
}
