using System.Runtime.CompilerServices;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connectors.Sdk;

/// <summary>
/// Ready-made facets that decline every call with <see cref="ConnectorErrorCodes.NotSupported"/>.
///
/// Why these exist: <see cref="IBrokerConnector"/> exposes six facets and almost no broker
/// implements all six. Without these, a connector author's options are to throw (which turns
/// a known capability gap into an incident), to return a made-up empty list (which is a
/// silent lie the portfolio then aggregates), or to hand-write five stub classes. All three
/// happen in practice. A connector that has no live feed writes:
///
/// <code>public override IConnectorStream? Stream => null;            // preferred, contract says null is legal
/// // or, if it must return something non-null to a caller that dislikes null:
/// public override IConnectorStream? Stream => NullStream.Instance;</code>
///
/// The distinction that matters: NotSupported is a CAPABILITY statement, not a failure. It is
/// deliberately excluded from <see cref="ConnectorErrorCodes.Retryable"/>, and the UI renders
/// it as a disabled control rather than an error toast.
/// </summary>
public static class NotSupportedFacets
{
    /// <summary>Convenience for connectors declining one method on an otherwise-real facet.</summary>
    public static Task<Result<T>> DeclineAsync<T>(string capability) =>
        Task.FromResult(Result<T>.Failure(ConnectorErrors.NotSupported(capability)));

    /// <summary>Non-generic companion to <see cref="DeclineAsync{T}"/>.</summary>
    public static Task<Result> DeclineAsync(string capability) =>
        Task.FromResult(Result.Failure(ConnectorErrors.NotSupported(capability)));
}

/// <summary>
/// A stream that is not a stream. Permanently <see cref="StreamState.Disconnected"/>, yields
/// no events, and refuses subscriptions instead of accepting them into a void — a subscribe
/// that "succeeds" and never ticks is far harder to diagnose than one that says no.
/// </summary>
public sealed class NullStream : IConnectorStream
{
    public static readonly NullStream Instance = new();

    private NullStream()
    {
    }

    public StreamState State => StreamState.Disconnected;

    public Task<Result> ConnectAsync(CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync("live streaming");

    /// <summary>Disconnecting something that was never connected is a no-op success, not an error.</summary>
    public Task<Result> DisconnectAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    public Task<Result> SubscribeAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        StreamMode mode,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync("live streaming");

    /// <summary>Symmetric with <see cref="DisconnectAsync"/>: removing nothing succeeds.</summary>
    public Task<Result> UnsubscribeAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    public async IAsyncEnumerable<StreamEvent> Events([EnumeratorCancellation] CancellationToken ct = default)
    {
        // One synthetic state event, then nothing. Emitting the state means a consumer that
        // renders the stale-data banner from ConnectionChanged shows the right thing without
        // needing to know it is talking to a null stream.
        yield return new StreamEvent.ConnectionChanged(
            StreamState.Disconnected,
            "This broker does not provide a live feed.");

        await Task.CompletedTask;
    }
}

/// <summary>Order facet for read-only connectors (a data-only vendor, a reference-data plugin).</summary>
public sealed class NotSupportedOrders : IConnectorOrders
{
    public static readonly NotSupportedOrders Instance = new();

    private NotSupportedOrders()
    {
    }

