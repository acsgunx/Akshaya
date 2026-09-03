using System.Runtime.CompilerServices;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Sdk.Decorators;

/// <summary>
/// A cross-cutting concern applied to every connector call.
///
/// One method covers the whole surface because <see cref="ConnectorOperation"/> carries the
/// per-call policy. A decorator therefore says what it does ONCE, not once per method, and a
/// new method on a facet cannot be accidentally left un-audited or un-rate-limited.
/// </summary>
public interface IConnectorInterceptor
{
    /// <summary>
    /// Wraps one call. Implementations must invoke <paramref name="next"/> at most once —
    /// except <see cref="ResilienceConnector"/>, which may invoke it repeatedly and only for
    /// operations where <see cref="ConnectorOperation.IsIdempotentRead"/> is true.
    /// </summary>
    Task<Result<T>> InterceptAsync<T>(
        ConnectorOperation operation,
        Func<CancellationToken, Task<Result<T>>> next,
        CancellationToken ct,
        ConnectorCallSubject subject = default);
}

/// <summary>
/// The identifiers a decorator needs about WHAT a call acts on, without knowing the method.
///
/// It exists for one reason: the audit trail must record the
/// <see cref="Abstractions.PlaceOrderRequest.ClientOrderId"/> of a placement that FAILED, and
/// a failure carries no result to read it from. Recovering a lost order later depends on that
/// id being in the trail, so it is threaded through rather than inferred.
/// </summary>
/// <param name="ClientOrderId">Caller-generated id, for placements and baskets.</param>
/// <param name="BrokerOrderId">Broker's id, for modify/cancel/lookup.</param>
/// <param name="LegCount">Number of legs, for baskets.</param>
public readonly record struct ConnectorCallSubject(
    Guid? ClientOrderId = null,
    string? BrokerOrderId = null,
    int? LegCount = null);

/// <summary>The unit type, so the non-generic <see cref="Result"/> can ride the generic path.</summary>
public readonly record struct Nothing
{
    public static readonly Nothing Value = default;
}

/// <summary>Adapters between <see cref="Result"/> and <see cref="Result{T}"/> for interceptors.</summary>
public static class ConnectorInterceptorExtensions
{
    /// <summary>
    /// Runs a void-returning call through the generic interception path by lifting
    /// <see cref="Result"/> into <c>Result&lt;Nothing&gt;</c> and back. Written once here so no
    /// decorator has to implement two nearly identical code paths.
    /// </summary>
    public static async Task<Result> InterceptAsync(
        this IConnectorInterceptor interceptor,
        ConnectorOperation operation,
        Func<CancellationToken, Task<Result>> next,
        CancellationToken ct,
        ConnectorCallSubject subject = default)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(next);

        var lifted = await interceptor.InterceptAsync<Nothing>(
            operation,
            async token =>
            {
                var result = await next(token);
                return result.IsSuccess
                    ? Result<Nothing>.Success(Nothing.Value)
                    : Result<Nothing>.Failure(result.Error);
            },
            ct,
            subject);

        return lifted.IsSuccess ? Result.Success() : Result.Failure(lifted.Error);
    }
}

/// <summary>
/// Base class for every host decorator: wraps an <see cref="IBrokerConnector"/>, routes every
/// facet call through <see cref="IConnectorInterceptor.InterceptAsync{T}"/>, and delegates
/// everything else.
///
/// FACET WRAPPING, and why it matters for composition: a decorator cannot simply return the
/// inner connector's <c>Orders</c> property, because then a caller holding
/// <c>connector.Orders</c> would bypass the decorator entirely. Each facet is therefore
/// wrapped in a small proxy created ONCE and cached, so
/// <c>connector.Orders == connector.Orders</c> holds (some callers cache the facet) and so
/// four stacked decorators produce four thin proxies rather than a new allocation per call.
///
/// Because each decorator is itself an <see cref="IBrokerConnector"/>, they compose in any
/// order; the host fixes a specific order for good reasons documented on
/// <c>ConnectorFactory</c>.
/// </summary>
public abstract class InterceptingConnector : IBrokerConnector, IConnectorInterceptor
{
    private readonly Lazy<IConnectorAuth> _auth;
    private readonly Lazy<IConnectorOrders> _orders;
    private readonly Lazy<IConnectorPortfolio> _portfolio;
    private readonly Lazy<IConnectorMarketData> _marketData;
    private readonly Lazy<IConnectorReference> _reference;
    private readonly Lazy<IConnectorStream?> _stream;

    protected InterceptingConnector(IBrokerConnector inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;

        _auth = new Lazy<IConnectorAuth>(() => new InterceptingAuth(inner.Auth, this));
        _orders = new Lazy<IConnectorOrders>(() => new InterceptingOrders(inner.Orders, this));
        _portfolio = new Lazy<IConnectorPortfolio>(() => new InterceptingPortfolio(inner.Portfolio, this));
        _marketData = new Lazy<IConnectorMarketData>(() => new InterceptingMarketData(inner.MarketData, this));
        _reference = new Lazy<IConnectorReference>(() => new InterceptingReference(inner.Reference, this));

        // Null stays null: the contract says a broker with no feed exposes null, and
        // manufacturing a proxy over nothing would make every caller's null check pass and
        // then fail later at subscribe time.
        _stream = new Lazy<IConnectorStream?>(() =>
            inner.Stream is { } stream ? new InterceptingStream(stream, this) : null);
    }

