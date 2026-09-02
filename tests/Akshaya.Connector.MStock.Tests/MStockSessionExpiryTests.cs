using FluentAssertions;
using Xunit;

namespace Akshaya.Connector.MStock.Tests;

/// <summary>
/// The expiry rule from ADR 0005, pinned.
///
/// mStock publishes a twelve-hour token lifetime AND invalidates every token at midnight India
/// time. Trusting the published lifetime alone means a token minted in the afternoon is believed
/// good until the small hours, the re-auth prompt is scheduled for after it already died, and the
/// trader finds out from a rejected order at the next open.
///
/// These tests exist so nobody "simplifies" ComputeExpiry back to issued-plus-lifetime.
/// </summary>
public sealed class MStockSessionExpiryTests
{
    private static readonly TimeZoneInfo Ist = MStockTime.ResolveZone("Asia/Kolkata");

    private static readonly TimeSpan NominalLifetime = TimeSpan.FromHours(12);

    /// <summary>Builds an instant from an India-local wall-clock time.</summary>
    private static DateTimeOffset IstTime(int year, int month, int day, int hour, int minute = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Ist.GetUtcOffset(local));
    }

    [Fact]
    public void An_afternoon_token_expires_at_midnight_not_twelve_hours_later()
    {
        // The case that motivated the rule. Issued 15:00 IST; naive arithmetic says 03:00 the
        // next day, which is three hours after the broker has already invalidated it.
        var issued = IstTime(2026, 9, 2, 15, 0);

        var expiry = MStockAuth.ComputeExpiry(issued, NominalLifetime, Ist);

        expiry.Should().Be(IstTime(2026, 9, 3, 0, 0));
        expiry.Should().BeBefore(issued + NominalLifetime);
    }

    [Fact]
    public void An_early_morning_token_expires_on_its_nominal_lifetime()
    {
        // Issued 02:00 IST: twelve hours lands at 14:00 the same day, comfortably before
        // midnight, so the published lifetime is the binding constraint here.
        var issued = IstTime(2026, 9, 2, 2, 0);

        var expiry = MStockAuth.ComputeExpiry(issued, NominalLifetime, Ist);

        expiry.Should().Be(IstTime(2026, 9, 2, 14, 0));
    }

    [Fact]
    public void A_token_minted_exactly_at_noon_takes_the_earlier_of_the_two()
    {
        // 12:00 + 12h = midnight exactly. The two constraints coincide; the result must be that
        // instant and not the following midnight.
        var issued = IstTime(2026, 9, 2, 12, 0);

        MStockAuth.ComputeExpiry(issued, NominalLifetime, Ist)
            .Should().Be(IstTime(2026, 9, 3, 0, 0));
    }

    [Fact]
    public void A_token_minted_just_before_midnight_expires_almost_immediately()
    {
        // 23:55 IST leaves five minutes of real session. The platform must know that, because
        // this is precisely when a trader would otherwise place an order into a dead session.
        var issued = IstTime(2026, 9, 2, 23, 55);

        var expiry = MStockAuth.ComputeExpiry(issued, NominalLifetime, Ist);

        expiry.Should().Be(IstTime(2026, 9, 3, 0, 0));
        (expiry - issued).Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Expiry_is_computed_in_the_venue_zone_not_the_server_zone()
    {
        // A server in UTC or Singapore must reach the same answer as one in India. If this ever
        // fails, someone has used local time somewhere in the chain and the platform's behaviour
        // now depends on where it is deployed.
        var issued = IstTime(2026, 9, 2, 15, 0);

        var fromIst = MStockAuth.ComputeExpiry(issued, NominalLifetime, Ist);
        var fromUtcInstant = MStockAuth.ComputeExpiry(
            issued.ToUniversalTime(), NominalLifetime, Ist);

        fromUtcInstant.ToUniversalTime().Should().Be(fromIst.ToUniversalTime());
    }

    [Fact]
    public void Expiring_early_is_always_preferred_to_expiring_late()
    {
        // A property rather than an example: whatever the issue time, the computed expiry never
        // exceeds either constraint. Expiring early costs one login; expiring late costs orders.
        for (var hour = 0; hour < 24; hour++)
        {
            var issued = IstTime(2026, 9, 2, hour);
            var expiry = MStockAuth.ComputeExpiry(issued, NominalLifetime, Ist);

            expiry.Should().BeOnOrBefore(issued + NominalLifetime);
            expiry.Should().BeOnOrBefore(MStockTime.NextVenueMidnight(issued, Ist));
            expiry.Should().BeAfter(issued, "a session that is already expired is not a session");
        }
    }
}
