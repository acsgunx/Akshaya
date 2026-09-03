using Akshaya.SharedKernel;
using Microsoft.AspNetCore.SignalR;

namespace Akshaya.Api.Hubs;

/// <summary>
/// The real-time surface: instrument subscriptions in, ticks and order/execution updates out.
///
/// This class is deliberately thin. Every piece of state — who is subscribed to what, the one
/// upstream connector per broker link, the conflation that protects it — lives in
/// <see cref="SubscriptionRegistry"/>, which is a singleton and therefore outlives any one
/// connection. A hub instance, by contrast, is created fresh per invocation by the SignalR
/// runtime and must never hold state itself; anything stored on `this` would vanish before the
/// next message from the same client arrived.
/// </summary>
public sealed class MarketDataHub(SubscriptionRegistry registry, ILogger<MarketDataHub> logger) : Hub
{
    private readonly SubscriptionRegistry _registry = registry;
    private readonly ILogger<MarketDataHub> _logger = logger;

    /// <summary>
    /// Joins the caller to their own order/execution channel.
    ///
    /// A hub connection cannot use <c>ICurrentUserAccessor</c> via <c>IHttpContextAccessor</c>
    /// the way an HTTP endpoint does — SignalR invocations do not run inside the original
    /// request's ambient context once the connection is up — so this reads the authenticated
    /// principal off <c>HubCallerContext.User</c>, which SignalR carries over from the
    /// connection-upgrade request. Both places resolve it through
    /// <c>AkshayaIdentity.Resolve</c> so they cannot disagree about who the caller is.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var (tenantId, userId, _) = AkshayaIdentity.Resolve(Context.User);
        _logger.LogDebug("Market-data hub connection {ConnectionId} established for {TenantId}/{UserId}.", Context.ConnectionId, tenantId, userId);

        await Groups.AddToGroupAsync(Context.ConnectionId, SubscriptionRegistry.UserGroup(tenantId, userId));
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Releases everything this connection was subscribed to. Deliberately unconditional — an
    /// abrupt disconnect (a closed laptop lid, a dropped Wi-Fi) must clean up exactly as
    /// thoroughly as a polite <see cref="Unsubscribe"/> call, or a broker link accumulates
    /// phantom subscribers that never get unsubscribed upstream.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _registry.HandleDisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribes this connection to a set of instruments on one broker link.
    ///
    /// Instruments are canonical instrument-key strings (e.g. <c>XNSE:INFY:Equity</c>) — the
    /// same wire format <c>InstrumentKeyJsonConverter</c> uses over HTTP, so a client never has
    /// to know two representations of the same key.
    /// </summary>
    public async Task Subscribe(string brokerLinkId, string[] instruments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerLinkId);
        ArgumentNullException.ThrowIfNull(instruments);

        var (tenantId, _, _) = AkshayaIdentity.Resolve(Context.User);
        var keys = ParseInstruments(instruments);

        var result = await _registry.SubscribeAsync(Context.ConnectionId, tenantId, brokerLinkId, keys, Context.ConnectionAborted);
        if (result.IsFailure)
        {
            // HubException is the one exception type whose message SignalR forwards to the
            // client verbatim; anything else arrives as an opaque "an error occurred".
            throw new HubException(result.Error.Message);
        }
    }

    public async Task Unsubscribe(string brokerLinkId, string[] instruments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerLinkId);
        ArgumentNullException.ThrowIfNull(instruments);

        var keys = ParseInstruments(instruments);
        await _registry.UnsubscribeAsync(Context.ConnectionId, brokerLinkId, keys, Context.ConnectionAborted);
    }

    private static IReadOnlyList<InstrumentKey> ParseInstruments(IReadOnlyList<string> raw)
    {
        var keys = new List<InstrumentKey>(raw.Count);
        foreach (var value in raw)
        {
            if (!InstrumentKey.TryParse(value, out var key))
            {
                throw new HubException($"'{value}' is not a valid instrument key.");
            }

            keys.Add(key);
        }

        return keys;
    }
}
