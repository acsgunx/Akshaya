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

        // NULLABLE bools, not bare ones. A non-nullable `bool` parameter is a REQUIRED query
        // string value in minimal APIs, so `GET /api/orders?openOnly=false` — exactly what the
        // web client sends — threw "Required parameter bool unresolvedOnly was not provided"
        // and returned a 500. The blotter showed "Could not load orders" and nothing else.
        //
        // Filters are optional by nature: omitting one means "do not filter", not "reject the
        // request". Both default to false below.
        group.MapGet("/", async (
            string? brokerLinkId,
            string? instrument,
            bool? openOnly,
            bool? unresolvedOnly,
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
                OpenOnly = openOnly ?? false,
                UnresolvedOnly = unresolvedOnly ?? false,
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

            // EVERY BROKER AT ONCE — the same rule BlendedPortfolioService states as its first
            // one, and for the same reason. This is the only fan-out in the API that used to
            // walk its links in sequence, so five linked brokers at 400ms each cost two
            // seconds of spinner rather than 400ms. Nothing about a fills list makes one
            // broker's answer depend on another's.
            var perLink = await Task.WhenAll(targets.Select(link => FetchTradesAsync(link, query, linkResolver, ct)));

            var trades = new List<TradeDto>();
            var warnings = new List<string>();

            // Merged in the order the links were listed, so the response is stable across calls
            // rather than ordered by whichever broker happened to answer first.
            foreach (var (linkTrades, warning) in perLink)
            {
                trades.AddRange(linkTrades);

                if (warning is not null)
                {
                    warnings.Add(warning);
                }
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

            // Two independent broker calls, started together. This estimate runs while the
            // trader is looking at the order ticket with the quantity box still focused, so its
            // latency is felt directly — and margin does not depend on charges.
            var marginTask = connector.Manifest.Orders.MarginEstimate
                ? connector.Orders.EstimateMarginAsync(placeRequest, ct)
                : null;

            var chargesTask = connector.Manifest.Orders.ChargesEstimate
                ? connector.Orders.EstimateChargesAsync(placeRequest, ct)
                : null;

            if (marginTask is not null || chargesTask is not null)
            {
                await Task.WhenAll(new Task?[] { marginTask, chargesTask }.OfType<Task>());
            }

            Money? marginRequired = null;
            Money? marginAvailable = null;
            bool? isMarginSufficient = null;

            if (marginTask is null)
            {
                warnings.Add("This broker does not offer a margin estimate.");
            }
            else
            {
                var margin = await marginTask;
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

            IReadOnlyList<ChargeLineDto> charges = [];
            Money? totalCharges = null;

            if (chargesTask is null)
            {
                warnings.Add("This broker does not offer an itemised charges estimate.");
            }
            else
            {
                var estimate = await chargesTask;
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

    /// <summary>
    /// One link's fills, with its failure captured rather than thrown.
    ///
    /// A broker that is down must cost this endpoint a WARNING and nothing else — that is the
    /// existing contract on <see cref="TradesResponse.Warnings"/>. Because the links now run
    /// concurrently, that has to hold per task: an exception escaping here would fault the
    /// whole <c>Task.WhenAll</c> and blank a blotter that four other brokers had answered.
    /// </summary>
    private static async Task<(IReadOnlyList<TradeDto> Trades, string? Warning)> FetchTradesAsync(
        BrokerLink link,
        OrderQuery query,
        BrokerLinkResolver linkResolver,
        CancellationToken ct)
    {
        try
        {
            var connectorResult = await linkResolver.ConnectAsync(link, ct);
            if (connectorResult.IsFailure)
            {
                return ([], $"{link.Id}: {connectorResult.Error.Message}");
            }

            await using var connector = connectorResult.Value;

            var found = await connector.Orders.GetTradesAsync(query, ct);
            return found.IsFailure
                ? ([], $"{connector.Manifest.DisplayName}: {found.Error.Message}")
                : ([.. found.Value.Select(t => TradeDto.From(t, link.Id, link.ConnectorId))], null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away. Propagate as itself rather than reporting it as a broker
            // problem the user could act on.
            throw;
        }
        catch (Exception ex)
        {
            return ([], $"{link.Id}: {ex.Message}");
        }
    }
}
