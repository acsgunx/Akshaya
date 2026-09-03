using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Domain;
using Akshaya.SharedKernel;
using FluentValidation;

namespace Akshaya.Modules.Trading.Application;

/// <summary>
/// A request to place one order, as the trading core receives it.
///
/// Distinct from <see cref="PlaceOrderRequest"/> — the connector contract — because it carries
/// the things a broker has no business knowing (tenant, user, which link, who asked) and omits
/// nothing the broker needs. The handler projects one onto the other exactly once.
/// </summary>
public sealed record PlaceOrderCommand
{
    public required string TenantId { get; init; }

    public required string UserId { get; init; }

    public required string BrokerLinkId { get; init; }

    /// <summary>
    /// Supplied by the CALLER so that a retry of the same user intent carries the same key.
    /// The handler generates one only when the caller did not, and a caller that generates a
    /// fresh id per HTTP retry has defeated the idempotency this exists to provide.
    /// </summary>
    public Guid? ClientOrderId { get; init; }

    public required InstrumentKey Instrument { get; init; }

    public required Side Side { get; init; }

    public required Quantity Quantity { get; init; }

    public required OrderType OrderType { get; init; }

    public required PositionEffect PositionEffect { get; init; }

    public TimeInForce TimeInForce { get; init; } = TimeInForce.Day;

    public OrderVariety Variety { get; init; } = OrderVariety.Regular;

    public Money? LimitPrice { get; init; }

    public Money? TriggerPrice { get; init; }

    public Quantity? DisclosedQuantity { get; init; }

    public DateOnly? GoodTillDate { get; init; }

    /// <summary>Strategy identifier. Several jurisdictions require automated orders to carry one.</summary>
    public string? AlgoId { get; init; }

    public string? Tag { get; init; }

    /// <summary>Who is asking. Recorded on every order event and every audit row.</summary>
    public string Actor { get; init; } = OrderActors.User;

    /// <summary>Projects onto the connector contract. The one place this mapping happens.</summary>
    public PlaceOrderRequest ToRequest(Guid clientOrderId) => new()
    {
        ClientOrderId = clientOrderId,
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
    };
}

/// <summary>
/// Structural validation only — the shape of the request, not whether it is a good idea.
///
/// The split matters. Anything a client could fix by reading the docs belongs here and returns
/// 400. Anything that depends on the account, the market or the broker belongs in the risk gate
/// and returns 422 with a named rule. Mixing them produces error messages that are technically
/// accurate and practically useless.
/// </summary>
public sealed class PlaceOrderCommandValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderCommandValidator()
    {
        RuleFor(c => c.TenantId).NotEmpty();
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.BrokerLinkId).NotEmpty();

        RuleFor(c => c.Quantity.Value)
            .GreaterThan(0m)
            .WithMessage("Quantity must be greater than zero. Direction is expressed by Side, never by a negative quantity.");

        RuleFor(c => c.Instrument.Symbol).NotEmpty();

        // A limit order with no limit price is the single most common malformed order, and the
        // broker's rejection for it is usually an opaque vendor code.
        RuleFor(c => c.LimitPrice)
            .NotNull()
            .When(c => c.OrderType is OrderType.Limit or OrderType.StopLimit)
            .WithMessage("A limit price is required for Limit and StopLimit orders.");

        RuleFor(c => c.TriggerPrice)
            .NotNull()
            .When(c => c.OrderType is OrderType.Stop or OrderType.StopLimit or OrderType.TrailingStop or OrderType.MarketIfTouched)
            .WithMessage("A trigger price is required for Stop, StopLimit, TrailingStop and MarketIfTouched orders.");

        RuleFor(c => c.GoodTillDate)
            .NotNull()
            .When(c => c.TimeInForce == TimeInForce.Gtd)
            .WithMessage("A good-till date is required when the time-in-force is Gtd.");

        RuleFor(c => c.LimitPrice!.Value.Amount)
            .GreaterThan(0m)
            .When(c => c.LimitPrice is not null)
            .WithMessage("A limit price must be positive.");

        RuleFor(c => c.TriggerPrice!.Value.Amount)
            .GreaterThan(0m)
            .When(c => c.TriggerPrice is not null)
            .WithMessage("A trigger price must be positive.");

        RuleFor(c => c.DisclosedQuantity!.Value.Value)
            .GreaterThan(0m)
            .LessThanOrEqualTo(c => c.Quantity.Value)
            .When(c => c.DisclosedQuantity is not null)
            .WithMessage("Disclosed quantity must be positive and no larger than the order quantity.");

        RuleFor(c => c.PositionEffect)
            .NotEqual(PositionEffect.None)
            .WithMessage("A position effect is required; the broker's manifest lists the ones it supports.");
    }
}

/// <summary>What the caller gets back from a successful placement.</summary>
/// <param name="OrderId">Our surrogate id; use it for modify, cancel and lookup.</param>
/// <param name="ClientOrderId">The idempotency key, echoed so the caller can reuse it on retry.</param>
/// <param name="BrokerOrderId">The broker's id, when it gave us one.</param>
/// <param name="State">Platform lifecycle state.</param>
/// <param name="Status">Canonical status for display.</param>
/// <param name="Message">Broker text, verbatim.</param>
public sealed record PlaceOrderResult(
    Guid OrderId,
    Guid ClientOrderId,
    string? BrokerOrderId,
    OrderState State,
    OrderStatus Status,
    string? Message);

