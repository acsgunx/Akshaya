using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Trading.Application;

/// <summary>
/// Cancels one order.
///
/// A cancel is NOT idempotent and is never retried automatically. A cancel that succeeded on a
/// timed-out first attempt reports OrderNotFound on the second, which reads as a failure and
/// tempts a caller into a third attempt, or worse into re-placing the order. So a failed cancel
/// is reported honestly and reconciliation establishes what actually happened.
///
/// The risk gate is deliberately NOT consulted. Reducing risk must never be blocked by a risk
/// rule; a platform that can stop you opening a position but not closing one is worse than one
/// with no limits at all.
/// </summary>
public sealed class CancelOrderHandler(
    BrokerLinkResolver linkResolver,
    IOrderRepository orders,
    IEventBus events,
    IAuditSink audit,
    IClock clock,
    ILogger<CancelOrderHandler> logger)
{
    public async Task<Result<PlaceOrderResult>> HandleAsync(CancelOrderCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var order = await orders.GetAsync(command.OrderId, ct);
        if (order is null || !string.Equals(order.TenantId, command.TenantId, StringComparison.Ordinal))
        {
            return new Error(ConnectorErrorCodes.OrderNotFound, $"No order '{command.OrderId}' exists for this account.");
        }

        if (order.IsTerminal)
        {
            // Already done. Reported as a success rather than an error: the caller wanted the
            // order not to be working, and it is not working.
            return new PlaceOrderResult(order.Id, order.ClientOrderId, order.BrokerOrderId, order.State, order.Status,
                $"Order was already {order.State}.");
        }

        if (order.BrokerOrderId is not { Length: > 0 } brokerOrderId)
        {
            return new Error(
                ConnectorErrorCodes.OrderNotFound,
                "This order has no broker id yet, so it cannot be cancelled. "
                + "It is being reconciled with your broker; try again shortly.");
        }

        var linkResult = await linkResolver.GetLinkAsync(command.TenantId, order.BrokerLinkId, ct);
        if (linkResult.IsFailure)
        {
            return Result<PlaceOrderResult>.Failure(linkResult.Error);
        }

        var connectorResult = await linkResolver.ConnectAsync(linkResult.Value, ct);
        if (connectorResult.IsFailure)
        {
            return Result<PlaceOrderResult>.Failure(connectorResult.Error);
        }

        await using var connector = connectorResult.Value;

        var ack = await connector.Orders.CancelAsync(brokerOrderId, ct);
        var at = clock.UtcNow;

        if (ack.IsFailure)
        {
            if (ack.Error.Code is ConnectorErrorCodes.Timeout or ConnectorErrorCodes.Unknown)
            {
                // We do not know whether the cancel landed. Recorded on the order so the UI can
                // show "cancelling"; the order itself stays working until the broker says
                // otherwise, because claiming it is cancelled when it may still fill is the
                // more dangerous lie.
                order.RecordAmendment(at, $"Cancel request timed out: {ack.Error.Message}", OrderActors.System);
                await orders.SaveAsync(order, ct);

                logger.LogWarning(
                    "Cancel of order {OrderId} timed out; leaving it working and deferring to reconciliation.",
                    order.Id);
            }
            else
            {
                order.RecordAmendment(at, $"Cancel rejected: {ack.Error}", OrderActors.Broker);
                await orders.SaveAsync(order, ct);
            }

            return Result<PlaceOrderResult>.Failure(ack.Error);
        }

        var state = ack.Value.Status.ToOrderState();
        if (state == OrderState.Cancelled)
        {
            order.MarkCancelled(at, ack.Value.Message, OrderActors.Broker, "Cancelled at the trader's request.");
        }
        else
        {
            // The broker accepted the cancel request but the order is not cancelled yet — a
            // pending cancel. Recorded, not asserted.
            order.RecordAmendment(at, $"Cancel accepted; broker reports {ack.Value.Status}.", OrderActors.Broker, ack.Value.Message);
        }

        await orders.SaveAsync(order, ct);

        await events.PublishAsync(
            new OrderStateChanged(
                order.Id,
                order.ClientOrderId,
                order.TenantId,
                order.UserId,
                order.BrokerLinkId,
                order.Instrument,
                order.State,
                order.Status,
                order.FilledQuantity,
                order.AveragePrice,
                order.StatusMessage,
                at),
            ct);

        await audit.RecordAsync(
            new AuditRecord
            {
                At = at,
                TenantId = command.TenantId,
                Actor = command.Actor,
                Action = "order.cancel",
                Subject = order.Id.ToString(),
                Detail = $"Cancel requested; order is now {order.State}.",
                Context = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["brokerOrderId"] = brokerOrderId,
                    ["clientOrderId"] = order.ClientOrderId.ToString(),
                },
            },
            ct);

        return new PlaceOrderResult(order.Id, order.ClientOrderId, order.BrokerOrderId, order.State, order.Status, ack.Value.Message);
    }
}
