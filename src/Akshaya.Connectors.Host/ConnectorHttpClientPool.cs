using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;

namespace Akshaya.Connectors.Host;

/// <summary>
/// The host's connection pool: one <see cref="SocketsHttpHandler"/> per connector id, shared by
/// every activation of that connector for the life of the process.
///
/// ═══════════════════ WHY THIS EXISTS, AND WHY IT IS SHAPED THIS WAY ═══════════════════
///
/// Connectors are REQUEST-SCOPED (see BrokerLinkResolver). Without this type, every connector
/// built its own <c>HttpClient</c> in its <c>Create</c> method and disposed it when the request
/// ended — which also tore down that client's connection pool. The next request therefore paid
/// a fresh DNS lookup, TCP handshake and TLS handshake to the broker before it could send a
/// single byte. On a dashboard that fans out to several brokers, that handshake WAS the loading
/// indicator: it is the one cost that is paid on every call and is entirely avoidable.
///
/// THE HANDLER IS SHARED; THE CLIENT IS NOT. This distinction is load-bearing and is a
/// SECURITY property, not a performance one. Connectors write per-session credentials into
/// <c>HttpClient.DefaultRequestHeaders</c> — an access token, an API key, a session cookie — so
/// a shared client instance would let one user's activation overwrite another's bearer token
/// and send a trader's order under someone else's credentials. A fresh <see cref="HttpClient"/>
/// per activation costs one small allocation and carries its own headers, base address and
/// timeout; the handler underneath it carries the sockets, which is the part worth keeping.
///
/// Every client is created with <c>disposeHandler: false</c>, so a connector that disposes its
/// client — several do, and they are right to — returns its connections to the pool instead of
/// closing them.
/// ══════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class ConnectorHttpClientPool : IDisposable
{
    private readonly ConcurrentDictionary<string, SocketsHttpHandler> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConnectorHttpPoolOptions _options;
    private bool _disposed;

    public ConnectorHttpClientPool(IOptions<ConnectorHostOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.HttpPool;
    }

    /// <summary>Test and composition-root seam: a pool with explicit options and no host wiring.</summary>
    public ConnectorHttpClientPool(ConnectorHttpPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// A client for one connector, over that connector's shared pool.
    ///
    /// The caller owns the returned client and may set its base address, timeout and default
    /// headers freely: it is theirs alone. Disposing it is optional and harmless.
    /// </summary>
    public HttpClient Create(string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var handler = _handlers.GetOrAdd(connectorId, _ => CreateHandler());
        return new HttpClient(handler, disposeHandler: false);
    }

    /// <summary>The delegate shape <see cref="Akshaya.Connectors.Sdk.ConnectorActivationContext"/> expects.</summary>
    public Func<string, HttpClient> AsFactory() => Create;

    private SocketsHttpHandler CreateHandler() => new()
    {
        // Recycle connections on a schedule even while they are in use, so a broker that moves
        // behind DNS is followed without a restart. This is the one thing a raw, long-lived
        // HttpClient gets wrong and IHttpClientFactory exists to fix.
        PooledConnectionLifetime = _options.PooledConnectionLifetime,
        PooledConnectionIdleTimeout = _options.PooledConnectionIdleTimeout,

        // The cap is per broker host, not per process: one broker saturating its pool must not
        // queue calls to a different broker.
        MaxConnectionsPerServer = _options.MaxConnectionsPerServer,

        // A connect that has not completed in this long is a broker that is down. Without it,
        // the OS-level timeout is tens of seconds and a request hangs far past the point the
        // trader has given up.
        ConnectTimeout = _options.ConnectTimeout,

        // Several brokers serve their instrument master gzipped and a few return Brotli. The
        // connectors that own their own client already ask for this; asking for it here keeps
        // their behaviour identical when they are handed a pooled one instead.
        AutomaticDecompression = DecompressionMethods.GZip
                                 | DecompressionMethods.Deflate
                                 | DecompressionMethods.Brotli,

        // HTTP/2 multiplexes, so a burst of concurrent calls on one link shares one connection
        // rather than opening several. Allowing more than one lifts the per-connection stream
        // cap that brokers set conservatively.
        EnableMultipleHttp2Connections = true,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var handler in _handlers.Values)
        {
            handler.Dispose();
        }

        _handlers.Clear();
    }
}

/// <summary>Tuning for <see cref="ConnectorHttpClientPool"/>. The defaults suit a broker API over the public internet.</summary>
public sealed class ConnectorHttpPoolOptions
{
    /// <summary>
    /// How long a pooled connection is reused before it is retired and replaced. Two minutes is
    /// the usual <c>IHttpClientFactory</c> value and the same reasoning applies: long enough
    /// that the handshake cost is amortised across hundreds of calls, short enough that a DNS
    /// change is picked up without a deploy.
    /// </summary>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long an unused connection is held open. Covers the gap between two dashboard refreshes.</summary>
    public TimeSpan PooledConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Concurrent connections per broker host. Comfortably above the fan-out any single screen
    /// produces, and well under what a broker would read as abuse — their own rate limiter,
    /// which the manifest declares and <c>ConnectorRateLimiter</c> enforces, is the real cap.
    /// </summary>
    public int MaxConnectionsPerServer { get; set; } = 32;

    /// <summary>Ceiling on establishing a connection, as distinct from the per-request timeout.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