    protected IBrokerConnector Inner { get; }

    public ConnectorManifest Manifest => Inner.Manifest;

    public IConnectorAuth Auth => _auth.Value;

    public IConnectorOrders Orders => _orders.Value;

    public IConnectorPortfolio Portfolio => _portfolio.Value;

    public IConnectorMarketData MarketData => _marketData.Value;

    public IConnectorReference Reference => _reference.Value;

    public IConnectorStream? Stream => _stream.Value;

    public Task<Result<ConnectorHealth>> CheckHealthAsync(CancellationToken ct = default) =>
        InterceptAsync(ConnectorOperations.CheckHealth, token => Inner.CheckHealthAsync(token), ct);

    /// <inheritdoc />
    public abstract Task<Result<T>> InterceptAsync<T>(
        ConnectorOperation operation,
        Func<CancellationToken, Task<Result<T>>> next,
        CancellationToken ct,
        ConnectorCallSubject subject = default);

    /// <summary>
    /// Hook for decorating the raw event stream (tracing counts ticks; audit ignores it).
    /// Default is pass-through — wrapping every tick in a decorator is a measurable cost on a
    /// feed doing tens of thousands of events a second.
    /// </summary>
    protected internal virtual IAsyncEnumerable<StreamEvent> InterceptEvents(
        IAsyncEnumerable<StreamEvent> events) => events;

