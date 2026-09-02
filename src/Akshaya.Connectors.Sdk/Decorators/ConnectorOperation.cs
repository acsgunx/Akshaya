using Akshaya.Connectors.Abstractions;

namespace Akshaya.Connectors.Sdk.Decorators;

/// <summary>Which facet of <see cref="IBrokerConnector"/> a call belongs to.</summary>
public enum ConnectorFacet
{
    Auth,
    Orders,
    Portfolio,
    MarketData,
    Reference,
    Stream,
    Health,
}

/// <summary>
/// Everything a decorator needs to know about a call WITHOUT knowing which method it is.
///
/// This is what makes the decorators generic instead of forty hand-written overrides each.
/// Two flags carry all the policy:
///
///  * <see cref="IsIdempotentRead"/> — the ONLY calls the resilience decorator may retry.
///  * <see cref="IsOrderAffecting"/> — the calls the audit decorator must record.
///
/// Both are properties of the OPERATION, declared once in <see cref="ConnectorOperations"/>,
/// rather than decisions each decorator re-derives from a method name. A typo in a string
/// comparison inside a retry policy is how an order gets placed twice.
/// </summary>
/// <param name="Facet">Owning facet, for metric and log dimensions.</param>
/// <param name="Method">Method name, for metric and log dimensions.</param>
/// <param name="RateLimitScope">Which manifest bucket this call draws from.</param>
/// <param name="IsIdempotentRead">
/// True only when calling this twice is indistinguishable from calling it once. Every read is;
/// no write is, not even <c>CancelAsync</c> (a cancel that succeeded on the first, timed-out
/// attempt would report OrderNotFound on the second and look like a failure).
/// </param>
/// <param name="IsOrderAffecting">
/// True when the call can change what exists at the broker. Drives the audit trail, which
/// several jurisdictions require and which is the first thing anyone asks for after an
/// incident.
/// </param>
public readonly record struct ConnectorOperation(
    ConnectorFacet Facet,
    string Method,
    string RateLimitScope,
    bool IsIdempotentRead,
    bool IsOrderAffecting)
{
    /// <summary>Stable name for spans, metrics and audit rows: <c>Orders.PlaceAsync</c>.</summary>
    public string FullName => $"{Facet}.{Method}";
}

/// <summary>
/// The operation table. One entry per method on the connector facets.
///
/// It is written out longhand rather than derived by reflection so that the idempotency
/// classification is reviewable in a pull request: this table IS the retry policy, and it
/// should be read by a human before anyone changes it.
/// </summary>
public static class ConnectorOperations
{
    // --- auth. Never rate limited on the quotes/data buckets; a login is metered globally
    // where it is metered at all. Never retried: a repeated OTP submission burns the user's
    // one-time code and several brokers lock the account after three. ---

    public static readonly ConnectorOperation AuthBegin =
        new(ConnectorFacet.Auth, nameof(IConnectorAuth.BeginAsync), RateLimitScopes.Global, false, false);

    public static readonly ConnectorOperation AuthContinue =
        new(ConnectorFacet.Auth, nameof(IConnectorAuth.ContinueAsync), RateLimitScopes.Global, false, false);

    public static readonly ConnectorOperation AuthRefresh =
        new(ConnectorFacet.Auth, nameof(IConnectorAuth.RefreshAsync), RateLimitScopes.Global, false, false);

    public static readonly ConnectorOperation AuthRevoke =
        new(ConnectorFacet.Auth, nameof(IConnectorAuth.RevokeAsync), RateLimitScopes.Global, false, false);

    public static readonly ConnectorOperation AuthKeepAlive =
        new(ConnectorFacet.Auth, nameof(IConnectorAuth.KeepAliveAsync), RateLimitScopes.Global, false, false);

    // --- orders. Writes are order-affecting and NOT idempotent; reads are both-safe. ---

    public static readonly ConnectorOperation PlaceOrder =
        new(ConnectorFacet.Orders, nameof(IConnectorOrders.PlaceAsync), RateLimitScopes.Orders, false, true);

    public static readonly ConnectorOperation ModifyOrder =
        new(ConnectorFacet.Orders, nameof(IConnectorOrders.ModifyAsync), RateLimitScopes.Orders, false, true);

    public static readonly ConnectorOperation CancelOrder =
        new(ConnectorFacet.Orders, nameof(IConnectorOrders.CancelAsync), RateLimitScopes.Orders, false, true);

    public static readonly ConnectorOperation CancelAllOrders =
        new(ConnectorFacet.Orders, nameof(IConnectorOrders.CancelAllAsync), RateLimitScopes.Orders, false, true);

    public static readonly ConnectorOperation PlaceBasket =
        new(ConnectorFacet.Orders, nameof(IConnectorOrders.PlaceBasketAsync), RateLimitScopes.Orders, false, true);

