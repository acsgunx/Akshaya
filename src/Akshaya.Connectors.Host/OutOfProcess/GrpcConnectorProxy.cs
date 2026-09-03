using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connectors.Host.OutOfProcess;

/// <summary>
/// An <see cref="IBrokerConnector"/> whose implementation lives in another process.
///
/// This is the type that makes "any broker, in any language" true rather than aspirational. Some
/// brokers cannot be reached comfortably from C#: a vendor ships a Python-only SDK, or speaks a
/// protocol nobody wants to reimplement, or requires a daemon with its own client library. Those
/// connectors run as their own process or container, speak the RPC contract in
/// <c>broker_connector.proto</c>, and arrive here as an ordinary connector.
///
/// The core cannot tell the difference, and that is the whole design: <see cref="ConnectorFactory"/>
/// wraps this in the same decorator chain — audit, tracing, resilience, rate limiting — as an
/// in-process connector, and every caller above sees the same interface.
///
/// See docs/adr/0006-three-hosting-models.md.
/// </summary>
public sealed class GrpcConnectorProxy : IBrokerConnector
{
    private readonly IRemoteConnectorTransport _transport;
    private readonly ILogger _logger;
    private readonly BrokerSession? _session;
    private bool _disposed;

    public GrpcConnectorProxy(
        ConnectorManifest manifest,
        Uri address,
        BrokerSession? session,
        ILogger<GrpcConnectorProxy> logger,
        IRemoteConnectorTransportFactory? transportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(logger);

        Manifest = manifest;
        _session = session;
        _logger = logger;

        // Defaulting rather than requiring a factory keeps a deployment without the gRPC binding
        // installed in a diagnosable state: the connector appears, is visibly unhealthy, and
        // says why. Failing construction instead would take the whole catalogue down over one
        // misconfigured connector.
        _transport = (transportFactory ?? new UnconfiguredRemoteTransportFactory())
            .Create(address, manifest);

        Auth = new RemoteAuth(_transport, session);
        Orders = new RemoteOrders(_transport, session);
        Portfolio = new RemotePortfolio(_transport, session);
        MarketData = new RemoteMarketData(_transport, session);
        Reference = new RemoteReference(_transport, session);

        // Honour the manifest: a remote connector that does not declare streaming gets a null
        // Stream, exactly as an in-process one would.
        Stream = manifest.MarketData.Streaming
            ? new RemoteStream(_transport, session)
            : null;
    }

    public ConnectorManifest Manifest { get; }

    public IConnectorAuth Auth { get; }

    public IConnectorOrders Orders { get; }

    public IConnectorPortfolio Portfolio { get; }

    public IConnectorMarketData MarketData { get; }

    public IConnectorReference Reference { get; }

    public IConnectorStream? Stream { get; }

    public async Task<Result<ConnectorHealth>> CheckHealthAsync(CancellationToken ct = default)
    {
        var health = await _transport.HealthAsync(ct).ConfigureAwait(false);

        if (health.IsFailure)
        {
            _logger.LogWarning(
                "Health check failed for out-of-process connector {ConnectorId} at {Address}: {Error}",
                Manifest.Id, _transport.Address, health.Error);
        }

        return health;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _transport.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // -----------------------------------------------------------------------------------------
    // Facets. Each is a thin translation from a method call to a named RPC. Deliberately dull:
    // any cleverness here would be behaviour that an in-process connector does not have, which
    // would make the two hosting models observably different.
    // -----------------------------------------------------------------------------------------

    private sealed class RemoteAuth(IRemoteConnectorTransport transport, BrokerSession? session)
        : IConnectorAuth
    {
        public Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default) =>
            transport.InvokeAsync<AuthContext, AuthStep>("BeginAuth", context, session, ct);

        public Task<Result<AuthStep>> ContinueAsync(
            AuthContext context, string response, CancellationToken ct = default) =>
            transport.InvokeAsync<ContinueAuthPayload, AuthStep>(
                "ContinueAuth", new ContinueAuthPayload(context, response), session, ct);

        public Task<Result<BrokerSession>> RefreshAsync(
            BrokerSession current, CancellationToken ct = default) =>
            transport.InvokeAsync<BrokerSession, BrokerSession>("RefreshSession", current, session, ct);

        public async Task<Result> RevokeAsync(BrokerSession current, CancellationToken ct = default)
        {
            var result = await transport
                .InvokeAsync<BrokerSession, bool>("RevokeSession", current, session, ct)
                .ConfigureAwait(false);

            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }

        public async Task<Result> KeepAliveAsync(BrokerSession current, CancellationToken ct = default)
        {
            var result = await transport
                .InvokeAsync<BrokerSession, bool>("KeepAlive", current, session, ct)
                .ConfigureAwait(false);

            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }
    }

    /// <summary>Carries the two arguments of ContinueAsync as one serialisable payload.</summary>
    private sealed record ContinueAuthPayload(AuthContext Context, string Response);

