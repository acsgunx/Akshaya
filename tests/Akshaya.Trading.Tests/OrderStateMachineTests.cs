using Akshaya.Modules.Trading.Domain;
using FluentAssertions;
using Xunit;

namespace Akshaya.Trading.Tests;

/// <summary>
/// Every legal transition in <see cref="OrderStateMachine.Allowed"/> must actually be allowed,
/// and every transition NOT in the table must throw for our own code
/// (<see cref="OrderStateMachine.Transition"/>) while still following the wider reconciliation
/// rule (<see cref="OrderStateMachine.Reconcile"/>) except for the one resurrection case.
///
/// PREVENTS: a state machine that silently drifts from its own documentation. The table in
/// OrderStateMachine.cs is meant to be the single place a reviewer reads to know the whole
/// legal graph; a test that walks every entry in it is what keeps that claim honest as the
/// table is edited.
/// </summary>
public sealed class OrderStateMachineTests
{
    public static readonly IEnumerable<OrderState> AllStates = Enum.GetValues<OrderState>();

    /// <summary>Every (from, to) pair the table declares legal for our own code.</summary>
    public static IEnumerable<object[]> LegalTransitions()
    {
        foreach (var (from, targets) in OrderStateMachine.Allowed)
        {
            foreach (var to in targets)
            {
                yield return [from, to];
            }
        }
    }

