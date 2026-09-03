using System.Text.Json;
using Akshaya.Connector.MStock;
using FluentAssertions;
using Xunit;

namespace Akshaya.Connector.MStock.Tests;

/// <summary>
/// PREVENTS: the regression that shipped — a successful mStock login rejected with "the
/// broker's response could not be understood" because three flags the connector never reads
/// were declared as strings and arrive as booleans.
///
/// The first test below is the EXACT body a live account returned. Everything after it exists
/// because the underlying fault was not "three fields had the wrong type", it was "any field
/// having an unexpected type fails the whole login" — so the tests pin the tolerance, not just
/// the three fields.
/// </summary>
public sealed class MStockLoginResponseTests
{
    /// <summary>Verbatim from a live account. Do not tidy it; its exact shape is the point.</summary>
    private const string LiveLoginResponse =
        """
        {"status":"success","data":{"ugid":"5544454f-5148-46f5-aca0-dee98ad5995c","is_kyc":true,"is_activate":false,"is_password_reset":true,"is_error":false,"cid":"1111","nm":"","flag":0}}
        """;

    private static MStockEnvelope<MStockLoginData>? Parse(string json) =>
        JsonSerializer.Deserialize<MStockEnvelope<MStockLoginData>>(json, MStockJson.Options);

    [Fact]
    public void The_login_response_a_live_account_returns_parses()
    {
        var envelope = Parse(LiveLoginResponse);

        envelope.Should().NotBeNull();
        envelope!.Status.Should().Be("success");
        envelope.IsSuccess.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
    }

    [Fact]
    public void The_one_field_the_login_leg_actually_needs_is_read()
    {
        // ugid is the only value the flow carries forward. If this breaks, the login breaks
        // for a reason worth failing over — unlike everything else in the payload.
        var envelope = Parse(LiveLoginResponse);

        envelope!.Data!.Ugid.Should().Be("5544454f-5148-46f5-aca0-dee98ad5995c");
    }

    [Fact]
    public void Boolean_flags_sent_as_bare_booleans_are_read_as_booleans()
    {
        // THE original bug. These were declared string? and mStock sends true/false.
        var data = Parse(LiveLoginResponse)!.Data!;

        data.IsKyc.Should().BeTrue();
        data.IsActivated.Should().BeFalse();
        data.IsPasswordReset.Should().BeTrue();
        data.IsError.Should().BeFalse();
    }

    [Fact]
    public void The_short_nickname_key_is_mapped()
    {
        // The connector was written against "nick_name"; the live payload sends "nm".
        var withNickname = LiveLoginResponse.Replace("""
            "nm":""
            """, """
            "nm":"Family account"
            """, StringComparison.Ordinal);

        Parse(withNickname)!.Data!.DisplayName.Should().Be("Family account");
    }

    [Fact]
    public void The_long_nickname_key_still_works()
    {
        var legacy = """{"status":"success","data":{"ugid":"u-1","nick_name":"Legacy build"}}""";

        Parse(legacy)!.Data!.DisplayName.Should().Be("Legacy build");
    }

    [Fact]
    public void An_empty_nickname_reads_as_no_name_rather_than_an_empty_label()
    {
        // The live payload has "nm":"" — a UI that renders that gets a blank label.
        Parse(LiveLoginResponse)!.Data!.DisplayName.Should().BeNull();
    }

    [Fact]
    public void A_missing_masked_mobile_is_tolerated()
    {
        // The live payload has no "mobile" at all, so the wizard cannot say where the OTP
        // went. That must be normal, not an error.
        Parse(LiveLoginResponse)!.Data!.MaskedMobile.Should().BeNull();
    }

    [Theory]
    // The same three flags, in every shape a vendor has plausibly sent them.
    [InlineData("true", true)]
    [InlineData("\"true\"", true)]
    [InlineData("\"TRUE\"", true)]
    [InlineData("\"Y\"", true)]
    [InlineData("\"yes\"", true)]
    [InlineData("1", true)]
    [InlineData("\"1\"", true)]
    [InlineData("false", false)]
    [InlineData("\"false\"", false)]
    [InlineData("\"N\"", false)]
    [InlineData("0", false)]
    [InlineData("\"0\"", false)]
    public void A_flag_is_understood_whatever_shape_it_arrives_in(string rawValue, bool expected)
    {
        var json = """{"status":"success","data":{"ugid":"u-1","is_kyc":""" + rawValue + "}}";

        Parse(json)!.Data!.IsKyc.Should().Be(expected);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"maybe\"")]
    public void An_uninterpretable_flag_reads_as_unknown_rather_than_failing_the_login(string rawValue)
    {
        // These flags are advisory. None of them is worth refusing to sign a trader in over.
        var json = """{"status":"success","data":{"ugid":"u-1","is_kyc":""" + rawValue + "}}";

        var parse = () => Parse(json);

        parse.Should().NotThrow();
        parse()!.Data!.IsKyc.Should().BeNull();
    }

    [Theory]
    [InlineData("\"1111\"", "1111")]
    [InlineData("1111", "1111")]
    [InlineData("true", "true")]
    public void A_client_code_is_read_whether_quoted_or_bare(string rawValue, string expected)
    {
        var json = """{"status":"success","data":{"ugid":"u-1","cid":""" + rawValue + "}}";

        Parse(json)!.Data!.ClientId.Should().Be(expected);
    }

    [Fact]
    public void A_number_keeps_the_vendors_own_formatting_when_read_as_a_string()
    {
        // Round-tripping through a numeric type would turn "1560.50" into "1560.5", and some of
        // these values get handed back to the broker verbatim.
        var json = """{"status":"success","data":{"ugid":"u-1","cid":1560.50}}""";

        Parse(json)!.Data!.ClientId.Should().Be("1560.50");
    }

    [Fact]
    public void An_unknown_extra_field_does_not_break_the_parse()
    {
        // The vendor adding a field must never be a breaking change for us.
        var json =
            """{"status":"success","data":{"ugid":"u-1","some_field_invented_next_year":{"nested":[1,2]}}}""";

        var parse = () => Parse(json);

        parse.Should().NotThrow();
        parse()!.Data!.Ugid.Should().Be("u-1");
    }

    [Fact]
    public void An_error_envelope_is_still_recognised_as_a_failure()
    {
        // Leniency must not have made a real rejection look like a success.
        var json =
            """{"status":"error","message":"Invalid username or password (MACM1)","error_type":"MiraeException"}""";

        var envelope = Parse(json);

        envelope!.IsSuccess.Should().BeFalse();
        envelope.Message.Should().Contain("Invalid username or password");
        envelope.ErrorType.Should().Be("MiraeException");
    }

    [Fact]
    public void An_object_where_a_string_belongs_still_throws()
    {
        // The leniency is scalar-only and deliberately so: an object here means the field's
        // MEANING changed, not merely its formatting, and that must not pass silently.
        var json = """{"status":"success","data":{"ugid":{"unexpected":"object"}}}""";

        var parse = () => Parse(json);

        parse.Should().Throw<JsonException>();
    }
}
