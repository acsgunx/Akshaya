using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Trading.Application;

/// <summary>
/// Amends a live order.
///
/// A modify is a NEW ORDER for risk purposes. Re-running the gate against the amended values is
/// not belt-and-braces: the classic way past a price-band guard is to place a sane order and
/// then modify it to an insane price, and the classic way past a value cap is to place for one
/// lot and modify to a thousand. Anything the gate would have refused at placement it must
/// refuse at amendment.
///
/// It also checks the manifest's <see cref="OrderSpec.Modifiable"/> list, because brokers differ
/// wildly about which fields can change on a live order. A broker that silently ignores a field
/// it cannot amend leaves the trader believing a change took effect when it did not.
/// </summary>
public sealed class ModifyOrderHandler(
    IValidator<ModifyOrderCommand> validator,
    BrokerLinkResolver linkResolver,
    RiskGate riskGate,
    IRiskPolicyStore policies,
    IRiskSnapshotProvider snapshots,
    IOrderRepository orders,
    IEventBus events,
    IAuditSink audit,
    IClock clock,
    ILogger<ModifyOrderHandler> logger)
{
    /// <summary>
    /// Field names as they appear in a manifest's <c>orders.modifiable</c> list. Constants so a
    /// typo is a compile error here rather than a permanently-refused amendment in production.
    /// </summary>
    public static class ModifiableFields
    {
        public const string Quantity = "quantity";
        public const string LimitPrice = "limitPrice";
        public const string TriggerPrice = "triggerPrice";
        public const string OrderType = "orderType";
        public const string TimeInForce = "timeInForce";
        public const string DisclosedQuantity = "disclosedQuantity";
    }

    public async Task<Result<PlaceOrderResult>> HandleAsync(ModifyOrderCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var order = await orders.GetAsync(command.OrderId, ct);
        if (order is null || !string.Equals(order.TenantId, command.TenantId, StringComparison.Ordinal))
        {
            return new Error(ConnectorErrorCodes.OrderNotFound, $"No order '{command.OrderId}' exists for this account.");
        }

        if (!order.State.IsWorking())
        {
            return new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"Order {order.Id} is {order.State} and can no longer be modified.");
        }

        if (order.BrokerOrderId is not { Length: > 0 } brokerOrderId)
        {
            // We have not been given a broker id yet, so there is nothing to address the
            // amendment to. Reconciliation will supply one shortly; refusing is better than
            // guessing at an id.
            return new Error(
                ConnectorErrorCodes.OrderNotFound,
                "This order has not yet been acknowledged by the broker and cannot be modified. Try again shortly.");
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
        var manifest = connector.Manifest;

        var unsupported = UnsupportedFields(manifest.Orders, command);
        if (unsupported.Count > 0)
        {
            return new Error(
                ConnectorErrorCodes.NotSupported,
                $"This broker cannot modify {string.Join(", ", unsupported)} on a live order. "
                + "Cancel and replace instead.");
        }

        // The amended order, expressed as if it were being placed fresh. This is what the risk
        // gate judges.
        var amended = Amend(order.Request, command);
        var policy = await policies.GetAsync(command.TenantId, ct);
        var now = clock.UtcNow;

        var quote = await connector.MarketData.GetQuoteAsync(order.Instrument, ct);
        var instrument = await connector.Reference.ResolveAsync(order.Instrument, ct);
        var snapshot = await snapshots.GetAsync(command.TenantId, command.UserId, order.BrokerLinkId, ct);

        var decision = await riskGate.EvaluateAsync(
            new RiskEvaluationContext
            {
                TenantId = command.TenantId,
                UserId = command.UserId,
                BrokerLinkId = order.BrokerLinkId,
                ConnectorId = order.ConnectorId,
                Manifest = manifest,
                Request = amended,
                Policy = policy,
                At = now,
                Instrument = instrument.IsSuccess ? instrument.Value : null,
                LastTradedPrice = quote.IsSuccess ? quote.Value.LastPrice : null,
                Snapshot = snapshot,

                // An amendment to a working order is never treated as reducing exposure: the
                // safe direction is to apply every rule.
                IsReducingExposure = false,
            },
            ct);

        if (!decision.IsAllowed)
        {
            order.RecordAmendment(
                now,
                $"Amendment blocked by {decision.RuleName}: {decision.Reason}",
                OrderActors.RiskGate);

            await orders.SaveAsync(order, ct);
            return Result<PlaceOrderResult>.Failure(decision.ToError());
        }

        var request = new ModifyOrderRequest
        {
            BrokerOrderId = brokerOrderId,
            Quantity = command.Quantity,
            LimitPrice = command.LimitPrice,
            TriggerPrice = command.TriggerPrice,
            OrderType = command.OrderType,
            TimeInForce = command.TimeInForce,
            DisclosedQuantity = command.DisclosedQuantity,
        };

        var ack = await connector.Orders.ModifyAsync(request, ct);
        var at = clock.UtcNow;

        if (ack.IsFailure)
        {
            // A modify that times out is ambiguous in a much less dangerous way than a place:
            // the worst case is that the order is still working on its old terms, which is a
            // state the trader can see and act on. We record the uncertainty and leave the
            // order alone rather than marking it Unknown and alarming the blotter.
            order.RecordAmendment(at, $"Amendment failed: {ack.Error}", OrderActors.Broker);
            await orders.SaveAsync(order, ct);

            logger.LogWarning("Modify of order {OrderId} failed: {Error}", order.Id, ack.Error);
            return Result<PlaceOrderResult>.Failure(ack.Error);
        }

        order.RecordAmendment(at, Describe(command), command.Actor, ack.Value.Message);
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
                Action = "order.modify",
                Subject = order.Id.ToString(),
                Detail = Describe(command),
                Context = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["brokerOrderId"] = brokerOrderId,
                    ["clientOrderId"] = order.ClientOrderId.ToString(),
                },
            },
            ct);

        return new PlaceOrderResult(order.Id, order.ClientOrderId, order.BrokerOrderId, order.State, order.Status, ack.Value.Message);
    }

    /// <summary>
    /// Fields the caller wants to change that the manifest does not list as modifiable.
    ///
    /// An EMPTY modifiable list means the connector did not declare one, not that nothing can
    /// change. Reading it the other way would break every existing connector the moment this
    /// check shipped, so absence is treated as "the broker will tell us".
    /// </summary>
    private static IReadOnlyList<string> UnsupportedFields(OrderSpec spec, ModifyOrderCommand command)
    {
        if (spec.Modifiable.Count == 0)
        {
            return [];
        }

        var wanted = new List<string>(6);
        if (command.Quantity is not null) { wanted.Add(ModifiableFields.Quantity); }
        if (command.LimitPrice is not null) { wanted.Add(ModifiableFields.LimitPrice); }
        if (command.TriggerPrice is not null) { wanted.Add(ModifiableFields.TriggerPrice); }
        if (command.OrderType is not null) { wanted.Add(ModifiableFields.OrderType); }
        if (command.TimeInForce is not null) { wanted.Add(ModifiableFields.TimeInForce); }
        if (command.DisclosedQuantity is not null) { wanted.Add(ModifiableFields.DisclosedQuantity); }

        return [.. wanted.Where(f => !spec.Modifiable.Contains(f, StringComparer.OrdinalIgnoreCase))];
    }

    private static PlaceOrderRequest Amend(PlaceOrderRequest original, ModifyOrderCommand command) => original with
    {
        Quantity = command.Quantity ?? original.Quantity,
        LimitPrice = command.LimitPrice ?? original.LimitPrice,
        TriggerPrice = command.TriggerPrice ?? original.TriggerPrice,
        OrderType = command.OrderType ?? original.OrderType,
        TimeInForce = command.TimeInForce ?? original.TimeInForce,
        DisclosedQuantity = command.DisclosedQuantity ?? original.DisclosedQuantity,
    };

    private static string Describe(ModifyOrderCommand command)
    {
        var parts = new List<string>(6);
        if (command.Quantity is { } q) { parts.Add($"quantity -> {q}"); }
        if (command.LimitPrice is { } lp) { parts.Add($"limit -> {lp}"); }
        if (command.TriggerPrice is { } tp) { parts.Add($"trigger -> {tp}"); }
        if (command.OrderType is { } ot) { parts.Add($"type -> {ot}"); }
        if (command.TimeInForce is { } tif) { parts.Add($"tif -> {tif}"); }
        if (command.DisclosedQuantity is { } dq) { parts.Add($"disclosed -> {dq}"); }
        return parts.Count == 0 ? "no change" : string.Join(", ", parts);
    }
}
