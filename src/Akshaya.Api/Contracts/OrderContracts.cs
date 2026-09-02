using Akshaya.Modules.Trading.Application;
using Akshaya.Modules.Trading.Domain;
using Akshaya.SharedKernel;
using FluentValidation;

namespace Akshaya.Api.Contracts;

/// <summary>
/// A request to place an order.
///
/// Note what the client does NOT send: a tenant, a user, or anything about the broker beyond
/// which link to use. Identity comes from the authenticated principal and never from the body —
/// a body-supplied tenant id is an authorisation bypass with extra steps.
/// </summary>
public sealed record PlaceOrderRequestDto
{
    public required string BrokerLinkId { get; init; }

    /// <summary>
    /// Optional idempotency key. Send the SAME value when retrying the same user intent and the
    /// API returns the original order instead of placing a second one. Generate a new one per
    /// intent, never per HTTP attempt.
    /// </summary>
    public Guid? ClientOrderId { get; init; }

    /// <summary>Canonical instrument key, e.g. <c>XNSE:INFY:Equity</c>.</summary>
    public required InstrumentKey Instrument { get; init; }

    public required Side Side { get; init; }

    public required Quantity Quantity { get; init; }

    public required OrderType OrderType { get; init; }

    /// <summary>Which of the broker's products this is. The manifest lists the supported combinations.</summary>
    public required PositionEffect PositionEffect { get; init; }

    public TimeInForce TimeInForce { get; init; } = TimeInForce.Day;

    public OrderVariety Variety { get; init; } = OrderVariety.Regular;

    public Money? LimitPrice { get; init; }

    public Money? TriggerPrice { get; init; }

    public Quantity? DisclosedQuantity { get; init; }

    public DateOnly? GoodTillDate { get; init; }

    /// <summary>Strategy identifier. Required by some regulators for automated order flow.</summary>
    public string? AlgoId { get; init; }

    public string? Tag { get; init; }

    /// <summary>Projects onto the trading core's command. Identity is supplied by the endpoint.</summary>
    public PlaceOrderCommand ToCommand(string tenantId, string userId, string actor) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        BrokerLinkId = BrokerLinkId,
        ClientOrderId = ClientOrderId,
        Instrument = Instrument,
        Side = Side,
        Quantity = Quantity,
        OrderType = OrderType,
        PositionEffect = PositionEffect,
        TimeInForce = TimeInForce,
        Variety = Variety,
        LimitPrice = LimitPrice,
        TriggerPrice = TriggerPrice,
        DisclosedQuantity = DisclosedQuantity,
        GoodTillDate = GoodTillDate,
        AlgoId = AlgoId,
        Tag = Tag,
        Actor = actor,
    };
}

/// <summary>
/// Shape-only validation at the edge.
///
/// It deliberately duplicates very little of the trading core's validator: this one catches
/// what a client can fix by reading the docs and returns 400, and the core's runs again inside
/// the handler because the handler is also reachable from tests, from a scheduler, and one day
/// from a gRPC surface. Validation that lives only at the HTTP edge is validation that a second
/// entry point silently skips.
/// </summary>
public sealed class PlaceOrderRequestDtoValidator : AbstractValidator<PlaceOrderRequestDto>
{
    public PlaceOrderRequestDtoValidator()
    {
        RuleFor(r => r.BrokerLinkId).NotEmpty().WithMessage("A broker link id is required.");

        RuleFor(r => r.Quantity.Value)
            .GreaterThan(0m)
            .WithMessage("Quantity must be greater than zero; direction is expressed by 'side'.");

        RuleFor(r => r.LimitPrice)
            .NotNull()
            .When(r => r.OrderType is OrderType.Limit or OrderType.StopLimit)
            .WithMessage("A limit price is required for limit and stop-limit orders.");

        RuleFor(r => r.TriggerPrice)
            .NotNull()
            .When(r => r.OrderType is OrderType.Stop or OrderType.StopLimit or OrderType.TrailingStop or OrderType.MarketIfTouched)
            .WithMessage("A trigger price is required for stop, stop-limit, trailing-stop and market-if-touched orders.");

        RuleFor(r => r.GoodTillDate)
            .NotNull()
            .When(r => r.TimeInForce == TimeInForce.Gtd)
            .WithMessage("A good-till date is required when timeInForce is 'gtd'.");

        RuleFor(r => r.PositionEffect)
            .NotEqual(PositionEffect.None)
            .WithMessage("A position effect is required; GET /api/connectors/{id} lists the supported ones.");
    }
}

/// <summary>A request to amend a live order. Every field is optional; at least one must be present.</summary>
public sealed record ModifyOrderRequestDto
{
    public Quantity? Quantity { get; init; }

