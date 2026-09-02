using System.Globalization;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Akshaya.Connectors.TestKit.FakeConnectors;

/// <summary>
/// A fictional Indian broker: password plus SMS OTP, rupees only, whole lot-based quantities,
/// NO live feed, no bracket orders, no refresh, and a session that dies at midnight India time
/// no matter when it was issued.
///
/// Beta is the awkward broker, and every one of its awkwardnesses is real:
///
///  * <b>No stream at all.</b> <see cref="Stream"/> is null, and the contract says callers
///    must handle that rather than assume. A platform that quietly assumed a socket would work
///    perfectly against <see cref="AlphaFakeConnector"/> and null-reference against this one.
///  * <b>Whole quantities.</b> Fractional shares do not exist here, and the manifest says so,
///    so the risk gate rejects a fraction before it ever reaches the broker.
///  * <b>Venue-midnight expiry.</b> A token issued at 23:50 IST is dead in ten minutes despite
///    its nominal twelve-hour lifetime. This is the single most common way an Indian broker
///    integration loses orders, and it deserves a fixture.
///  * <b>No refresh.</b> The only way out of an expired session is an interactive login, and
///    the manifest saying so is what stops the session monitor spinning on a refresh that will
///    never work.
/// </summary>
public sealed class BetaFakeConnector : ConnectorBase
{
    /// <summary>Connector id, matching the manifest.</summary>
    public const string ConnectorId = "beta";

    /// <summary>Creates the connector.</summary>
    public BetaFakeConnector(
        ConnectorManifest manifest,
        BrokerSession? session,
        IClock clock,
        ILogger? logger = null)
        : base(manifest, session, logger ?? NullLogger.Instance, clock)
    {
        Book = new FakeBrokerBook(manifest, RequireSession, ValidateAgainstManifest, Clock);

        foreach (var definition in Universe())
        {
            Book.WithInstrument(definition, ReferencePrice(definition.Key.Symbol));
        }

        Symbols = new BetaSymbolTranslator(Universe());
        AuthFacet = new BetaAuth(Clock);
    }

    /// <summary>The in-memory book, exposed so a test can arm a timeout on it.</summary>
    public FakeBrokerBook Book { get; }

    /// <summary>Beta's symbology: a table from the daily script master, with no structural fallback.</summary>
    public ISymbolTranslator Symbols { get; }

    private BetaAuth AuthFacet { get; }

    /// <inheritdoc />
    public override IConnectorAuth Auth => AuthFacet;

    /// <inheritdoc />
    public override IConnectorOrders Orders => Book;

    /// <inheritdoc />
    public override IConnectorPortfolio Portfolio => Book;

    /// <inheritdoc />
    public override IConnectorMarketData MarketData => Book;

    /// <inheritdoc />
    public override IConnectorReference Reference => Book;

    /// <inheritdoc />
    /// <remarks>
    /// Null, and deliberately not <c>NullStream.Instance</c>. The manifest declares
    /// <c>streaming: false</c>, and the contract's answer for "this broker has no feed" is a
    /// null Stream. Returning a stream object that refuses everything would satisfy a caller
    /// that forgot to null-check, and that caller would then break on the next broker.
    /// </remarks>
    public override IConnectorStream? Stream => null;

