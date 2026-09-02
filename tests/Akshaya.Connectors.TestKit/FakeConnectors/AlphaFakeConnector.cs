using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Akshaya.Connectors.TestKit.FakeConnectors;

/// <summary>
/// A fictional international broker: OAuth2, multi-currency (USD and SGD), fractional
/// quantities, a live feed, bracket orders and a refreshable session.
///
/// Alpha exists to be as UNLIKE <see cref="BetaFakeConnector"/> as the contract permits. Every
/// axis on which real brokers differ is set to the opposite value here, and both pass the same
/// conformance suite. If a change to the abstraction quietly assumes a single currency, whole
/// quantities, or that every broker has a socket, one of these two stops compiling or stops
/// passing — which is the entire point of maintaining two fakes rather than one.
/// </summary>
public sealed class AlphaFakeConnector : ConnectorBase
{
    /// <summary>Connector id, matching the manifest.</summary>
    public const string ConnectorId = "alpha";

    private readonly FakeBrokerStream _stream;

    /// <summary>Creates the connector.</summary>
    public AlphaFakeConnector(
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

        Symbols = new AlphaSymbolTranslator();
        AuthFacet = new AlphaAuth(Clock);
        _stream = new FakeBrokerStream(manifest.MarketData.MaxStreamSubscriptions);
    }

    /// <summary>The in-memory book, exposed so a test can arm a timeout on it.</summary>
    public FakeBrokerBook Book { get; }

    /// <summary>Alpha's symbology: <c>EXCHANGE:SYMBOL:KIND</c>, structural rather than table-driven.</summary>
    public ISymbolTranslator Symbols { get; }

    /// <summary>The socket, exposed so the suite can assert it leaks no upstream subscriptions.</summary>
    public FakeBrokerStream FakeStream => _stream;

    private AlphaAuth AuthFacet { get; }

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
    /// <remarks>Non-null, because the manifest declares streaming. The suite checks the pairing.</remarks>
    public override IConnectorStream? Stream => _stream;

    /// <summary>
    /// Alpha's error vocabulary. Numeric-ish prefixed codes, plus the SDK's generic phrase
    /// table for the messages that carry no code at all.
    /// </summary>
    public static IVendorErrorMapper CreateErrorMapper() => new DefaultVendorErrorMapper(
        vendorCodes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AUTH_401"] = ConnectorErrorCodes.SessionExpired,
            ["AUTH_403"] = ConnectorErrorCodes.ReauthRequired,
            ["RATE_429"] = ConnectorErrorCodes.RateLimited,
            ["FUNDS_1001"] = ConnectorErrorCodes.InsufficientFunds,
            ["SYM_404"] = ConnectorErrorCodes.InstrumentNotFound,
            ["ORD_409"] = ConnectorErrorCodes.OrderRejected,
        },
        messagePhrases:
        [
            ("buying power", ConnectorErrorCodes.InsufficientFunds),
        ]);

    /// <summary>The instruments Alpha trades. Two currencies on purpose.</summary>
    public static IReadOnlyList<InstrumentDefinition> Universe() =>
    [
        new InstrumentDefinition
        {
            Key = new InstrumentKey(Venue.Nasdaq, "AAPL", AssetClass.Equity),
            Name = "Apple Inc.",
            Currency = Currency.Usd,
            Isin = "US0378331005",
            LotSize = 1m,
            TickSize = 0.01m,
        },
        new InstrumentDefinition
        {
            Key = new InstrumentKey(Venue.Nasdaq, "QQQ", AssetClass.Etf),
            Name = "Invesco QQQ Trust",
            Currency = Currency.Usd,
            LotSize = 1m,
            TickSize = 0.01m,
        },
        new InstrumentDefinition
        {
            Key = new InstrumentKey(Venue.Sgx, "D05", AssetClass.Equity),
            Name = "DBS Group Holdings",
            Currency = Currency.Sgd,
            LotSize = 1m,
            TickSize = 0.01m,
        },
    ];

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        await base.DisposeAsync();
    }

    private static decimal ReferencePrice(string symbol) => symbol switch
    {
        "AAPL" => 225.50m,
        "QQQ" => 480.25m,
        "D05" => 38.40m,
        _ => 100m,
    };
}

/// <summary>
/// Alpha's OAuth2 handshake: begin returns a redirect, continue exchanges the code.
///
/// A refresh really works here, because the manifest says <c>refreshSupported: true</c> and the
/// conformance suite checks the two agree. A connector whose manifest promises refresh and
/// whose code declines it puts the session monitor into a loop that never resolves and never
/// prompts the user — the failure is a session that quietly stops working with no dialog.
/// </summary>
public sealed class AlphaAuth(IClock clock) : IConnectorAuth
{
    /// <summary>Where the wizard sends the user. Fictional; nothing is ever fetched.</summary>
    public const string AuthorizeUrl = "https://auth.alpha.example/oauth2/authorize";

