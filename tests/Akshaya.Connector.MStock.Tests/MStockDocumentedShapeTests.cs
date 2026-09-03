using System.Text.Json;
using Akshaya.Connector.MStock;
using FluentAssertions;
using Xunit;

namespace Akshaya.Connector.MStock.Tests;

/// <summary>
/// PREVENTS: the Kite-lineage assumption. This connector was written against mStock's published
/// Type A documentation, but several DTOs had quietly been shaped like ZERODHA KITE's instead —
/// a reasonable-looking guess that is wrong in three places, each of which fails a whole
/// feature.
///
/// Every payload below is copied verbatim from
/// https://tradingapi.mstock.com/docs/v1/typeA/User/ (retrieved 2026-09-03), with only the
/// vendor's own XXXXX placeholders replaced by plausible values. If mStock changes a shape,
/// these are the tests that should fail.
/// </summary>
public sealed class MStockDocumentedShapeTests
{
    /// <summary>
    /// The documented login response.
    ///
    /// NOTE THE QUOTED FLAGS. The documentation shows <c>"is_kyc": "true"</c> — a string — while
    /// a live account returns a bare <c>true</c>. The docs and the API disagree, so the
    /// connector has to accept BOTH; that is the whole justification for the lenient converters
    /// rather than simply correcting the declared types. See MStockLoginResponseTests for the
    /// live shape.
    /// </summary>
    private const string DocumentedLoginResponse =
        """
        {"status":"success","data":{"ugid":"xxxxxx-xxxxx-4b04-8086-a8b37f62953d","is_kyc":"true","is_activate":"true","is_password_reset":"true","is_error":"false","cid":"MA1234","nm":"A Trader","flag":0}}
        """;

    [Fact]
    public void The_login_response_as_documented_parses()
    {
        var envelope = JsonSerializer.Deserialize<MStockEnvelope<MStockLoginData>>(
            DocumentedLoginResponse, MStockJson.Options);

        envelope!.IsSuccess.Should().BeTrue();
        envelope.Data!.Ugid.Should().Be("xxxxxx-xxxxx-4b04-8086-a8b37f62953d");
        envelope.Data.DisplayName.Should().Be("A Trader");

        // Quoted "true"/"false" read as real booleans, exactly as the bare form does.
        envelope.Data.IsKyc.Should().BeTrue();
        envelope.Data.IsActivated.Should().BeTrue();
        envelope.Data.IsError.Should().BeFalse();
    }

    [Fact]
    public void The_documented_login_response_carries_no_masked_mobile()
    {
        // Confirms against the docs what the live payload already showed: there is no field
        // naming the number the OTP went to. The wizard must not promise one.
        DocumentedLoginResponse.Should().NotContain("mobile");
    }

    /// <summary>The documented session-token / verifytotp response. Both routes return this shape.</summary>
    private const string DocumentedSessionResponse =
        """
        {"status":"success","data":{"user_type":"individual","email":"trader@example.com","user_name":"A Trader","user_shortname":"NA","broker":"MIRAE","exchanges":["NSE","NFO","CDS"],"products":["CNC","NRML","MIS"],"order_types":["MARKET","LIMIT"],"avatar_url":"","user_id":"538","api_key":"ay3KxxxxxxxxxxkB/MAKg@@","access_token":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9","public_token":"4876d509-06c1-45db-8248-33dbde85135b","enctoken":"eyJhbGciOiJIUzUxMiJ9_juyp9To5lDGtr7jqmh-","refresh_token":"iGcP5onq6MXJYHHvAE26qS1A2cLodotQqZE5azfsah8","silo":"","login_time":"2024-09-26 03:34:48","meta":{"demat_consent":"physical"}}}
        """;

    [Fact]
    public void The_session_response_as_documented_parses_with_every_token_we_need()
    {
        var envelope = JsonSerializer.Deserialize<MStockEnvelope<MStockSessionData>>(
            DocumentedSessionResponse, MStockJson.Options);

        envelope!.IsSuccess.Should().BeTrue();

        var data = envelope.Data!;
        data.AccessToken.Should().Be("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9");
        data.RefreshToken.Should().Be("iGcP5onq6MXJYHHvAE26qS1A2cLodotQqZE5azfsah8");
        data.PublicToken.Should().Be("4876d509-06c1-45db-8248-33dbde85135b");

        // Not interchangeable with the access token — the socket authenticates with this one.
        data.EncToken.Should().Be("eyJhbGciOiJIUzUxMiJ9_juyp9To5lDGtr7jqmh-");

        data.UserId.Should().Be("538");
        data.Exchanges.Should().BeEquivalentTo(["NSE", "NFO", "CDS"]);
        data.Products.Should().BeEquivalentTo(["CNC", "NRML", "MIS"]);
    }

    [Fact]
    public void The_nested_meta_object_in_the_session_response_does_not_break_the_parse()
    {
        // "meta":{"demat_consent":"physical"} is an OBJECT and is not mapped. An unmapped
        // object must be skipped, not throw — this is the shape most likely to trip a future
        // lenient-string converter if someone maps it carelessly.
        var parse = () => JsonSerializer.Deserialize<MStockEnvelope<MStockSessionData>>(
            DocumentedSessionResponse, MStockJson.Options);

        parse.Should().NotThrow();
    }