    public Money? LimitPrice { get; init; }

    public Money? TriggerPrice { get; init; }

    public OrderType? OrderType { get; init; }

    public TimeInForce? TimeInForce { get; init; }

    public Quantity? DisclosedQuantity { get; init; }

    public ModifyOrderCommand ToCommand(string tenantId, string userId, Guid orderId, string actor) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        OrderId = orderId,
        Quantity = Quantity,
        LimitPrice = LimitPrice,
        TriggerPrice = TriggerPrice,
        OrderType = OrderType,
        TimeInForce = TimeInForce,
        DisclosedQuantity = DisclosedQuantity,
        Actor = actor,
    };
}

public sealed class ModifyOrderRequestDtoValidator : AbstractValidator<ModifyOrderRequestDto>
{
    public ModifyOrderRequestDtoValidator()
    {
        RuleFor(r => r)
            .Must(r => r.Quantity is not null
                       || r.LimitPrice is not null
                       || r.TriggerPrice is not null
                       || r.OrderType is not null
                       || r.TimeInForce is not null
                       || r.DisclosedQuantity is not null)
            .WithMessage("A modify must change at least one field.");

        RuleFor(r => r.Quantity!.Value.Value).GreaterThan(0m).When(r => r.Quantity is not null);
        RuleFor(r => r.LimitPrice!.Value.Amount).GreaterThan(0m).When(r => r.LimitPrice is not null);
        RuleFor(r => r.TriggerPrice!.Value.Amount).GreaterThan(0m).When(r => r.TriggerPrice is not null);
    }
}

/// <summary>Cancel every working order, on one link or on all of them.</summary>
public sealed record CancelAllRequestDto
{
    /// <summary>Null cancels across every usable link the user has. Say so explicitly in the UI.</summary>
    public string? BrokerLinkId { get; init; }
}

/// <summary>One step in an order's life, as the API exposes it.</summary>
/// <param name="At">When.</param>
/// <param name="Actor">Who or what caused it.</param>
/// <param name="State">Platform lifecycle state entered.</param>
/// <param name="Status">Canonical status for display.</param>
/// <param name="Note">Our explanation of the step.</param>
/// <param name="RawBrokerPayload">
/// Exactly what the broker sent. Exposed on purpose: when a trader disputes a fill, the answer
/// is the broker's own words, not our paraphrase of them.
/// </param>
public sealed record OrderEventDto(
    DateTimeOffset At,
    string Actor,
    OrderState State,
    OrderStatus Status,
    string? Note,
    string? RawBrokerPayload);

/// <summary>An order as the API exposes it.</summary>
public sealed record OrderDto
{
    public required Guid Id { get; init; }

    public required Guid ClientOrderId { get; init; }

    public required string BrokerLinkId { get; init; }

    /// <summary>Opaque connector id, for grouping and for the account label. Never branch on it.</summary>
    public required string ConnectorId { get; init; }

    public string? BrokerOrderId { get; init; }

    public required InstrumentKey Instrument { get; init; }

    public required Side Side { get; init; }

    public required Quantity Quantity { get; init; }

    public required Quantity FilledQuantity { get; init; }

    public required Quantity PendingQuantity { get; init; }

    public required OrderType OrderType { get; init; }

    public required PositionEffect PositionEffect { get; init; }

    public required TimeInForce TimeInForce { get; init; }

    public required OrderVariety Variety { get; init; }

    public Money? LimitPrice { get; init; }

    public Money? TriggerPrice { get; init; }

    public Money? AveragePrice { get; init; }

    /// <summary>Platform lifecycle state — richer than <see cref="Status"/>, and what the blotter colours on.</summary>
    public required OrderState State { get; init; }

    public required OrderStatus Status { get; init; }

    /// <summary>Broker text, verbatim. Show it unedited.</summary>
    public string? StatusMessage { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public IReadOnlyList<OrderEventDto> Events { get; init; } = [];

    /// <summary>
    /// True when the platform cannot vouch for this order's state and reconciliation is working
    /// on it. The UI must show "checking with your broker" rather than a status — and must not
    /// offer a resubmit button.
    /// </summary>
    public bool IsUnresolved => State == OrderState.Unknown;

    /// <summary>Projects the aggregate. Events are included only where the caller asked for them.</summary>
    public static OrderDto From(Order order, bool includeEvents = false)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new OrderDto
        {
            Id = order.Id,
            ClientOrderId = order.ClientOrderId,
            BrokerLinkId = order.BrokerLinkId,
            ConnectorId = order.ConnectorId,
            BrokerOrderId = order.BrokerOrderId,
            Instrument = order.Instrument,
            Side = order.Request.Side,
            Quantity = order.Request.Quantity,
            FilledQuantity = order.FilledQuantity,
            PendingQuantity = order.PendingQuantity,
            OrderType = order.Request.OrderType,
            PositionEffect = order.Request.PositionEffect,
            TimeInForce = order.Request.TimeInForce,
            Variety = order.Request.Variety,
            LimitPrice = order.Request.LimitPrice,
            TriggerPrice = order.Request.TriggerPrice,
            AveragePrice = order.AveragePrice,
            State = order.State,
            Status = order.Status,
            StatusMessage = order.StatusMessage,
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
            Events = includeEvents
                ? [.. order.Events.Select(e => new OrderEventDto(e.At, e.Actor, e.State, e.Status, e.Note, e.RawBrokerPayload))]
                : [],
        };
    }
}