    /// <summary>
    /// Hook for decorating the instrument-master enumeration. Default is pass-through, for the
    /// same reason: an instrument master is hundreds of thousands of rows.
    /// </summary>
    protected internal virtual IAsyncEnumerable<InstrumentDefinition> InterceptInstruments(
        IAsyncEnumerable<InstrumentDefinition> instruments) => instruments;

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return Inner.DisposeAsync();
    }

    private sealed class InterceptingAuth(IConnectorAuth inner, InterceptingConnector owner) : IConnectorAuth
    {
        public Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.AuthBegin, token => inner.BeginAsync(context, token), ct);

        public Task<Result<AuthStep>> ContinueAsync(
            AuthContext context,
            string response,
            CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.AuthContinue,
                token => inner.ContinueAsync(context, response, token),
                ct);

        public Task<Result<BrokerSession>> RefreshAsync(BrokerSession session, CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.AuthRefresh, token => inner.RefreshAsync(session, token), ct);

        public Task<Result> RevokeAsync(BrokerSession session, CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.AuthRevoke, token => inner.RevokeAsync(session, token), ct);

        public Task<Result> KeepAliveAsync(BrokerSession session, CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.AuthKeepAlive, token => inner.KeepAliveAsync(session, token), ct);
    }

    private sealed class InterceptingOrders(IConnectorOrders inner, InterceptingConnector owner) : IConnectorOrders
    {
        public Task<Result<OrderAck>> PlaceAsync(PlaceOrderRequest request, CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.PlaceOrder,
                token => inner.PlaceAsync(request, token),
                ct,
                new ConnectorCallSubject(ClientOrderId: request.ClientOrderId));

        public Task<Result<OrderAck>> ModifyAsync(ModifyOrderRequest request, CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.ModifyOrder,
                token => inner.ModifyAsync(request, token),
                ct,
                new ConnectorCallSubject(BrokerOrderId: request.BrokerOrderId));

        public Task<Result<OrderAck>> CancelAsync(string brokerOrderId, CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.CancelOrder,
                token => inner.CancelAsync(brokerOrderId, token),
                ct,
                new ConnectorCallSubject(BrokerOrderId: brokerOrderId));

        public Task<Result<int>> CancelAllAsync(CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.CancelAllOrders, token => inner.CancelAllAsync(token), ct);

        public Task<Result<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(
            OrderQuery query,
            CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.GetOrders, token => inner.GetOrdersAsync(query, token), ct);

        public Task<Result<BrokerOrder>> GetOrderAsync(string brokerOrderId, CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.GetOrder,
                token => inner.GetOrderAsync(brokerOrderId, token),
                ct,
                new ConnectorCallSubject(BrokerOrderId: brokerOrderId));

        public Task<Result<IReadOnlyList<BrokerTrade>>> GetTradesAsync(
            OrderQuery query,
            CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.GetTrades, token => inner.GetTradesAsync(query, token), ct);

        public Task<Result<IReadOnlyList<OrderAck>>> PlaceBasketAsync(
            IReadOnlyList<PlaceOrderRequest> requests,
            CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.PlaceBasket,
                token => inner.PlaceBasketAsync(requests, token),
                ct,
                new ConnectorCallSubject(LegCount: requests.Count));

        public Task<Result<MarginEstimate>> EstimateMarginAsync(
            PlaceOrderRequest request,
            CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.EstimateMargin,
                token => inner.EstimateMarginAsync(request, token),
                ct);

        public Task<Result<ChargesEstimate>> EstimateChargesAsync(
            PlaceOrderRequest request,
            CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.EstimateCharges,
                token => inner.EstimateChargesAsync(request, token),
                ct);
    }

    private sealed class InterceptingPortfolio(IConnectorPortfolio inner, InterceptingConnector owner)
        : IConnectorPortfolio
    {
        public Task<Result<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.GetPositions, token => inner.GetPositionsAsync(token), ct);

        public Task<Result<IReadOnlyList<BrokerHolding>>> GetHoldingsAsync(CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.GetHoldings, token => inner.GetHoldingsAsync(token), ct);

        public Task<Result<IReadOnlyList<BrokerBalance>>> GetBalancesAsync(CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.GetBalances, token => inner.GetBalancesAsync(token), ct);

        public Task<Result> ConvertPositionAsync(
            ConvertPositionRequest request,
            CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.ConvertPosition,
                token => inner.ConvertPositionAsync(request, token),
                ct);
    }

    private sealed class InterceptingMarketData(IConnectorMarketData inner, InterceptingConnector owner)
        : IConnectorMarketData
    {
        public Task<Result<Quote>> GetQuoteAsync(InstrumentKey instrument, CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.GetQuote, token => inner.GetQuoteAsync(instrument, token), ct);

        public Task<Result<IReadOnlyDictionary<InstrumentKey, Money>>> GetLtpAsync(
            IReadOnlyCollection<InstrumentKey> instruments,
            CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.GetLtp, token => inner.GetLtpAsync(instruments, token), ct);

        public Task<Result<IReadOnlyDictionary<InstrumentKey, Quote>>> GetQuotesAsync(
            IReadOnlyCollection<InstrumentKey> instruments,
            CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.GetQuotes, token => inner.GetQuotesAsync(instruments, token), ct);

        public Task<Result<CandleSeries>> GetHistoricalAsync(
            HistoryRequest request,
            CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.GetHistorical,
                token => inner.GetHistoricalAsync(request, token),
                ct);

        public Task<Result<MarketDepth>> GetDepthAsync(InstrumentKey instrument, CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.GetDepth, token => inner.GetDepthAsync(instrument, token), ct);

        public Task<Result<OptionChain>> GetOptionChainAsync(
            InstrumentKey underlying,
            DateOnly expiry,
            CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.GetOptionChain,
                token => inner.GetOptionChainAsync(underlying, expiry, token),
                ct);
    }

    private sealed class InterceptingReference(IConnectorReference inner, InterceptingConnector owner)
        : IConnectorReference
    {
        public IAsyncEnumerable<InstrumentDefinition> GetInstrumentsAsync(
            Venue? venue = null,
            AssetClass? assetClass = null,
            CancellationToken ct = default) =>
            owner.InterceptInstruments(inner.GetInstrumentsAsync(venue, assetClass, ct));

        public Task<Result<InstrumentDefinition>> ResolveAsync(InstrumentKey key, CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.ResolveInstrument, token => inner.ResolveAsync(key, token), ct);

        public Task<Result<IReadOnlyList<InstrumentDefinition>>> SearchAsync(
            string query,
            int limit = 20,
            CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.SearchInstruments,
                token => inner.SearchAsync(query, limit, token),
                ct);
    }

    private sealed class InterceptingStream(IConnectorStream inner, InterceptingConnector owner) : IConnectorStream
    {
        /// <summary>Read straight through: state is a field read, and wrapping it would be silly.</summary>
        public StreamState State => inner.State;

        public Task<Result> ConnectAsync(CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.StreamConnect, token => inner.ConnectAsync(token), ct);

        public Task<Result> DisconnectAsync(CancellationToken ct = default) =>
            owner.InterceptAsync(ConnectorOperations.StreamDisconnect, token => inner.DisconnectAsync(token), ct);

        public Task<Result> SubscribeAsync(
            IReadOnlyCollection<InstrumentKey> instruments,
            StreamMode mode,
            CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.StreamSubscribe,
                token => inner.SubscribeAsync(instruments, mode, token),
                ct);

        public Task<Result> UnsubscribeAsync(
            IReadOnlyCollection<InstrumentKey> instruments,
            CancellationToken ct = default) =>
            owner.InterceptAsync(
                ConnectorOperations.StreamUnsubscribe,
                token => inner.UnsubscribeAsync(instruments, token),
                ct);

        public IAsyncEnumerable<StreamEvent> Events(CancellationToken ct = default) =>
            owner.InterceptEvents(inner.Events(ct));
    }
}

/// <summary>
/// Helpers for decorators that want to observe an <see cref="IAsyncEnumerable{T}"/> without
/// buffering it.
/// </summary>
public static class AsyncEnumerableInterception
{
    /// <summary>
    /// Calls <paramref name="onItem"/> for each element as it flows past. Streaming, allocation
    /// free per item, and it does not swallow the enumerator's cancellation.
    /// </summary>
    public static async IAsyncEnumerable<T> Tap<T>(
        this IAsyncEnumerable<T> source,
        Action<T> onItem,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onItem);

        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
        {
            onItem(item);
            yield return item;
        }
    }
}