    private sealed class RemoteOrders(IRemoteConnectorTransport transport, BrokerSession? session)
        : IConnectorOrders
    {
        public Task<Result<OrderAck>> PlaceAsync(
            PlaceOrderRequest request, CancellationToken ct = default) =>
            transport.InvokeAsync<PlaceOrderRequest, OrderAck>("PlaceOrder", request, session, ct);

        public Task<Result<OrderAck>> ModifyAsync(
            ModifyOrderRequest request, CancellationToken ct = default) =>
            transport.InvokeAsync<ModifyOrderRequest, OrderAck>("ModifyOrder", request, session, ct);

        public Task<Result<OrderAck>> CancelAsync(
            string brokerOrderId, CancellationToken ct = default) =>
            transport.InvokeAsync<string, OrderAck>("CancelOrder", brokerOrderId, session, ct);

        public Task<Result<int>> CancelAllAsync(CancellationToken ct = default) =>
            transport.InvokeAsync<object?, int>("CancelAllOrders", null, session, ct);

        public Task<Result<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(
            OrderQuery query, CancellationToken ct = default) =>
            transport.InvokeAsync<OrderQuery, IReadOnlyList<BrokerOrder>>(
                "GetOrders", query, session, ct);

        public Task<Result<BrokerOrder>> GetOrderAsync(
            string brokerOrderId, CancellationToken ct = default) =>
            transport.InvokeAsync<string, BrokerOrder>("GetOrder", brokerOrderId, session, ct);

        public Task<Result<IReadOnlyList<BrokerTrade>>> GetTradesAsync(
            OrderQuery query, CancellationToken ct = default) =>
            transport.InvokeAsync<OrderQuery, IReadOnlyList<BrokerTrade>>(
                "GetTrades", query, session, ct);

        public Task<Result<IReadOnlyList<OrderAck>>> PlaceBasketAsync(
            IReadOnlyList<PlaceOrderRequest> requests, CancellationToken ct = default) =>
            transport.InvokeAsync<IReadOnlyList<PlaceOrderRequest>, IReadOnlyList<OrderAck>>(
                "PlaceBasket", requests, session, ct);

        public Task<Result<MarginEstimate>> EstimateMarginAsync(
            PlaceOrderRequest request, CancellationToken ct = default) =>
            transport.InvokeAsync<PlaceOrderRequest, MarginEstimate>(
                "EstimateMargin", request, session, ct);

        public Task<Result<ChargesEstimate>> EstimateChargesAsync(
            PlaceOrderRequest request, CancellationToken ct = default) =>
            transport.InvokeAsync<PlaceOrderRequest, ChargesEstimate>(
                "EstimateCharges", request, session, ct);
    }

    private sealed class RemotePortfolio(IRemoteConnectorTransport transport, BrokerSession? session)
        : IConnectorPortfolio
    {
        public Task<Result<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(
            CancellationToken ct = default) =>
            transport.InvokeAsync<object?, IReadOnlyList<BrokerPosition>>(
                "GetPositions", null, session, ct);

        public Task<Result<IReadOnlyList<BrokerHolding>>> GetHoldingsAsync(
            CancellationToken ct = default) =>
            transport.InvokeAsync<object?, IReadOnlyList<BrokerHolding>>(
                "GetHoldings", null, session, ct);

        public Task<Result<IReadOnlyList<BrokerBalance>>> GetBalancesAsync(
            CancellationToken ct = default) =>
            transport.InvokeAsync<object?, IReadOnlyList<BrokerBalance>>(
                "GetBalances", null, session, ct);

        public async Task<Result> ConvertPositionAsync(
            ConvertPositionRequest request,
            CancellationToken ct = default)
        {
            var result = await transport
                .InvokeAsync<ConvertPositionRequest, object?>("ConvertPosition", request, session, ct)
                .ConfigureAwait(false);

            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }
    }

    private sealed class RemoteMarketData(IRemoteConnectorTransport transport, BrokerSession? session)
        : IConnectorMarketData
    {
        public Task<Result<Quote>> GetQuoteAsync(
            InstrumentKey instrument, CancellationToken ct = default) =>
            transport.InvokeAsync<InstrumentKey, Quote>("GetQuote", instrument, session, ct);

        public Task<Result<IReadOnlyDictionary<InstrumentKey, Money>>> GetLtpAsync(
            IReadOnlyCollection<InstrumentKey> instruments, CancellationToken ct = default) =>
            transport.InvokeAsync<IReadOnlyCollection<InstrumentKey>,
                IReadOnlyDictionary<InstrumentKey, Money>>("GetLtp", instruments, session, ct);

        public Task<Result<IReadOnlyDictionary<InstrumentKey, Quote>>> GetQuotesAsync(
            IReadOnlyCollection<InstrumentKey> instruments, CancellationToken ct = default) =>
            transport.InvokeAsync<IReadOnlyCollection<InstrumentKey>,
                IReadOnlyDictionary<InstrumentKey, Quote>>("GetQuotes", instruments, session, ct);

        public Task<Result<CandleSeries>> GetHistoricalAsync(
            HistoryRequest request, CancellationToken ct = default) =>
            transport.InvokeAsync<HistoryRequest, CandleSeries>("GetHistorical", request, session, ct);

        public Task<Result<MarketDepth>> GetDepthAsync(
            InstrumentKey instrument, CancellationToken ct = default) =>
            transport.InvokeAsync<InstrumentKey, MarketDepth>("GetDepth", instrument, session, ct);

        public Task<Result<OptionChain>> GetOptionChainAsync(
            InstrumentKey underlying, DateOnly expiry, CancellationToken ct = default) =>
            transport.InvokeAsync<OptionChainQuery, OptionChain>(
                "GetOptionChain", new OptionChainQuery(underlying, expiry), session, ct);
    }

