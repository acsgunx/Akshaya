using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connectors.Sdk;

/// <summary>
/// The base a C# connector author starts from.
///
/// It supplies the parts every connector needs identically — manifest, session, logger,
/// clock, a session monitor wired to the manifest's auth spec, a default health check — and
/// defaults every facet to a <see cref="NotSupportedFacets"/> implementation so a connector
/// only overrides what its broker can actually do. Nothing here is required: a connector may
/// implement <see cref="IBrokerConnector"/> directly, and out-of-process connectors in other
/// languages obviously do.
///
/// What this class deliberately does NOT do: rate limiting, retries, tracing and audit. Those
/// are the host's decorators. A connector that implements its own retry loop defeats the
/// host's policy and, worse, can retry an order placement. Do not.
/// </summary>
public abstract class ConnectorBase : IBrokerConnector
{
    private BrokerSession? _session;

    protected ConnectorBase(
        ConnectorManifest manifest,
        BrokerSession? session,
        ILogger logger,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(logger);

        Manifest = manifest;
        _session = session;
        Logger = logger;
        Clock = clock ?? SystemClock.Instance;
        SessionMonitor = new SessionMonitor(manifest.Auth, Clock);
    }

    public ConnectorManifest Manifest { get; }

    /// <summary>
    /// The session this instance is bound to, or null for the unauthenticated instance used
    /// during the login handshake. Mutable because <see cref="IConnectorAuth.RefreshAsync"/>
    /// replaces it in place — the caller holding this connector must keep working across a
    /// refresh without re-resolving from the factory.
    /// </summary>
    protected BrokerSession? Session => Volatile.Read(ref _session);

    protected ILogger Logger { get; }

    protected IClock Clock { get; }

    /// <summary>Wired to <c>Manifest.Auth</c>, so venue-midnight expiry is handled for free.</summary>
    protected SessionMonitor SessionMonitor { get; }

    /// <summary>Every connector must authenticate somehow; there is no sensible default.</summary>
    public abstract IConnectorAuth Auth { get; }

    public virtual IConnectorOrders Orders => NotSupportedOrders.Instance;

    public virtual IConnectorPortfolio Portfolio => NotSupportedPortfolio.Instance;

    public virtual IConnectorMarketData MarketData => NotSupportedMarketData.Instance;

    public virtual IConnectorReference Reference => NotSupportedReference.Instance;

    /// <summary>Null by default: most brokers have no feed, and the contract says callers handle null.</summary>
    public virtual IConnectorStream? Stream => null;

    /// <summary>
    /// Default health: session validity plus stream state, no network call. Connectors whose
    /// broker exposes a cheap ping should override and fold the round-trip latency in — but
    /// must keep it cheap, because the UI polls this.
    /// </summary>
    public virtual Task<Result<ConnectorHealth>> CheckHealthAsync(CancellationToken ct = default)
    {
        var streamState = Stream?.State ?? StreamState.Disconnected;
        var session = Session;

        if (session is null)
        {
            return Task.FromResult<Result<ConnectorHealth>>(new ConnectorHealth
            {
                IsHealthy = false,
                StreamState = streamState,
                SessionValid = false,
                Detail = "No session is bound to this connector instance.",
            });
        }

        var status = SessionMonitor.Evaluate(session);

        return Task.FromResult<Result<ConnectorHealth>>(new ConnectorHealth
        {
            IsHealthy = status.IsUsable,
            StreamState = streamState,
            SessionValid = status.IsUsable,
            SessionExpiresAt = status.EffectiveExpiresAt,
            // Gateway liveness is the supervisor's business, not the connector's. Anything
            // hosted in-process is trivially "running"; gateway connectors override this.
            GatewayRunning = Manifest.Hosting != ConnectorHosting.Gateway,
            Detail = status.State switch
            {
                SessionState.Expired => "Session has expired; re-authentication is required.",
                SessionState.ExpiringSoon =>
                    $"Session expires in {status.TimeRemaining:hh\\:mm\\:ss}.",
                _ => status.Detail,
            },
        });
    }

    /// <summary>
    /// Replaces the bound session after a successful refresh or re-auth. Uses a volatile write
    /// because the host's keepalive timer refreshes on a different thread from the request
    /// that is reading it.
    /// </summary>
    protected void UpdateSession(BrokerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Volatile.Write(ref _session, session);
    }