    /// <inheritdoc />
    public Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var clientId = context.Credentials.GetOrDefault("client_id");
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Task.FromResult(Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.InvalidCredentials,
                "Alpha needs a client id before it can start an authorisation.")));
        }

        // A fixed state value keeps the fake deterministic. A real connector generates a
        // cryptographically random one and checks it on the callback; substituting one here
        // would make every test's expected redirect different from the last run's.
        var state = "alpha-state";

        return Task.FromResult(Result<AuthStep>.Success(new AuthStep.RedirectRequired(
            $"{AuthorizeUrl}?client_id={Uri.EscapeDataString(clientId)}&state={state}",
            state)));
    }

    /// <inheritdoc />
    public Task<Result<AuthStep>> ContinueAsync(
        AuthContext context,
        string response,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(response))
        {
            return Task.FromResult(Result<AuthStep>.Failure(new Error(
                ConnectorErrorCodes.ChallengeFailed,
                "No authorisation code was supplied.")));
        }

        return Task.FromResult(Result<AuthStep>.Success(
            new AuthStep.Completed(Issue("alpha-access-" + response))));
    }

    /// <inheritdoc />
    public Task<Result<BrokerSession>> RefreshAsync(BrokerSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            // The manifest declares refresh, but a session with no refresh token cannot use
            // it. ReauthRequired, not NotSupported: the capability exists, this session lacks
            // the material — and the UI's response to those two is different.
            return Task.FromResult(Result<BrokerSession>.Failure(
                ConnectorErrors.ReauthRequired(AlphaFakeConnector.ConnectorId)));
        }

        return Task.FromResult(Result<BrokerSession>.Success(Issue("alpha-access-refreshed")));
    }

    /// <inheritdoc />
    public Task<Result> RevokeAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    /// <inheritdoc />
    public Task<Result> KeepAliveAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    private BrokerSession Issue(string accessToken)
    {
        var now = clock.UtcNow;

        return new BrokerSession
        {
            ConnectorId = AlphaFakeConnector.ConnectorId,
            AccountId = "ALPHA-1",
            AccessToken = accessToken,
            RefreshToken = "alpha-refresh",
            ExpiresAt = now + TimeSpan.FromHours(8),
            Extras = SessionMonitor.WithIssuedAt(null, now),
        };
    }
}

/// <summary>
/// Alpha's symbology, computed rather than looked up: <c>NASDAQ:AAPL:EQ</c>.
///
/// Structural on purpose, so the round-trip test is asserting a real property of the mapping
/// rather than the reflexivity of a dictionary. An exchange or instrument kind it does not
/// know is <see cref="ConnectorErrorCodes.InstrumentNotFound"/> — never a guess, because a
/// guessed symbol is an order on the wrong instrument.
/// </summary>
public sealed class AlphaSymbolTranslator : ISymbolTranslator
{
    private static readonly (Venue Venue, string Native)[] Exchanges =
    [
        (Venue.Nasdaq, "NASDAQ"),
        (Venue.Sgx, "SGX"),
    ];

    private static readonly (AssetClass Class, string Native)[] Kinds =
    [
        (AssetClass.Equity, "EQ"),
        (AssetClass.Etf, "ETF"),
    ];

    /// <inheritdoc />
    public Result<string> ToNative(InstrumentKey key)
    {
        string? exchange = null;
        foreach (var (venue, native) in Exchanges)
        {
            if (venue == key.Venue)
            {
                exchange = native;
                break;
            }
        }

        if (exchange is null)
        {
            return Result<string>.Failure(ConnectorErrors.InstrumentNotFound(key));
        }

        string? kind = null;
        foreach (var (assetClass, native) in Kinds)
        {
            if (assetClass == key.AssetClass)
            {
                kind = native;
                break;
            }
        }

        if (kind is null || string.IsNullOrWhiteSpace(key.Symbol))
        {
            return Result<string>.Failure(ConnectorErrors.InstrumentNotFound(key));
        }

        return $"{exchange}:{key.Symbol}:{kind}";
    }

    /// <inheritdoc />
    public Result<InstrumentKey> ToCanonical(string nativeSymbol, string? nativeExchange = null)
    {
        if (string.IsNullOrWhiteSpace(nativeSymbol))
        {
            return Result<InstrumentKey>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                "An empty native symbol cannot be resolved."));
        }

        var parts = nativeSymbol.Split(':');
        var exchangeName = nativeExchange ?? (parts.Length == 3 ? parts[0] : null);

        if (parts.Length != 3 || string.IsNullOrWhiteSpace(exchangeName))
        {
            return Result<InstrumentKey>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                $"'{nativeSymbol}' is not an Alpha symbol. Expected EXCHANGE:SYMBOL:KIND."));
        }

        Venue? venue = null;
        foreach (var (candidate, native) in Exchanges)
        {
            if (string.Equals(native, exchangeName, StringComparison.OrdinalIgnoreCase))
            {
                venue = candidate;
                break;
            }
        }

        AssetClass? assetClass = null;
        foreach (var (candidate, native) in Kinds)
        {
            if (string.Equals(native, parts[2], StringComparison.OrdinalIgnoreCase))
            {
                assetClass = candidate;
                break;
            }
        }

        if (venue is null || assetClass is null)
        {
            return Result<InstrumentKey>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                $"Alpha does not trade '{nativeSymbol}'."));
        }

        return new InstrumentKey(venue.Value, parts[1], assetClass.Value);
    }
}
