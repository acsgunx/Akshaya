using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Paper;

/// <summary>
/// The order facet: manifest validation, session gating, and a thin pass to
/// <see cref="MatchingEngine"/>.
///
/// It holds no state of its own. The engine is the book, and a facet that cached orders beside
/// it would eventually disagree with it — which is exactly the bug the platform's "the broker
/// is the source of truth, not our copy" rule exists to prevent, and it would be embarrassing
/// to introduce it in the connector that is supposed to be a reference implementation.
/// </summary>
/// <param name="engine">The simulated venue.</param>
/// <param name="requireSession">
/// <c>ConnectorBase.RequireSession</c>. Passed as a delegate rather than re-implemented so
/// that Paper produces the identical SessionExpired / ReauthRequired split every other
/// connector does — the conformance suite checks for it.
/// </param>
/// <param name="validate">
/// <c>ConnectorBase.ValidateAgainstManifest</c>. Reused rather than re-derived so a manifest
/// edit changes behaviour here without anyone remembering to update this class.
/// </param>
public sealed class PaperOrders(
    MatchingEngine engine,
    Func<Result<BrokerSession>> requireSession,
    Func<PlaceOrderRequest, Result> validate) : IConnectorOrders
{
    /// <inheritdoc />
    public Task<Result<OrderAck>> PlaceAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var gate = Guard(request);
        return Task.FromResult(gate.IsFailure
            ? Result<OrderAck>.Failure(gate.Error)
            : engine.Place(request));
    }

    /// <inheritdoc />
    public Task<Result<OrderAck>> ModifyAsync(ModifyOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = requireSession();
        return Task.FromResult(session.IsFailure
            ? Result<OrderAck>.Failure(session.Error)
            : engine.Modify(request));
    }

    /// <inheritdoc />
    public Task<Result<OrderAck>> CancelAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var session = requireSession();
        return Task.FromResult(session.IsFailure
            ? Result<OrderAck>.Failure(session.Error)
            : engine.Cancel(brokerOrderId));
    }

    /// <inheritdoc />
    public Task<Result<int>> CancelAllAsync(CancellationToken ct = default)
    {
        var session = requireSession();
        return Task.FromResult(session.IsFailure
            ? Result<int>.Failure(session.Error)
            : engine.CancelAll());
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<BrokerOrder>>> GetOrdersAsync(
        OrderQuery query,
        CancellationToken ct = default)
    {
        var session = requireSession();
        return Task.FromResult(session.IsFailure
            ? Result<IReadOnlyList<BrokerOrder>>.Failure(session.Error)
            : Result<IReadOnlyList<BrokerOrder>>.Success(engine.Orders(query)));
    }

    /// <inheritdoc />
    public Task<Result<BrokerOrder>> GetOrderAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var session = requireSession();
        return Task.FromResult(session.IsFailure
            ? Result<BrokerOrder>.Failure(session.Error)
            : engine.Order(brokerOrderId));
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<BrokerTrade>>> GetTradesAsync(
        OrderQuery query,
        CancellationToken ct = default)
    {
        var session = requireSession();
        return Task.FromResult(session.IsFailure
            ? Result<IReadOnlyList<BrokerTrade>>.Failure(session.Error)
            : Result<IReadOnlyList<BrokerTrade>>.Success(engine.Trades(query)));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The manifest declares the basket ATOMIC, so this must be all-or-nothing and not a loop
    /// that leaves the trader half-hedged. Every leg is validated first; only if all of them
    /// pass does anything reach the book. That is stricter than most real brokers manage,
    /// which is the point of declaring it honestly in the manifest: a connector that loops
    /// declares <c>atomic: false</c> and the UI warns about partial execution.
    ///
    /// Note what atomicity here does NOT mean: legs still fill independently once accepted.
    /// Atomic acceptance is not atomic execution, and no venue offers the latter.
    /// </remarks>
    public Task<Result<IReadOnlyList<OrderAck>>> PlaceBasketAsync(
        IReadOnlyList<PlaceOrderRequest> requests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
        {
            return Task.FromResult(Result<IReadOnlyList<OrderAck>>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "A basket must contain at least one order.")));
        }

        for (var i = 0; i < requests.Count; i++)
        {
            var gate = Guard(requests[i]);
            if (gate.IsFailure)
            {
                // Name the leg. "Leg 4 of 12 was rejected" is actionable; "the basket was
                // rejected" sends the trader to check twelve order tickets by hand.
                return Task.FromResult(Result<IReadOnlyList<OrderAck>>.Failure(gate.Error with
                {
                    Message = $"Basket leg {i + 1} of {requests.Count}: {gate.Error.Message}",
                }));
            }
        }

        var acks = new List<OrderAck>(requests.Count);

        foreach (var request in requests)
        {
            var ack = engine.Place(request);
            if (ack.IsFailure)
            {
                // Should be unreachable: every leg passed the same validation a moment ago.
                // If it happens anyway, roll back what was accepted rather than leaving a
                // partial basket working.
                foreach (var placed in acks)
                {
                    _ = engine.Cancel(placed.BrokerOrderId);
                }

                return Task.FromResult(Result<IReadOnlyList<OrderAck>>.Failure(ack.Error));
            }

            acks.Add(ack.Value);
        }

        return Task.FromResult(Result<IReadOnlyList<OrderAck>>.Success(acks));
    }

    /// <inheritdoc />
    public Task<Result<MarginEstimate>> EstimateMarginAsync(
        PlaceOrderRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = requireSession();
        return Task.FromResult(session.IsFailure
            ? Result<MarginEstimate>.Failure(session.Error)
            : engine.EstimateMargin(request));
    }

    /// <inheritdoc />
    public Task<Result<ChargesEstimate>> EstimateChargesAsync(
        PlaceOrderRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = requireSession();
        return Task.FromResult(session.IsFailure
            ? Result<ChargesEstimate>.Failure(session.Error)
            : engine.EstimateCharges(request));
    }

    /// <summary>
    /// Session first, then manifest. The order matters: an expired session must report
    /// ReauthRequired even for an order the manifest would have rejected anyway, or the
    /// trader is told to fix the wrong thing.
    /// </summary>
    private Result Guard(PlaceOrderRequest request)
    {
        var session = requireSession();
        if (session.IsFailure)
        {
            return Result.Failure(session.Error);
        }

        return validate(request);
    }
}