    /// <summary>
    /// Beta's error vocabulary: hyphenated numeric codes, and one phrase the SDK's generic
    /// table does not know. The generic layer is left on, because Beta's wording does not
    /// collide with it.
    /// </summary>
    public static IVendorErrorMapper CreateErrorMapper() => new DefaultVendorErrorMapper(
        vendorCodes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["B-101"] = ConnectorErrorCodes.InvalidCredentials,
            ["B-102"] = ConnectorErrorCodes.ChallengeFailed,
            ["B-201"] = ConnectorErrorCodes.SessionExpired,
            ["B-220"] = ConnectorErrorCodes.OrderRejected,
            ["B-330"] = ConnectorErrorCodes.MarketClosed,
            ["B-440"] = ConnectorErrorCodes.InsufficientFunds,
        },
        messagePhrases:
        [
            // "RMS" is India-specific risk-management wording that the generic table would
            // otherwise leave as Unknown, which the UI renders as an unhelpful shrug.
            ("rms rejection", ConnectorErrorCodes.RiskRejected),
        ]);

    /// <summary>
    /// The instruments Beta trades. Includes a futures contract with a real lot size, so the
    /// whole-quantity and multiplier paths are actually exercised rather than assumed.
    /// </summary>
    public static IReadOnlyList<InstrumentDefinition> Universe() =>
    [
        new InstrumentDefinition
        {
            Key = new InstrumentKey(Venue.Nse, "INFY", AssetClass.Equity),
            Name = "Infosys Limited",
            Currency = Currency.Inr,
            Isin = "INE009A01021",
            LotSize = 1m,
            TickSize = 0.05m,
        },
        new InstrumentDefinition
        {
            Key = new InstrumentKey(Venue.Nse, "RELIANCE", AssetClass.Equity),
            Name = "Reliance Industries Limited",
            Currency = Currency.Inr,
            Isin = "INE002A01018",
            LotSize = 1m,
            TickSize = 0.05m,
        },
        new InstrumentDefinition
        {
            Key = new InstrumentKey(
                Venue.Nse,
                "NIFTY",
                AssetClass.Future,
                new DateOnly(2026, 9, 24)),
            Name = "NIFTY futures 2026-09",
            Currency = Currency.Inr,
            LotSize = 25m,
            TickSize = 0.05m,
            Multiplier = 25m,
        },
    ];

    private static decimal ReferencePrice(string symbol) => symbol switch
    {
        "INFY" => 1_540.25m,
        "RELIANCE" => 2_980.50m,
        "NIFTY" => 24_800m,
        _ => 100m,
    };
}

/// <summary>
/// Beta's login: password first, then an SMS OTP.
///
/// The interesting part is <see cref="Issue"/>. Expiry is the EARLIER of the nominal lifetime
/// and the next midnight in India — not the later, and not the broker's own claim. Being early
/// costs one extra login; being late costs orders, and the asymmetry decides the direction.
/// </summary>
public sealed class BetaAuth(IClock clock) : IConnectorAuth
{
    /// <summary>The venue whose midnight kills the token.</summary>
    public const string VenueTimeZoneId = "Asia/Kolkata";

    /// <summary>Nominal token lifetime, before the midnight rule bites.</summary>
    public static readonly TimeSpan NominalLifetime = TimeSpan.FromHours(12);

    /// <summary>The OTP this fake accepts. Fixed so tests are deterministic.</summary>
    public const string ValidOtp = "123456";