    public Task<Result<OrderAck>> PlaceAsync(PlaceOrderRequest request, CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<OrderAck>("placing orders");

    public Task<Result<OrderAck>> ModifyAsync(ModifyOrderRequest request, CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<OrderAck>("modifying orders");

    public Task<Result<OrderAck>> CancelAsync(string brokerOrderId, CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<OrderAck>("cancelling orders");

    public Task<Result<int>> CancelAllAsync(CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<int>("cancel-all");

    public Task<Result<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(
        OrderQuery query,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<IReadOnlyList<BrokerOrder>>("reading the order book");

    public Task<Result<BrokerOrder>> GetOrderAsync(string brokerOrderId, CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<BrokerOrder>("reading the order book");

    public Task<Result<IReadOnlyList<BrokerTrade>>> GetTradesAsync(
        OrderQuery query,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<IReadOnlyList<BrokerTrade>>("reading the trade book");

    public Task<Result<IReadOnlyList<OrderAck>>> PlaceBasketAsync(
        IReadOnlyList<PlaceOrderRequest> requests,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<IReadOnlyList<OrderAck>>("basket orders");

    public Task<Result<MarginEstimate>> EstimateMarginAsync(
        PlaceOrderRequest request,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<MarginEstimate>("margin estimation");

    public Task<Result<ChargesEstimate>> EstimateChargesAsync(
        PlaceOrderRequest request,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<ChargesEstimate>("charges estimation");
}

/// <summary>
/// Portfolio facet for connectors that only execute (an exchange DMA link with no custody
/// view). Note it declines rather than returning empty lists: an empty holdings list is
/// indistinguishable from "flat", and the Portfolio module would happily aggregate that
/// into a wrong total.
/// </summary>
public sealed class NotSupportedPortfolio : IConnectorPortfolio
{
    public static readonly NotSupportedPortfolio Instance = new();

    private NotSupportedPortfolio()
    {
    }

    public Task<Result<IReadOnlyList<BrokerPosition>>> GetPositionsAsync(CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<IReadOnlyList<BrokerPosition>>("positions");

    public Task<Result<IReadOnlyList<BrokerHolding>>> GetHoldingsAsync(CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<IReadOnlyList<BrokerHolding>>("holdings");

    public Task<Result<IReadOnlyList<BrokerBalance>>> GetBalancesAsync(CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<IReadOnlyList<BrokerBalance>>("balances");

    public Task<Result> ConvertPositionAsync(
        ConvertPositionRequest request,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync("position conversion");
}

/// <summary>Market-data facet for execution-only brokers; prices come from another connector.</summary>
public sealed class NotSupportedMarketData : IConnectorMarketData
{
    public static readonly NotSupportedMarketData Instance = new();

    private NotSupportedMarketData()
    {
    }

    public Task<Result<Quote>> GetQuoteAsync(InstrumentKey instrument, CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<Quote>("quotes");

    public Task<Result<IReadOnlyDictionary<InstrumentKey, Money>>> GetLtpAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<IReadOnlyDictionary<InstrumentKey, Money>>("last traded price");

    public Task<Result<IReadOnlyDictionary<InstrumentKey, Quote>>> GetQuotesAsync(
        IReadOnlyCollection<InstrumentKey> instruments,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<IReadOnlyDictionary<InstrumentKey, Quote>>("quotes");

    public Task<Result<CandleSeries>> GetHistoricalAsync(
        HistoryRequest request,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<CandleSeries>("historical candles");

    public Task<Result<MarketDepth>> GetDepthAsync(InstrumentKey instrument, CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<MarketDepth>("market depth");

    public Task<Result<OptionChain>> GetOptionChainAsync(
        InstrumentKey underlying,
        DateOnly expiry,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<OptionChain>("option chains");
}

/// <summary>
/// Reference facet for brokers with no instrument master of their own. The instrument
/// enumeration yields nothing (an empty ingest is meaningful and harmless), but the
/// single-instrument lookups decline, because a failed resolve must never look like
/// "instrument does not exist" when the truth is "this broker cannot tell you".
/// </summary>
public sealed class NotSupportedReference : IConnectorReference
{
    public static readonly NotSupportedReference Instance = new();

    private NotSupportedReference()
    {
    }

    public async IAsyncEnumerable<InstrumentDefinition> GetInstrumentsAsync(
        Venue? venue = null,
        AssetClass? assetClass = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<Result<InstrumentDefinition>> ResolveAsync(InstrumentKey key, CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<InstrumentDefinition>("instrument reference data");

    public Task<Result<IReadOnlyList<InstrumentDefinition>>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<IReadOnlyList<InstrumentDefinition>>("instrument search");
}

/// <summary>
/// Auth facet for connectors that need no authentication at all (a paper-trading connector,
/// a public data source). <see cref="BeginAsync"/> is abstract-by-absence here: it declines,
/// so a connector using this must genuinely have a session handed to it another way.
/// </summary>
public sealed class NotSupportedAuth : IConnectorAuth
{
    public static readonly NotSupportedAuth Instance = new();

    private NotSupportedAuth()
    {
    }

    public Task<Result<AuthStep>> BeginAsync(AuthContext context, CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<AuthStep>("interactive authentication");

    public Task<Result<AuthStep>> ContinueAsync(
        AuthContext context,
        string response,
        CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<AuthStep>("interactive authentication");

    public Task<Result<BrokerSession>> RefreshAsync(BrokerSession session, CancellationToken ct = default) =>
        NotSupportedFacets.DeclineAsync<BrokerSession>("session refresh");

    /// <summary>Revoking a session that does not exist is a success — the desired end state holds.</summary>
    public Task<Result> RevokeAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    /// <summary>
    /// Success, not NotSupported. The host calls this on a timer for every connector; a
    /// connector with no keepalive requirement is already in the state the call is trying to
    /// achieve, and failing here would light up health dashboards for no reason.
    /// </summary>
    public Task<Result> KeepAliveAsync(BrokerSession session, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());
}
