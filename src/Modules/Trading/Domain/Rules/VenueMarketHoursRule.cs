using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Domain.Rules;

/// <summary>
/// Refuses an order the venue cannot possibly accept right now.
///
/// PREVENTS: the overnight order that nobody is watching. A trader submits at 21:00 local time,
/// the broker's REST endpoint returns 200 because it is only a queue, and the order rests
/// unexamined until it fires into the open — often into a gap, at a price nobody would have
/// chosen with the morning's information. Worse is the variant where the broker silently drops
/// it: the trader believes they are positioned for the open and they are not.
///
/// The rule does NOT simply ban out-of-hours orders. After-market orders are a real product and
/// pre-open auctions are a real venue state. What it does is require that an out-of-hours order
/// be DELIBERATE — an after-market variety the broker actually supports, an at-the-open
/// time-in-force, or a tenant that has explicitly opted in — rather than a mistake about what
/// time it is somewhere else in the world.
///
/// Venue state comes from <see cref="ITradingCalendar"/>, which is reference data. Nothing here
/// knows what a specific venue's hours are; adding a market is a calendar row.
/// </summary>
public sealed class VenueMarketHoursRule(ITradingCalendar calendar) : IRiskRule
{
    private readonly ITradingCalendar _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));

    public string Name => RiskRuleNames.VenueMarketHours;

    public int Order => 60;

    public Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var venue = context.Request.Instrument.Venue;
        var state = _calendar.GetState(venue, context.At);

        if (state == VenueState.Open)
        {
            return Task.FromResult(RiskDecision.Allow());
        }

        // A deliberate after-market order, on a broker that declares it can queue one.
        if (context.Request.Variety == OrderVariety.AfterMarket
            && context.Manifest.Orders.Varieties.Contains(OrderVariety.AfterMarket))
        {
            return Task.FromResult(RiskDecision.Allow());
        }

        // A deliberate auction order during the pre-open window.
        if (state == VenueState.PreOpen && context.Request.TimeInForce == TimeInForce.AtTheOpen)
        {
            return Task.FromResult(RiskDecision.Allow());
        }

        if (context.Policy.AllowOrdersWhenVenueClosed)
        {
            // The tenant has opted in to letting the broker queue or refuse. We surface the
            // broker's answer rather than pre-empting it, which is the right behaviour for
            // venues whose queuing rules we do not model.
            return Task.FromResult(RiskDecision.Allow());
        }

        var nextOpen = _calendar.NextOpen(venue, context.At);
        var when = nextOpen is { } open
            ? $" It next opens at {open:u}."
            : string.Empty;

        return Task.FromResult(RiskDecision.Deny(
            Name,
            $"{venue} is {Describe(state)}.{when}",
            // MarketClosed maps to 409 rather than 422: the request is well-formed and will be
            // valid again later, which is exactly what a conflict-with-current-state means.
            ConnectorErrorCodes.MarketClosed,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["venue"] = venue.ToString(),
                ["venueState"] = state.ToString(),
            }));
    }

    private static string Describe(VenueState state) => state switch
    {
        VenueState.Holiday => "closed for a holiday",
        VenueState.PreOpen => "in its pre-open auction and not accepting this order type",
        VenueState.Break => "on a trading break",
        VenueState.PostClose => "in its post-close session and not accepting this order type",
        _ => "closed",
    };
}