    /// <inheritdoc />
    public Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var username = context.Credentials.GetOrDefault("username");
        var password = context.Credentials.GetOrDefault("password");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return Task.FromResult(Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "Beta needs a client code and a password.")));
        }

        return Task.FromResult(Result<AuthStep>.Success(new AuthStep.ChallengeRequired(
            ChallengeKind.SmsOtp,
            "Enter the one-time password sent to your registered mobile number.",
            "+91 ****1234",
            TimeSpan.FromMinutes(3))));
    }

    /// <inheritdoc />
    public Task<Result<AuthStep>> ContinueAsync(
        AuthContext context,
        string response,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(response?.Trim(), ValidOtp, StringComparison.Ordinal))
        {
            return Task.FromResult(Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.ChallengeFailed,
                "That one-time password was not accepted.",
                VendorCode: "B-102",
                VendorMessage: "Invalid OTP")));
        }

        return Task.FromResult(Result<AuthStep>.Success(new AuthStep.Completed(Issue())));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Declines, matching <c>auth.refreshSupported: false</c>. NotSupported and not
    /// ReauthRequired: the capability does not exist at all, so the session monitor must stop
    /// asking rather than retry. The conformance suite checks this against the manifest.
    /// </remarks>
    public Task<Result<BrokerSession>> RefreshAsync(BrokerSession session, CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<BrokerSession>("session refresh");

    /// <inheritdoc />
    public Task<Result> RevokeAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    /// <inheritdoc />
    public Task<Result> KeepAliveAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    /// <summary>
    /// The earlier of issue-plus-lifetime and the next Indian midnight. Public and static so a
    /// test can assert the rule directly rather than inferring it from a session.
    /// </summary>
    public static DateTimeOffset ComputeExpiry(DateTimeOffset issuedAt) =>
        Min(issuedAt + NominalLifetime, SessionMonitor.NextVenueMidnight(VenueTimeZoneId, issuedAt));

    private BrokerSession Issue()
    {
        var now = clock.UtcNow;

        return new BrokerSession
        {
            ConnectorId = BetaFakeConnector.ConnectorId,
            AccountId = "BETA-1",
            AccessToken = "beta-access",
            // No refresh token, and the manifest agrees. A refresh token here would be a
            // capability the connector cannot actually use.
            RefreshToken = null,
            ExpiresAt = ComputeExpiry(now),
            Extras = SessionMonitor.WithIssuedAt(null, now),
        };
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

/// <summary>
/// Beta's symbology, driven by the script master: <c>INFY-EQ</c>, <c>NIFTY26SEPFUT</c>.
///
/// Table-driven with NO structural fallback, unlike <see cref="AlphaSymbolTranslator"/>. That
/// is the honest model for a broker whose derivative symbols encode an expiry in a format that
/// changes between monthly and weekly contracts: a structural guess would be right most of the
/// time, and a symbol that is right most of the time is an order on the wrong contract the
/// rest of the time.
/// </summary>
public sealed class BetaSymbolTranslator : ISymbolTranslator
{
    private readonly Dictionary<InstrumentKey, string> _toNative = [];
    private readonly Dictionary<string, InstrumentKey> _toCanonical = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the translation tables from a script master.</summary>
    public BetaSymbolTranslator(IReadOnlyList<InstrumentDefinition> universe)
    {
        ArgumentNullException.ThrowIfNull(universe);

        foreach (var definition in universe)
        {
            var native = Encode(definition.Key);
            _toNative[definition.Key] = native;
            _toCanonical[native] = definition.Key;
        }
    }

    /// <inheritdoc />
    public Result<string> ToNative(InstrumentKey key) =>
        _toNative.TryGetValue(key, out var native)
            ? native
            : Result<string>.Failure(ConnectorErrors.InstrumentNotFound(key));

    /// <inheritdoc />
    public Result<InstrumentKey> ToCanonical(string nativeSymbol, string? nativeExchange = null)
    {
        if (string.IsNullOrWhiteSpace(nativeSymbol))
        {
            return Result<InstrumentKey>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                "An empty native symbol cannot be resolved."));
        }

        return _toCanonical.TryGetValue(nativeSymbol.Trim(), out var key)
            ? key
            : Result<InstrumentKey>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                $"'{nativeSymbol}' is not in Beta's script master."));
    }

    private static string Encode(InstrumentKey key) => key.AssetClass switch
    {
        AssetClass.Future when key.Expiry is { } expiry =>
            key.Symbol + (expiry.Year % 100).ToString("D2", CultureInfo.InvariantCulture)
            + Month(expiry.Month) + "FUT",
        _ => key.Symbol + "-EQ",
    };

    private static string Month(int month) => month switch
    {
        1 => "JAN",
        2 => "FEB",
        3 => "MAR",
        4 => "APR",
        5 => "MAY",
        6 => "JUN",
        7 => "JUL",
        8 => "AUG",
        9 => "SEP",
        10 => "OCT",
        11 => "NOV",
        12 => "DEC",
        _ => "XXX",
    };
}