    /// <summary>Every (from, to) pair NOT in the table — the full complement of the legal set.</summary>
    public static IEnumerable<object[]> IllegalTransitions()
    {
        foreach (var from in AllStates)
        {
            foreach (var to in AllStates)
            {
                if (!OrderStateMachine.CanTransition(from, to))
                {
                    yield return [from, to];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void Every_transition_the_table_allows_is_actually_allowed(OrderState from, OrderState to)
    {
        OrderStateMachine.CanTransition(from, to).Should().BeTrue();
        OrderStateMachine.Transition(from, to).Should().Be(to);
    }

    [Theory]
    [MemberData(nameof(IllegalTransitions))]
    public void Every_transition_the_table_omits_throws_for_our_own_code(OrderState from, OrderState to)
    {
        var act = () => OrderStateMachine.Transition(from, to);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{from}*{to}*");
    }

    [Theory]
    [InlineData(OrderState.Filled)]
    [InlineData(OrderState.Cancelled)]
    [InlineData(OrderState.Rejected)]
    [InlineData(OrderState.Expired)]
    public void Terminal_states_have_no_outgoing_transitions_for_our_own_code(OrderState terminal)
    {
        OrderStateMachine.Allowed[terminal].Should().BeEmpty();
        terminal.IsTerminal().Should().BeTrue();
    }

    [Fact]
    public void PendingSubmit_can_reach_RiskChecked_Rejected_Cancelled_or_Unknown_and_nothing_else()
    {
        OrderStateMachine.Allowed[OrderState.PendingSubmit].Should().BeEquivalentTo(
        [
            OrderState.RiskChecked,
            OrderState.Rejected,
            OrderState.Cancelled,
            OrderState.Unknown,
        ]);
    }

    [Fact]
    public void A_market_order_may_jump_straight_from_Submitted_to_Filled()
    {
        // Fast markets and market orders acknowledge and fill in a single payload; forcing an
        // Acknowledged hop we never actually observed would be inventing a fact.
        OrderStateMachine.CanTransition(OrderState.Submitted, OrderState.Filled).Should().BeTrue();
    }

    [Fact]
    public void PartiallyFilled_may_transition_to_itself_for_every_subsequent_partial_fill()
    {
        OrderStateMachine.CanTransition(OrderState.PartiallyFilled, OrderState.PartiallyFilled).Should().BeTrue();
    }

    [Theory]
    [InlineData(OrderState.PendingSubmit)]
    [InlineData(OrderState.RiskChecked)]
    [InlineData(OrderState.Submitted)]
    [InlineData(OrderState.Acknowledged)]
    [InlineData(OrderState.PartiallyFilled)]
    public void Any_non_terminal_state_may_become_Unknown_on_a_send_or_poll_timeout(OrderState from)
    {
        // Admitting ignorance must never be blocked by the state machine — this is what a
        // PlaceOrderHandler timeout relies on to record MarkUnknown from wherever it was.
        OrderStateMachine.CanTransition(from, OrderState.Unknown).Should().BeTrue();
    }

    [Fact]
    public void Unknown_can_be_resolved_to_any_concrete_outcome_by_reconciliation()
    {
        foreach (var to in OrderStateMachine.Allowed[OrderState.Unknown])
        {
            OrderStateMachine.CanTransition(OrderState.Unknown, to).Should().BeTrue();
        }

        // Unknown itself is not a resolution, and RiskChecked is a fact about US, never
        // something the broker reports back to us via Unknown.
        OrderStateMachine.Allowed[OrderState.Unknown].Should().NotContain(OrderState.Unknown);
        OrderStateMachine.Allowed[OrderState.Unknown].Should().NotContain(OrderState.RiskChecked);
        OrderStateMachine.Allowed[OrderState.Unknown].Should().NotContain(OrderState.PendingSubmit);
    }

    // ── Reconciliation: wider table, one refusal ──────────────────────────────────────────

    public static IEnumerable<object[]> AllStatePairs()
    {
        foreach (var from in AllStates)
        {
            foreach (var to in AllStates)
            {
                yield return [from, to];
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllStatePairs))]
    public void Reconciliation_allows_everything_except_resurrecting_a_filled_order_into_working(
        OrderState from,
        OrderState to)
    {
        var isResurrection = from == OrderState.Filled && to.IsWorking();

        OrderStateMachine.CanReconcile(from, to).Should().Be(!isResurrection);

        if (isResurrection)
        {
            var act = () => OrderStateMachine.Reconcile(from, to);
            act.Should().Throw<InvalidOperationException>();
        }
        else
        {
            OrderStateMachine.Reconcile(from, to).Should().Be(to);
        }
    }

    [Theory]
    [InlineData(OrderState.Submitted)]
    [InlineData(OrderState.Acknowledged)]
    [InlineData(OrderState.PartiallyFilled)]
    public void A_filled_order_cannot_be_reconciled_back_into_a_working_state(OrderState workingState)
    {
        workingState.IsWorking().Should().BeTrue("the test itself must exercise a working state");
        OrderStateMachine.CanReconcile(OrderState.Filled, workingState).Should().BeFalse();
    }

    [Fact]
    public void A_filled_order_can_still_be_reconciled_to_Filled_itself_or_to_other_terminal_states()
    {
        // A fill does not un-happen, but the broker may still correct which terminal outcome
        // it actually was (a corrected settlement record, for instance).
        OrderStateMachine.CanReconcile(OrderState.Filled, OrderState.Filled).Should().BeTrue();
        OrderStateMachine.CanReconcile(OrderState.Filled, OrderState.Cancelled).Should().BeTrue();
        OrderStateMachine.CanReconcile(OrderState.Filled, OrderState.Rejected).Should().BeTrue();
        OrderStateMachine.CanReconcile(OrderState.Filled, OrderState.Expired).Should().BeTrue();
    }

    // ── Canonical status projection ───────────────────────────────────────────────────────

    [Fact]
    public void RiskChecked_projects_to_PendingSubmit_because_nothing_has_been_sent_yet()
    {
        OrderState.RiskChecked.ToCanonicalStatus().Should().Be(Akshaya.SharedKernel.OrderStatus.PendingSubmit);
    }

    [Fact]
    public void Acknowledged_projects_to_Open_because_the_broker_confirmed_it_is_live_at_the_venue()
    {
        OrderState.Acknowledged.ToCanonicalStatus().Should().Be(Akshaya.SharedKernel.OrderStatus.Open);
    }

    [Fact]
    public void A_broker_reported_Open_status_lifts_back_to_Acknowledged_never_to_RiskChecked()
    {
        // The broker never tells us RiskChecked — that is a fact about us, not about it — so
        // the lift from OrderStatus must never produce it.
        Akshaya.SharedKernel.OrderStatus.Open.ToOrderState().Should().Be(OrderState.Acknowledged);
    }
}