    /// <summary>
    /// The gate every non-auth broker call goes through.
    ///
    /// It distinguishes the two failures the UI must handle differently:
    /// <see cref="ConnectorErrorCodes.SessionExpired"/> when there is no session at all
    /// (a programming error upstream, or the unauthenticated instance being misused), and
    /// <see cref="ConnectorErrorCodes.ReauthRequired"/> when a real session has died — the
    /// second is a "sign in again" prompt, the first is a bug report.
    /// </summary>
    protected Result<BrokerSession> RequireSession()
    {
        var session = Session;
        if (session is null)
        {
            return ConnectorErrors.SessionExpired(Manifest.Id);
        }

        return SessionMonitor.IsExpired(session)
            ? ConnectorErrors.ReauthRequired(Manifest.Id)
            : Result<BrokerSession>.Success(session);
    }

    /// <summary>Declines a capability this broker does not have. See <see cref="NotSupportedFacets"/>.</summary>
    protected static Result<T> NotSupported<T>(string capability) =>
        Result<T>.Failure(ConnectorErrors.NotSupported(capability));

    /// <summary>Non-generic companion to <see cref="NotSupported{T}"/>.</summary>
    protected static Result NotSupported(string capability) =>
        Result.Failure(ConnectorErrors.NotSupported(capability));

    /// <summary>Async form, to keep declining methods to a single expression.</summary>
    protected static Task<Result<T>> NotSupportedAsync<T>(string capability) =>
        NotSupportedFacets.DeclineAsync<T>(capability);

    /// <summary>Async non-generic form.</summary>
    protected static Task<Result> NotSupportedAsync(string capability) =>
        NotSupportedFacets.DeclineAsync(capability);

    /// <summary>
    /// Guards a request against what the manifest CLAIMS, before it reaches the broker. This
    /// is the enforcement half of the manifest contract: the conformance suite checks the
    /// connector honours its manifest, and this check makes the connector honour it at runtime
    /// too, so a manifest edit cannot quietly enable an unsupported order type.
    /// </summary>
    protected Result ValidateAgainstManifest(PlaceOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var orders = Manifest.Orders;

        if (!orders.Supports(request.OrderType))
        {
            return ConnectorErrors.NotSupported($"{request.OrderType} orders");
        }

        if (!orders.Supports(request.TimeInForce))
        {
            return ConnectorErrors.NotSupported($"{request.TimeInForce} time-in-force");
        }

        if (!orders.Supports(request.PositionEffect))
        {
            return ConnectorErrors.NotSupported($"the {request.PositionEffect} product type");
        }

        if (!orders.Varieties.Contains(request.Variety))
        {
            return ConnectorErrors.NotSupported($"{request.Variety} orders");
        }

        if (request.Quantity.IsFractional && !orders.FractionalQuantity)
        {
            return ConnectorErrors.NotSupported("fractional quantities");
        }

        if (!Manifest.SupportsVenue(request.Instrument.Venue))
        {
            return ConnectorErrors.InstrumentNotFound(request.Instrument);
        }

        if (!Manifest.SupportsAssetClass(request.Instrument.AssetClass))
        {
            return ConnectorErrors.InstrumentNotFound(request.Instrument);
        }

        // Currency check: an order priced in a currency the broker does not settle is a
        // cross-border mistake worth catching here rather than at the venue.
        if (request.LimitPrice is { } limit
            && !Manifest.Currencies.Contains(limit.Currency.Code, StringComparer.OrdinalIgnoreCase))
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"This broker does not deal in {limit.Currency}.");
        }

        // Price presence rules are universal across venues, so they live here rather than
        // being re-derived in every connector.
        return request.OrderType switch
        {
            OrderType.Limit or OrderType.StopLimit when request.LimitPrice is null =>
                new Error(ConnectorErrorCodes.InvalidRequest, "A limit price is required for this order type."),
            OrderType.Stop or OrderType.StopLimit or OrderType.MarketIfTouched or OrderType.TrailingStop
                when request.TriggerPrice is null =>
                new Error(ConnectorErrorCodes.InvalidRequest, "A trigger price is required for this order type."),
            _ when request.TimeInForce == TimeInForce.Gtd && request.GoodTillDate is null =>
                new Error(ConnectorErrorCodes.InvalidRequest, "A good-till date is required for GTD orders."),
            _ when request.Quantity <= Quantity.Zero =>
                new Error(ConnectorErrorCodes.InvalidRequest, "Quantity must be positive."),
            _ => Result.Success(),
        };
    }

    /// <summary>
    /// Override to release broker resources (sockets, gateway handles). Always call
    /// <c>base.DisposeAsync()</c>.
    /// </summary>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
