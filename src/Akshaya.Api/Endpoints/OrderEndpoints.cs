using Akshaya.Api.Contracts;
using Akshaya.Api.Infrastructure;
using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Application;
using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using FluentValidation;

namespace Akshaya.Api.Endpoints;

/// <summary>
/// The order blotter's endpoints: place, amend, cancel, the panic button, lookup, and a
/// pre-trade cost estimate. Every handler here is a thin adapter — all of the actual policy
/// lives in <c>Akshaya.Modules.Trading.Application</c>; this file's job is HTTP shape only.
/// </summary>
public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/orders").WithTags("Orders");

        group.MapPost("/", async (
            PlaceOrderRequestDto request,
            ICurrentUserAccessor user,
            IValidator<PlaceOrderRequestDto> validator,
            PlaceOrderHandler handler,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return ProblemDetailsMapper.ValidationProblem(validation.Errors.Select(e => e.ErrorMessage));
            }

            var command = request.ToCommand(user.TenantId, user.UserId, OrderActors.User);
            var result = await handler.HandleAsync(command, ct);
            return result.ToHttp(OrderActionResponse.From);
        });

        group.MapPost("/{id:guid}/modify", async (
            Guid id,
            ModifyOrderRequestDto request,
            ICurrentUserAccessor user,
            IValidator<ModifyOrderRequestDto> validator,
            ModifyOrderHandler handler,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return ProblemDetailsMapper.ValidationProblem(validation.Errors.Select(e => e.ErrorMessage));
            }

            var command = request.ToCommand(user.TenantId, user.UserId, id, OrderActors.User);
            var result = await handler.HandleAsync(command, ct);
            return result.ToHttp(OrderActionResponse.From);
        });

        group.MapPost("/{id:guid}/cancel", async (
            Guid id,
            ICurrentUserAccessor user,
            CancelOrderHandler handler,
            CancellationToken ct) =>
        {
            var command = new CancelOrderCommand
            {
                TenantId = user.TenantId,
                UserId = user.UserId,
                OrderId = id,
                Actor = OrderActors.User,
            };

            var result = await handler.HandleAsync(command, ct);
            return result.ToHttp(OrderActionResponse.From);
        });

        group.MapPost("/cancel-all", async (
            CancelAllRequestDto request,
            ICurrentUserAccessor user,
            CancelAllHandler handler,
            CancellationToken ct) =>
        {
            var command = new CancelAllCommand
            {
                TenantId = user.TenantId,
                UserId = user.UserId,
                BrokerLinkId = request.BrokerLinkId,
                Actor = OrderActors.User,
            };

            var result = await handler.HandleAsync(command, ct);
            return result.ToHttp(CancelAllResponse.From);
        });

        group.MapGet("/", async (
            string? brokerLinkId,
            string? instrument,
            bool openOnly,
            bool unresolvedOnly,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            ICurrentUserAccessor user,
            IOrderRepository orders,
            CancellationToken ct) =>
        {
            InstrumentKey? instrumentKey = null;
            if (instrument is { Length: > 0 })
            {
                if (!InstrumentKey.TryParse(instrument, out var parsed))
                {
                    return ProblemDetailsMapper.ValidationProblem([$"'{instrument}' is not a valid instrument key."]);
                }

                instrumentKey = parsed;
            }

            var filter = new OrderFilter
            {
                TenantId = user.TenantId,
                UserId = user.UserId,
                BrokerLinkId = brokerLinkId,
                Instrument = instrumentKey,
                OpenOnly = openOnly,
                UnresolvedOnly = unresolvedOnly,
                From = from,
                To = to,
                Limit = limit is > 0 ? limit.Value : 200,
            };

            var found = await orders.ListAsync(filter, ct);
            return Results.Ok(found.Select(o => OrderDto.From(o)).ToArray());
        });

        // Fills, not orders. Deliberately BEFORE the "/{id:guid}" route below — a literal
        // segment and a route constraint do not collide in ASP.NET's matcher, but keeping the
        // literal first makes the intent unambiguous to the next person reading the file.
        group.MapGet("/trades", async (
            string? brokerLinkId,
            DateOnly? from,
            DateOnly? to,
            string? instrument,
            ICurrentUserAccessor user,
            IBrokerLinkStore links,
            BrokerLinkResolver linkResolver,
            CancellationToken ct) =>
        {
            InstrumentKey? instrumentKey = null;
            if (instrument is { Length: > 0 })
            {
                if (!InstrumentKey.TryParse(instrument, out var parsed))
                {
                    return ProblemDetailsMapper.ValidationProblem([$"'{instrument}' is not a valid instrument key."]);
                }

                instrumentKey = parsed;
            }

            IReadOnlyList<BrokerLink> targets;
            if (brokerLinkId is { Length: > 0 })
            {
                var one = await linkResolver.GetLinkAsync(user.TenantId, brokerLinkId, ct);
                if (one.IsFailure)
                {
                    return ProblemDetailsMapper.ToProblem(one.Error);
                }

                targets = [one.Value];
            }
            else
            {
                targets = [.. (await links.ListAsync(user.TenantId, user.UserId, ct)).Where(l => l.IsUsable)];
            }

            var query = new OrderQuery { From = from, To = to, Instrument = instrumentKey };

            var trades = new List<TradeDto>();
            var warnings = new List<string>();

            foreach (var link in targets)
            {
                var connectorResult = await linkResolver.ConnectAsync(link, ct);
                if (connectorResult.IsFailure)
                {
                    warnings.Add($"{link.Id}: {connectorResult.Error.Message}");
                    continue;
                }

                await using var connector = connectorResult.Value;

                var found = await connector.Orders.GetTradesAsync(query, ct);
                if (found.IsFailure)
                {
                    // Named and skipped, never fatal. See TradesResponse.Warnings.
                    warnings.Add($"{connector.Manifest.DisplayName}: {found.Error.Message}");
                    continue;
                }

                trades.AddRange(found.Value.Select(t => TradeDto.From(t, link.Id, link.ConnectorId)));
            }

            // Newest first: a fills list is read from the top, and the most recent execution is
            // the one someone checking "did that go through" is looking for.
            trades.Sort((a, b) => b.ExecutedAt.CompareTo(a.ExecutedAt));

            return Results.Ok(new TradesResponse(trades, warnings, warnings.Count > 0));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            ICurrentUserAccessor user,
            IOrderRepository orders,
            CancellationToken ct) =>
        {
            var order = await orders.GetAsync(id, ct);
            if (order is null || !string.Equals(order.TenantId, user.TenantId, StringComparison.Ordinal))
            {
                return ProblemDetailsMapper.ToProblem(new Error(
                    ConnectorErrorCodes.OrderNotFound,
                    $"No order '{id}' exists for this account."));
            }

            return Results.Ok(OrderDto.From(order, includeEvents: true));
        });

        group.MapPost("/estimate", async (
            PlaceOrderRequestDto request,
            ICurrentUserAccessor user,
            IValidator<PlaceOrderRequestDto> validator,
            BrokerLinkResolver linkResolver,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return ProblemDetailsMapper.ValidationProblem(validation.Errors.Select(e => e.ErrorMessage));
            }

            var linkResult = await linkResolver.GetLinkAsync(user.TenantId, request.BrokerLinkId, ct);
            if (linkResult.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(linkResult.Error);
            }

            var connectorResult = await linkResolver.ConnectAsync(linkResult.Value, ct);
            if (connectorResult.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(connectorResult.Error);
            }

            await using var connector = connectorResult.Value;

            // A throwaway client id: this order is never persisted or sent, only priced.
            var placeRequest = request
                .ToCommand(user.TenantId, user.UserId, OrderActors.User)
                .ToRequest(Guid.CreateVersion7());

            var warnings = new List<string>();

            Money? marginRequired = null;
            Money? marginAvailable = null;
            bool? isMarginSufficient = null;

            if (connector.Manifest.Orders.MarginEstimate)
            {
                var margin = await connector.Orders.EstimateMarginAsync(placeRequest, ct);
                if (margin.IsSuccess)
                {
                    marginRequired = margin.Value.Required;
                    marginAvailable = margin.Value.Available;
                    isMarginSufficient = margin.Value.IsSufficient;
                }
                else
                {
                    warnings.Add($"Margin could not be estimated: {margin.Error.Message}");
                }
            }
            else
            {
                warnings.Add("This broker does not offer a margin estimate.");
            }

            IReadOnlyList<ChargeLineDto> charges = [];
            Money? totalCharges = null;

            if (connector.Manifest.Orders.ChargesEstimate)
            {
                var estimate = await connector.Orders.EstimateChargesAsync(placeRequest, ct);
                if (estimate.IsSuccess)
                {
                    charges = [.. estimate.Value.Lines.Select(l => new ChargeLineDto(l.Name, l.Amount, l.Note))];
                    totalCharges = estimate.Value.Total;
                }
                else
                {
                    warnings.Add($"Charges could not be estimated: {estimate.Error.Message}");
                }
            }
            else
            {
                warnings.Add("This broker does not offer an itemised charges estimate.");
            }

            return Results.Ok(new OrderEstimateResponse(
                marginRequired,
                marginAvailable,
                isMarginSufficient,
                charges,
                totalCharges,
                warnings));
        });

        return app;
    }
}