    /// <summary>The documented fund-summary response: an ARRAY of flat SCREAMING_SNAKE rows.</summary>
    private const string DocumentedFundSummaryResponse =
        """
        {"status":"success","data":[{"ADDITIONAL_MARGIN":"0.0","ADHOC_LIMIT":"99999999999","AMOUNT_UTILIZED":"27395824.71","AVAILABLE_BALANCE":"299972678840.29","BANK_HOLDING":"99999999999","CLEAR_BALANCE":"199999949998","COLLATERALS":"74668","LIMIT_SOD":"99999999999","LIMIT_TYPE":"CAPITAL","MF_COLLATERAL":"0.0","MTF_AVAILABLE_BALANCE":"299972678840.29","MTF_COLLATERAL":"0.0","MTF_UTILIZE":"0.0","MTM_COMBINED":"0","OFS_UTILIZED":"0.0","OPT_BUY_PRIMIUM_UTILIZE":"0.0","PAY_OUT_AMT":"50000.0","PEAK_MARGIN":"33222959.73","PHYSICAL_MARGIN":"0.0","REALISED_PROFITS":"0","RECEIVABLES":"0","SEG":"A","SUM_OF_ALL":"300000024665","UNCLEAR_BALANCE":"0"}]}
        """;

    [Fact]
    public void The_fund_summary_as_documented_parses_as_a_list_of_rows()
    {
        // THE bug this class is named for: the DTO previously expected Kite's
        // {"equity":{"available":{…}}} object. mStock sends an array, so this threw and the
        // fund summary could never have worked at all.
        var envelope = JsonSerializer.Deserialize<MStockEnvelope<IReadOnlyList<MStockFundRow>>>(
            DocumentedFundSummaryResponse, MStockJson.Options);

        envelope!.IsSuccess.Should().BeTrue();
        envelope.Data.Should().ContainSingle();
    }

    [Fact]
    public void Quoted_monetary_strings_bind_to_decimals()
    {
        // Every money value is a QUOTED string on this route. They bind only because
        // MStockJson.Options sets AllowReadingFromString.
        var row = JsonSerializer.Deserialize<MStockEnvelope<IReadOnlyList<MStockFundRow>>>(
            DocumentedFundSummaryResponse, MStockJson.Options)!.Data![0];

        row.AvailableBalance.Should().Be(299972678840.29m);
        row.AmountUtilized.Should().Be(27395824.71m);
        row.ClearBalance.Should().Be(199999949998m);
        row.Collaterals.Should().Be(74668m);
        row.RealisedProfits.Should().Be(0m);
        row.Segment.Should().Be("A");
        row.LimitType.Should().Be("CAPITAL");
    }

    [Fact]
    public void The_vendors_misspelled_premium_field_is_mapped_as_spelled()
    {
        // OPT_BUY_PRIMIUM_UTILIZE, sic. Correcting the spelling in the DTO would silently stop
        // it binding.
        var row = JsonSerializer.Deserialize<MStockEnvelope<IReadOnlyList<MStockFundRow>>>(
            DocumentedFundSummaryResponse, MStockJson.Options)!.Data![0];

        row.OptionBuyPremiumUtilized.Should().Be(0m);
    }

    [Fact]
    public void The_logout_response_parses_although_its_data_node_is_a_bare_string()
    {
        // Documented as {"status":"success","data":"Success"} — data is a STRING, not an
        // object. The placeholder class this used to deserialise into threw on it, so every
        // successful logout was reported as a malformed response.
        var json = """{"status":"success","data":"Success"}""";

        var parse = () => JsonSerializer.Deserialize<MStockEnvelope<JsonElement>>(json, MStockJson.Options);

        parse.Should().NotThrow();
        parse()!.IsSuccess.Should().BeTrue();
        parse()!.Data.GetString().Should().Be("Success");
    }

    [Theory]
    // Every documented failure envelope, so the error mapper keeps recognising each one.
    [InlineData("""{"status":"error","message":"Please provide valid api version.","data":null}""", null)]
    [InlineData("""{"status":"error","message":"Invalid username or password (YYYY)","error_type":"MiraeException","data":null}""", "MiraeException")]
    [InlineData("""{"status":"error","message":"The entered OTP is incorrect. Please proceed to login page. (-MACM60)","error_type":"MiraeException","data":null}""", "MiraeException")]
    [InlineData("""{"status":"error","message":"API is suspended/expired for use. Please check your API subscription and try again.","error_type":"APIKeyException","data":null}""", "APIKeyException")]
    [InlineData("""{"status":"error","message":"Please enter correct TOTP","error_type":"MiraeException","data":null}""", "MiraeException")]
    [InlineData("""{"status":"error","message":"Invalid request. Please try again.","error_type":"TokenException","data":null}""", "TokenException")]
    public void Every_documented_failure_envelope_is_recognised_as_a_failure(string json, string? errorType)
    {
        var envelope = JsonSerializer.Deserialize<MStockEnvelope<JsonElement>>(json, MStockJson.Options);

        envelope!.IsSuccess.Should().BeFalse();
        envelope.ErrorType.Should().Be(errorType);
        envelope.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_expired_api_key_maps_to_a_re_authentication_prompt_not_a_retry()
    {
        // APIKeyException is a 403 the user can only fix by renewing their subscription.
        // Telling them to "try again" would be useless advice repeated forever.
        var mapper = new MStockErrorMapper();

        var error = mapper.MapHttp(
            403,
            """{"status":"error","message":"API is suspended/expired for use. Please check your API subscription and try again.","error_type":"APIKeyException","data":null}""");

        error.Code.Should().Be(Akshaya.Connectors.Abstractions.ConnectorErrorCodes.ReauthRequired);
        error.VendorCode.Should().Be("APIKeyException");
    }
}
