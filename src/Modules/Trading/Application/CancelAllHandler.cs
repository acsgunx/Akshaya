using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Trading.Application;

/// <summary>
/// Cancels everything working, on one broker link or on all of them.
///
/// THE PANIC BUTTON, and it is written to behave like one:
///
///  * It uses the broker's NATIVE cancel-all when the manifest declares one. Native is atomic
///    and fast; a loop of individual cancels against a broker with a two-per-second order limit
///    takes a minute to clear forty orders, and the market does not wait a minute.
///  * When it has to loop, it says so. <see cref="CancelAllLinkResult.UsedNativeCancelAll"/> is
///    false and the UI must warn that some orders may still be live — reporting a clean number
///    after a partial sweep is the failure mode that gets someone hurt.
///  * One dead broker does not stop the others. Each link is attempted independently and its
///    error is reported alongside the successes, because in a panic the user needs the four
///    links that worked far more than they need a single error page.
///  * It never consults the risk gate. Reducing exposure is never blocked.
/// </summary>
public sealed class CancelAllHandler(
    BrokerLinkResolver linkResolver,
    IBrokerLinkStore links,
    IOrderRepository orders,
    IEventBus events,
    IAuditSink audit,
    IClock clock,
    ILogger<CancelAllHandler> logger)
{
    public async Task<Result<CancelAllResult>> HandleAsync(CancelAllCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        IReadOnlyList<BrokerLink> targets;
        if (command.BrokerLinkId is { Length: > 0 } linkId)
        {
            var one = await linkResolver.GetLinkAsync(command.TenantId, linkId, ct);
            if (one.IsFailure)
            {
                return Result<CancelAllResult>.Failure(one.Error);
            }

            targets = [one.Value];
        }
        else
        {
            targets = [.. (await links.ListAsync(command.TenantId, command.UserId, ct)).Where(l => l.IsUsable)];
        }

        // Sequential, not parallel. A panic cancel that fans out across five brokers at once
        // hits every one of their order-rate limits simultaneously, and a rate-limited cancel
        // is a cancel that did not happen.
        var results = new List<CancelAllLinkResult>(targets.Count);
        foreach (var link in targets)
        {
            results.Add(await CancelLinkAsync(command, link, ct));
        }

        var result = new CancelAllResult(results);

        await audit.RecordAsync(
            new AuditRecord
            {
                At = clock.UtcNow,
                TenantId = command.TenantId,
                Actor = command.Actor,
                Action = "order.cancel_all",
                Subject = command.BrokerLinkId ?? "all-links",
                Succeeded = !result.IsPartial,
                Detail = $"Cancelled {result.TotalCancelled} of {result.TotalRequested} working order(s) "
                         + $"across {results.Count} link(s).",
            },
            ct);

        return result;
    }

    private async Task<CancelAllLinkResult> CancelLinkAsync(
        CancelAllCommand command,
        BrokerLink link,
        CancellationToken ct)
    {
        var working = await orders.ListAsync(
            new OrderFilter
            {
                TenantId = command.TenantId,
                UserId = command.UserId,
                BrokerLinkId = link.Id,
                OpenOnly = true,
                Limit = int.MaxValue,
            },
            ct);

        var connectorResult = await linkResolver.ConnectAsync(link, ct);
        if (connectorResult.IsFailure)
        {
            return new CancelAllLinkResult(link.Id, working.Count, 0, false, connectorResult.Error);
        }

        await using var connector = connectorResult.Value;

        if (connector.Manifest.Orders.CancelAll)
        {
            var native = await connector.Orders.CancelAllAsync(ct);
            if (native.IsFailure)
            {
                return new CancelAllLinkResult(link.Id, working.Count, 0, true, native.Error);
            }

            // The broker's count is authoritative — it may have had working orders we did not
            // know about, and it may have had fewer. We record OUR orders as cancelled only
            // when the broker confirms them, which reconciliation does moments later.
            foreach (var order in working)
            {
                order.RecordAmendment(
                    clock.UtcNow,
                    "Included in a broker-side cancel-all; awaiting confirmation.",
                    command.Actor);

                await orders.SaveAsync(order, ct);
            }

            return new CancelAllLinkResult(link.Id, working.Count, native.Value, true, null);
        }

        // No native cancel-all: loop, and be honest that the result can be partial.
        var cancelled = 0;
        foreach (var order in working)
        {
            if (order.BrokerOrderId is not { Length: > 0 } brokerOrderId)
            {
                continue;
            }

            var ack = await connector.Orders.CancelAsync(brokerOrderId, ct);
            var at = clock.UtcNow;

            if (ack.IsFailure)
            {
                logger.LogWarning(
                    "Cancel-all: order {OrderId} on link {LinkId} could not be cancelled: {Error}",
                    order.Id,
                    link.Id,
                    ack.Error);

                order.RecordAmendment(at, $"Cancel-all failed for this order: {ack.Error}", OrderActors.Broker);
                await orders.SaveAsync(order, ct);
                continue;
            }

            if (ack.Value.Status.ToOrderState() == OrderState.Cancelled)
            {
                order.MarkCancelled(at, ack.Value.Message, OrderActors.Broker, "Cancelled by cancel-all.");
                cancelled++;
            }
            else
            {
                order.RecordAmendment(at, $"Cancel-all accepted; broker reports {ack.Value.Status}.", OrderActors.Broker);
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
        }

        return new CancelAllLinkResult(link.Id, working.Count, cancelled, false, null);
    }
}