    public static readonly ConnectorOperation GetOrders =
        new(ConnectorFacet.Orders, nameof(IConnectorOrders.GetOrdersAsync), RateLimitScopes.Data, true, false);

    public static readonly ConnectorOperation GetOrder =
        new(ConnectorFacet.Orders, nameof(IConnectorOrders.GetOrderAsync), RateLimitScopes.Data, true, false);

    public static readonly ConnectorOperation GetTrades =
        new(ConnectorFacet.Orders, nameof(IConnectorOrders.GetTradesAsync), RateLimitScopes.Data, true, false);

    // Estimates are pure reads at every broker we have met — they compute, they do not book.
    public static readonly ConnectorOperation EstimateMargin =
        new(ConnectorFacet.Orders, nameof(IConnectorOrders.EstimateMarginAsync), RateLimitScopes.Data, true, false);

    public static readonly ConnectorOperation EstimateCharges =
        new(ConnectorFacet.Orders, nameof(IConnectorOrders.EstimateChargesAsync), RateLimitScopes.Data, true, false);

    // --- portfolio ---

    public static readonly ConnectorOperation GetPositions =
        new(ConnectorFacet.Portfolio, nameof(IConnectorPortfolio.GetPositionsAsync), RateLimitScopes.Data, true, false);

    public static readonly ConnectorOperation GetHoldings =
        new(ConnectorFacet.Portfolio, nameof(IConnectorPortfolio.GetHoldingsAsync), RateLimitScopes.Data, true, false);

    public static readonly ConnectorOperation GetBalances =
        new(ConnectorFacet.Portfolio, nameof(IConnectorPortfolio.GetBalancesAsync), RateLimitScopes.Data, true, false);

    // --- market data. The quotes bucket, which brokers meter separately and generously. ---

    public static readonly ConnectorOperation GetQuote =
        new(ConnectorFacet.MarketData, nameof(IConnectorMarketData.GetQuoteAsync), RateLimitScopes.Quotes, true, false);

    public static readonly ConnectorOperation GetLtp =
        new(ConnectorFacet.MarketData, nameof(IConnectorMarketData.GetLtpAsync), RateLimitScopes.Quotes, true, false);

    public static readonly ConnectorOperation GetQuotes =
        new(ConnectorFacet.MarketData, nameof(IConnectorMarketData.GetQuotesAsync), RateLimitScopes.Quotes, true, false);

    public static readonly ConnectorOperation GetHistorical =
        new(ConnectorFacet.MarketData, nameof(IConnectorMarketData.GetHistoricalAsync), RateLimitScopes.Data, true, false);

    public static readonly ConnectorOperation GetDepth =
        new(ConnectorFacet.MarketData, nameof(IConnectorMarketData.GetDepthAsync), RateLimitScopes.Quotes, true, false);

    public static readonly ConnectorOperation GetOptionChain =
        new(ConnectorFacet.MarketData, nameof(IConnectorMarketData.GetOptionChainAsync), RateLimitScopes.Quotes, true, false);

    // --- reference ---

    public static readonly ConnectorOperation GetInstruments =
        new(ConnectorFacet.Reference, nameof(IConnectorReference.GetInstrumentsAsync), RateLimitScopes.Data, true, false);

    public static readonly ConnectorOperation ResolveInstrument =
        new(ConnectorFacet.Reference, nameof(IConnectorReference.ResolveAsync), RateLimitScopes.Data, true, false);

    public static readonly ConnectorOperation SearchInstruments =
        new(ConnectorFacet.Reference, nameof(IConnectorReference.SearchAsync), RateLimitScopes.Data, true, false);

    // --- streaming. Connect and subscribe are retried: they are idempotent in effect (the
    // desired end state is "subscribed"), and a feed that gives up on the first blip is
    // useless. Unsubscribe is likewise idempotent. ---

    public static readonly ConnectorOperation StreamConnect =
        new(ConnectorFacet.Stream, nameof(IConnectorStream.ConnectAsync), RateLimitScopes.Global, true, false);

    public static readonly ConnectorOperation StreamDisconnect =
        new(ConnectorFacet.Stream, nameof(IConnectorStream.DisconnectAsync), RateLimitScopes.Global, true, false);

    public static readonly ConnectorOperation StreamSubscribe =
        new(ConnectorFacet.Stream, nameof(IConnectorStream.SubscribeAsync), RateLimitScopes.Quotes, true, false);

    public static readonly ConnectorOperation StreamUnsubscribe =
        new(ConnectorFacet.Stream, nameof(IConnectorStream.UnsubscribeAsync), RateLimitScopes.Quotes, true, false);

    // --- health. Deliberately NOT rate limited against a real bucket and never retried: the
    // UI polls it, and a health check that queues behind a rate limiter reports the connector
    // as slow when the connector is fine. ---

    public static readonly ConnectorOperation CheckHealth =
        new(ConnectorFacet.Health, nameof(IBrokerConnector.CheckHealthAsync), RateLimitScopes.Global, true, false);
}