/// <summary>A request to amend a live order.</summary>
public sealed record ModifyOrderCommand
{
    public required string TenantId { get; init; }

    public required string UserId { get; init; }

    /// <summary>Our surrogate order id, not the broker's.</summary>
    public required Guid OrderId { get; init; }

    public Quantity? Quantity { get; init; }

    public Money? LimitPrice { get; init; }

    public Money? TriggerPrice { get; init; }

    public OrderType? OrderType { get; init; }

    public TimeInForce? TimeInForce { get; init; }

    public Quantity? DisclosedQuantity { get; init; }

    public string Actor { get; init; } = OrderActors.User;

    /// <summary>True when the caller asked to change nothing at all.</summary>
    public bool IsEmpty => Quantity is null
                           && LimitPrice is null
                           && TriggerPrice is null
                           && OrderType is null
                           && TimeInForce is null
                           && DisclosedQuantity is null;
}

public sealed class ModifyOrderCommandValidator : AbstractValidator<ModifyOrderCommand>
{
    public ModifyOrderCommandValidator()
    {
        RuleFor(c => c.TenantId).NotEmpty();
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.OrderId).NotEmpty();

        RuleFor(c => c)
            .Must(c => !c.IsEmpty)
            .WithMessage("A modify must change at least one field.");

        RuleFor(c => c.Quantity!.Value.Value)
            .GreaterThan(0m)
            .When(c => c.Quantity is not null);

        RuleFor(c => c.LimitPrice!.Value.Amount)
            .GreaterThan(0m)
            .When(c => c.LimitPrice is not null);

        RuleFor(c => c.TriggerPrice!.Value.Amount)
            .GreaterThan(0m)
            .When(c => c.TriggerPrice is not null);
    }
}

/// <summary>A request to cancel one order.</summary>
public sealed record CancelOrderCommand
{
    public required string TenantId { get; init; }

    public required string UserId { get; init; }

    public required Guid OrderId { get; init; }

    public string Actor { get; init; } = OrderActors.User;
}

/// <summary>
/// A request to cancel everything working on one broker link, or on every link the user has.
///
/// The panic button. It is separate from a loop of cancels in the caller because brokers that
/// support a native cancel-all do it atomically and far faster, and because the partial-failure
/// story has to be reported honestly rather than hidden inside a retry.
/// </summary>
public sealed record CancelAllCommand
{
    public required string TenantId { get; init; }

    public required string UserId { get; init; }

    /// <summary>Null cancels across every usable link the user has.</summary>
    public string? BrokerLinkId { get; init; }

    public string Actor { get; init; } = OrderActors.User;
}

/// <summary>Outcome of a cancel-all against one broker link.</summary>
/// <param name="BrokerLinkId">Which link.</param>
/// <param name="Requested">How many working orders we knew about.</param>
/// <param name="Cancelled">How many the broker confirmed cancelled.</param>
/// <param name="UsedNativeCancelAll">
/// True when the broker cancelled them atomically. False means we looped, and a partial
/// outcome is possible — the UI must say so rather than reporting a clean number.
/// </param>
/// <param name="Error">Set when the link could not be processed at all.</param>
public sealed record CancelAllLinkResult(
    string BrokerLinkId,
    int Requested,
    int Cancelled,
    bool UsedNativeCancelAll,
    Error? Error);

/// <summary>Aggregate outcome of a cancel-all across every link it touched.</summary>
/// <param name="Links">Per-link outcomes, including the ones that failed.</param>
public sealed record CancelAllResult(IReadOnlyList<CancelAllLinkResult> Links)
{
    public int TotalCancelled => Links.Sum(l => l.Cancelled);

    public int TotalRequested => Links.Sum(l => l.Requested);

    /// <summary>True when at least one link failed or cancelled fewer orders than it was asked to.</summary>
    public bool IsPartial => Links.Any(l => l.Error is not null || l.Cancelled < l.Requested);
}

/// <summary>
/// A request to move an open position between margin products.
///
/// Carries the SIDE and the SOURCE product rather than inferring them, because a hedged
/// account can hold a long and a short in the same instrument under different products at
/// once, and a conversion that guesses which one the trader meant is a conversion of the
/// wrong position.
/// </summary>
public sealed record ConvertPositionCommand
{
    public required string TenantId { get; init; }

    public required string UserId { get; init; }

    public required string BrokerLinkId { get; init; }

    public required InstrumentKey Instrument { get; init; }

    /// <summary>BUY for a long position, SELL for a short one.</summary>
    public required Side Side { get; init; }

    /// <summary>Partial conversion is legitimate: take delivery of some, let the rest square off.</summary>
    public required Quantity Quantity { get; init; }

    /// <summary>The product the position is held under now.</summary>
    public required PositionEffect From { get; init; }

    /// <summary>The product to move it to.</summary>
    public required PositionEffect To { get; init; }

    public string Actor { get; init; } = OrderActors.User;
}
