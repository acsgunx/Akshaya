using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Domain;
using Akshaya.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Akshaya.Trading.Tests;

/// <summary>
/// An accepted amendment must move the ORDER, not just its event log.
///
/// PREVENTS: the bug these tests were written against. <see cref="Order.Request"/> was
/// immutable, so a successful amendment appended an event reading "quantity -> 7" while every
/// projection off the aggregate went on reporting the original 10 — the blotter showed the
/// trader their own change being ignored, <see cref="Order.PendingQuantity"/> computed against
/// a quantity that no longer existed at the broker, and the risk gate sized its next check on
/// the stale figure. Nothing threw and nothing logged; the numbers were simply wrong until the
/// next reconciliation pass happened to overwrite them.
/// </summary>
public sealed class OrderAmendmentTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 4, 9, 30, 0, TimeSpan.FromHours(5.5));

    private static PlaceOrderRequest Request(decimal quantity = 10m, decimal limit = 1500m) => new()
    {
        ClientOrderId = Guid.CreateVersion7(),
        Instrument = new InstrumentKey(Venue.Nse, "INFY", AssetClass.Equity),
        Side = Side.Buy,
        Quantity = new Quantity(quantity),
        OrderType = OrderType.Limit,
        PositionEffect = PositionEffect.Intraday,
        LimitPrice = new Money(limit, Currency.Inr),
    };

    private static Order NewOrder() =>
        Order.Create("tenant-1", "user-1", "link-1", "connector-1", Request(), At);

    [Fact]
    public void An_accepted_amendment_updates_the_order_terms()
    {
        var order = NewOrder();
        var amended = Request(quantity: 7m, limit: 1490m);

        order.RecordAmendment(At.AddMinutes(1), "quantity -> 7", OrderActors.User, amended: amended);

        order.Request.Quantity.Value.Should().Be(7m);
        order.Request.LimitPrice!.Value.Amount.Should().Be(1490m);
    }

    [Fact]
    public void An_accepted_amendment_moves_the_pending_quantity_with_it()
    {
        // The reason the stale Request mattered beyond cosmetics: PendingQuantity is derived
        // from it, and it is what the risk engine reads as live exposure.
        var order = NewOrder();

        order.RecordAmendment(At.AddMinutes(1), "quantity -> 7", OrderActors.User, amended: Request(quantity: 7m));

        order.PendingQuantity.Value.Should().Be(7m);
    }

    [Fact]
    public void A_blocked_amendment_leaves_the_order_on_its_original_terms()
    {
        // The order is still working at the broker on the terms it was placed with, and those
        // are the terms the trader has to be shown. Passing no `amended` is how the handler
        // says "the broker did not accept this".
        var order = NewOrder();

        order.RecordAmendment(At.AddMinutes(1), "Amendment blocked by MaxOrderValue", OrderActors.RiskGate);

        order.Request.Quantity.Value.Should().Be(10m);
        order.Request.LimitPrice!.Value.Amount.Should().Be(1500m);
    }

    [Fact]
    public void Every_amendment_is_recorded_whether_or_not_it_was_accepted()
    {
        var order = NewOrder();
        var before = order.Events.Count;

        order.RecordAmendment(At.AddMinutes(1), "quantity -> 7", OrderActors.User, amended: Request(quantity: 7m));
        order.RecordAmendment(At.AddMinutes(2), "Amendment failed: broker said no", OrderActors.Broker);

        // The audit trail must replay the order's whole life, including the attempts that went
        // nowhere — a refused amendment is exactly the thing someone asks about afterwards.
        order.Events.Should().HaveCount(before + 2);
        order.Events[^1].Note.Should().Contain("failed");
    }

    [Fact]
    public void An_amendment_stamps_the_update_time()
    {
        var order = NewOrder();
        var at = At.AddMinutes(3);

        order.RecordAmendment(at, "quantity -> 7", OrderActors.User, amended: Request(quantity: 7m));

        order.UpdatedAt.Should().Be(at);
    }
}
