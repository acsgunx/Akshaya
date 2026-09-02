using Akshaya.Connector.Paper;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;

namespace Akshaya.Connectors.TestKit.TestDoubles;

/// <summary>
/// Builds the objects every connector test needs, so a test reads as the thing it is asserting
/// rather than as forty lines of object initialisers.
///
/// The defaults are chosen to be BORING: round prices, lot size one, a single currency, a
/// clock at a safe instant. A fixture with interesting defaults makes every test that uses it
/// implicitly a test of those defaults, and then nobody can tell which assertions actually
/// depend on them.
/// </summary>
public static class FixtureBuilder
{
    /// <summary>An ordinary cash equity. The instrument most tests should use.</summary>
    public static InstrumentDefinition Equity(
        Venue venue,
        string symbol,
        Currency currency,
        decimal lotSize = 1m,
        decimal tickSize = 0.01m,
        string? isin = null) =>
        new()
        {
            Key = new InstrumentKey(venue, symbol, AssetClass.Equity),
            Name = symbol + " Limited",
            Currency = currency,
            Isin = isin,
            LotSize = lotSize,
            TickSize = tickSize,
            Multiplier = 1m,
        };

    /// <summary>
    /// A futures contract, for the cases where the contract multiplier matters — position
    /// valuation and every Indian charge line. A multiplier of one hides an entire class of
    /// P&amp;L bug, so at least one instrument in any fixture set should have a real one.
    /// </summary>
    public static InstrumentDefinition Future(
        Venue venue,
        string symbol,
        Currency currency,
        DateOnly expiry,
        decimal lotSize = 50m,
        decimal multiplier = 50m) =>
        new()
        {
            Key = new InstrumentKey(venue, symbol, AssetClass.Future, expiry),
            Name = $"{symbol} futures {expiry:yyyy-MM}",
            Currency = currency,
            LotSize = lotSize,
            TickSize = 0.05m,
            Multiplier = multiplier,
        };

    /// <summary>A market order. Quantity is whole so it is legal on lot-based connectors too.</summary>
    public static PlaceOrderRequest Order(
        InstrumentKey instrument,
        Side side = Side.Buy,
        decimal quantity = 10m,
        OrderType type = OrderType.Market,
        TimeInForce timeInForce = TimeInForce.Day,
        PositionEffect positionEffect = PositionEffect.Delivery,
        Money? limitPrice = null,
        Money? triggerPrice = null,
        OrderVariety variety = OrderVariety.Regular,
        Guid? clientOrderId = null,
        DateOnly? goodTillDate = null) =>
        new()
        {
            ClientOrderId = clientOrderId ?? Guid.NewGuid(),
            Instrument = instrument,
            Side = side,
            Quantity = new Quantity(quantity),
            OrderType = type,
            PositionEffect = positionEffect,
            TimeInForce = timeInForce,
            Variety = variety,
            LimitPrice = limitPrice,
            TriggerPrice = triggerPrice,
            GoodTillDate = goodTillDate,
        };

    /// <summary>
    /// A price source seeded with one instrument and a flat tape. Enough for any test whose
    /// subject is the connector rather than the fill model.
    /// </summary>
    public static InMemoryMarketDataSource SingleInstrumentSource(
        InstrumentDefinition definition,
        decimal price,
        DateTimeOffset at,
        int ticks = 1)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var source = new InMemoryMarketDataSource().WithInstrument(definition, price);

        for (var i = 0; i < ticks; i++)
        {
            source.WithTick(
                definition.Key,
                price,
                at.AddSeconds(i),
                bid: price - definition.TickSize,
                ask: price + definition.TickSize);
        }

        return source;
    }

    /// <summary>
    /// A ready-to-drive Paper connector. The clock is manual and the seed is fixed, so the
    /// fills a test asserts on are the fills every re-run produces.
    /// </summary>
    public static PaperConnector PaperConnector(
        ConnectorManifest manifest,
        IMarketDataSource source,
        IClock clock,
        BrokerSession? session = null,
        PaperOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(clock);

        return new PaperConnector(
            manifest,
            session ?? new PaperAuth(clock).CreateSession("test"),
            source,
            options ?? new PaperOptions(),
            NullLogger<PaperConnector>.Instance,
            clock);
    }
}