    private sealed record OptionChainQuery(InstrumentKey Underlying, DateOnly Expiry);

    private sealed record InstrumentQuery(Venue? Venue, AssetClass? AssetClass);

    private sealed record SearchQuery(string Query, int Limit);

    private sealed class RemoteReference(IRemoteConnectorTransport transport, BrokerSession? session)
        : IConnectorReference
    {
        public async IAsyncEnumerable<InstrumentDefinition> GetInstrumentsAsync(
            Venue? venue = null,
            AssetClass? assetClass = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            // Instrument masters run to hundreds of thousands of rows. This is paged rather than
            // fetched whole so the proxy never has to hold one in memory — the same reason the
            // contract makes this method streaming in the first place.
            var result = await transport
                .InvokeAsync<InstrumentQuery, IReadOnlyList<InstrumentDefinition>>(
                    "GetInstruments", new InstrumentQuery(venue, assetClass), session, ct)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {
                yield break;
            }

            foreach (var definition in result.Value)
            {
                ct.ThrowIfCancellationRequested();
                yield return definition;
            }
        }

        public Task<Result<InstrumentDefinition>> ResolveAsync(
            InstrumentKey key, CancellationToken ct = default) =>
            transport.InvokeAsync<InstrumentKey, InstrumentDefinition>(
                "ResolveInstrument", key, session, ct);

        public Task<Result<IReadOnlyList<InstrumentDefinition>>> SearchAsync(
            string query, int limit = 20, CancellationToken ct = default) =>
            transport.InvokeAsync<SearchQuery, IReadOnlyList<InstrumentDefinition>>(
                "SearchInstruments", new SearchQuery(query, limit), session, ct);
    }

    private sealed class RemoteStream(IRemoteConnectorTransport transport, BrokerSession? session)
        : IConnectorStream
    {
        private readonly HashSet<InstrumentKey> _subscribed = [];
        private readonly Lock _gate = new();

        public StreamState State { get; private set; } = StreamState.Disconnected;

        public async Task<Result> ConnectAsync(CancellationToken ct = default)
        {
            State = StreamState.Connecting;

            var result = await transport
                .InvokeAsync<object?, bool>("StreamConnect", null, session, ct)
                .ConfigureAwait(false);

            State = result.IsSuccess ? StreamState.Connected : StreamState.Disconnected;
            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }

        public async Task<Result> DisconnectAsync(CancellationToken ct = default)
        {
            var result = await transport
                .InvokeAsync<object?, bool>("StreamDisconnect", null, session, ct)
                .ConfigureAwait(false);

            State = StreamState.Disconnected;

            lock (_gate)
            {
                _subscribed.Clear();
            }

            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }

        public async Task<Result> SubscribeAsync(
            IReadOnlyCollection<InstrumentKey> instruments,
            StreamMode mode,
            CancellationToken ct = default)
        {
            var result = await transport
                .InvokeAsync<SubscriptionRequest, bool>(
                    "Subscribe", new SubscriptionRequest(instruments, mode), session, ct)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                // Tracked locally so a reconnect can restore exactly what was subscribed. A
                // reconnect that silently drops subscriptions looks like a quiet market.
                lock (_gate)
                {
                    foreach (var instrument in instruments)
                    {
                        _subscribed.Add(instrument);
                    }
                }
            }

            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }

        public async Task<Result> UnsubscribeAsync(
            IReadOnlyCollection<InstrumentKey> instruments, CancellationToken ct = default)
        {
            var result = await transport
                .InvokeAsync<SubscriptionRequest, bool>(
                    "Unsubscribe", new SubscriptionRequest(instruments, StreamMode.Ltp), session, ct)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                lock (_gate)
                {
                    foreach (var instrument in instruments)
                    {
                        _subscribed.Remove(instrument);
                    }
                }
            }

            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }

        public IAsyncEnumerable<StreamEvent> Events(CancellationToken ct = default)
        {
            InstrumentKey[] snapshot;
            lock (_gate)
            {
                snapshot = [.. _subscribed];
            }

            return transport.StreamAsync("Events", snapshot, StreamMode.Full, session, ct);
        }
    }

    private sealed record SubscriptionRequest(
        IReadOnlyCollection<InstrumentKey> Instruments, StreamMode Mode);
}