/// <summary>The result of placing, modifying or cancelling — thin, because the blotter is the truth.</summary>
/// <param name="OrderId">Our surrogate id. Use it for modify, cancel and lookup.</param>
/// <param name="ClientOrderId">Idempotency key. Reuse it verbatim on a retry.</param>
/// <param name="BrokerOrderId">The broker's id, once it has given us one.</param>
/// <param name="State">Platform lifecycle state.</param>
/// <param name="Status">Canonical status.</param>
/// <param name="Message">Broker text, verbatim.</param>
public sealed record OrderActionResponse(
    Guid OrderId,
    Guid ClientOrderId,
    string? BrokerOrderId,
    OrderState State,
    OrderStatus Status,
    string? Message)
{
    public static OrderActionResponse From(PlaceOrderResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new OrderActionResponse(
            result.OrderId,
            result.ClientOrderId,
            result.BrokerOrderId,
            result.State,
            result.Status,
            result.Message);
    }
}

/// <summary>What a cancel-all achieved, per link and in total.</summary>
/// <param name="Links">Per-link outcomes, failures included.</param>
/// <param name="TotalRequested">Working orders we knew about.</param>
/// <param name="TotalCancelled">Orders the brokers confirmed cancelled.</param>
/// <param name="IsPartial">
/// True when something did not get cancelled, or when a broker had no atomic cancel-all and we
/// looped. The UI MUST surface this — reporting a clean number after a partial sweep is the
/// failure mode that leaves someone exposed without knowing it.
/// </param>
public sealed record CancelAllResponse(
    IReadOnlyList<CancelAllLinkResponse> Links,
    int TotalRequested,
    int TotalCancelled,
    bool IsPartial)
{
    public static CancelAllResponse From(CancelAllResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CancelAllResponse(
            [.. result.Links.Select(l => new CancelAllLinkResponse(
                l.BrokerLinkId,
                l.Requested,
                l.Cancelled,
                l.UsedNativeCancelAll,
                l.Error?.Message))],
            result.TotalRequested,
            result.TotalCancelled,
            result.IsPartial);
    }
}

/// <summary>Cancel-all outcome for one link.</summary>
/// <param name="BrokerLinkId">Which link.</param>
/// <param name="Requested">Working orders on it.</param>
/// <param name="Cancelled">Confirmed cancelled.</param>
/// <param name="Atomic">False means we looped and a partial result is possible.</param>
/// <param name="Error">Why the link failed entirely, if it did.</param>
public sealed record CancelAllLinkResponse(
    string BrokerLinkId,
    int Requested,
    int Cancelled,
    bool Atomic,
    string? Error);

/// <summary>
/// A pre-trade cost estimate: margin required plus itemised charges.
///
/// Both halves are optional because the manifest says whether the broker offers them. A client
/// renders whichever it gets and must not fabricate the other — an invented brokerage figure is
/// worse than a blank one.
/// </summary>
/// <param name="MarginRequired">Capital the broker will block.</param>
/// <param name="MarginAvailable">Capital available, when the broker reports it.</param>
/// <param name="IsMarginSufficient">Null when availability is unknown.</param>
/// <param name="Charges">Itemised charge lines, in the order's own currency.</param>
/// <param name="TotalCharges">Sum of the lines.</param>
/// <param name="Warnings">What the broker could not estimate, and why.</param>
public sealed record OrderEstimateResponse(
    Money? MarginRequired,
    Money? MarginAvailable,
    bool? IsMarginSufficient,
    IReadOnlyList<ChargeLineDto> Charges,
    Money? TotalCharges,
    IReadOnlyList<string> Warnings);

/// <summary>One named charge. A list, not fixed fields: every market itemises differently.</summary>
/// <param name="Name">The broker's own label for the charge.</param>
/// <param name="Amount">How much.</param>
/// <param name="Note">Any qualification the broker attached.</param>
public sealed record ChargeLineDto(string Name, Money Amount, string? Note);
