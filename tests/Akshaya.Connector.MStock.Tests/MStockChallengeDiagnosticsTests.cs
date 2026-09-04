using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using FluentAssertions;
using Xunit;

namespace Akshaya.Connector.MStock.Tests;

/// <summary>
/// A rejected one-time code must tell the user WHY, in mStock's own words.
///
/// PREVENTS: the failure these tests were written against. Every challenge rejection rendered
/// as the flat sentence "mStock did not accept the one-time code." — our wording, replacing the
/// broker's. Those two failures look identical to a user and need opposite responses:
///
///   "The entered OTP is incorrect."  -> retype it, or ask for a new one
///   "Please enter correct TOTP"      -> STOP. No SMS was ever sent; this account uses an
///                                       authenticator app, and no amount of retyping an SMS
///                                       code will ever work.
///
/// Someone who cannot tell those apart retries the same doomed code until they give up. The
/// error mapper's own fallback arm already argues this position, with the OTP case as its
/// worked example — the typed arms simply did not follow it.
/// </summary>
public sealed class MStockChallengeDiagnosticsTests
{
    private static readonly MStockErrorMapper Mapper = new();

    /// <summary>mStock's documented wrong-OTP envelope, verbatim.</summary>
    private const string WrongOtpBody =
        """{"status":"error","message":"The entered OTP is incorrect. Please proceed to login page. (-MACM60)","error_type":"MiraeException","data":null}""";

    /// <summary>mStock's documented wrong-TOTP envelope, verbatim.</summary>
    private const string WrongTotpBody =
        """{"status":"error","message":"Please enter correct TOTP","error_type":"MiraeException","data":null}""";

    [Fact]
    public void A_rejected_otp_is_classified_as_a_challenge_failure()
    {
        var error = Mapper.MapHttp(200, WrongOtpBody);

        // HTTP 200, because mStock reports business failures inside a success status.
        error.Code.Should().Be(ConnectorErrorCodes.ChallengeFailed);
    }

    [Fact]
    public void A_rejected_otp_keeps_mstocks_own_explanation()
    {
        var error = Mapper.MapHttp(200, WrongOtpBody);

        error.Message.Should().Contain("The entered OTP is incorrect");
    }

    [Fact]
    public void A_rejected_totp_says_totp_so_the_user_can_tell_the_two_apart()
    {
        // THE DIAGNOSTIC THAT MATTERS. This is what an authenticator-enabled account gets back
        // when its code is posted to the SMS endpoint, and the word "TOTP" is the only clue
        // the user has that they are on the wrong route entirely.
        var error = Mapper.MapHttp(200, WrongTotpBody);

        error.Code.Should().Be(ConnectorErrorCodes.ChallengeFailed);
        error.Message.Should().Contain("TOTP");
    }

    // -----------------------------------------------------------------------------------
    // The path the user actually sees.
    //
    // MapHttp is only reached when the connector maps a raw response itself; it already kept
    // the broker's text. The route that reaches a live login is
    // HttpConnectorClient -> DescribeCanonicalCode, and THAT is where the wording was being
    // thrown away. Asserting against MapHttp alone would have looked green while the real
    // path stayed broken — which is exactly what the first draft of this file did.
    // -----------------------------------------------------------------------------------

    private static VendorErrorContext ContextFor(string vendorMessage) => new()
    {
        VendorCode = "MiraeException",
        VendorMessage = vendorMessage,
        HttpStatus = 200,
    };

    [Fact]
    public void The_described_challenge_failure_carries_the_brokers_reason()
    {
        var described = Mapper.DescribeCanonicalCode(
            ConnectorErrorCodes.ChallengeFailed,
            ContextFor("The entered OTP is incorrect. Please proceed to login page. (-MACM60)"));

        described.Should().Contain("The entered OTP is incorrect");
    }

    [Fact]
    public void The_described_totp_failure_still_says_totp()
    {
        var described = Mapper.DescribeCanonicalCode(
            ConnectorErrorCodes.ChallengeFailed,
            ContextFor("Please enter correct TOTP"));

        described.Should().Contain("TOTP");
    }

    [Fact]
    public void Our_sentence_still_leads_so_the_message_reads_as_ours()
    {
        // The broker's text is appended, not substituted: "mStock said: ..." keeps it clearly
        // attributed rather than passing vendor prose off as the platform's own voice.
        var described = Mapper.DescribeCanonicalCode(
            ConnectorErrorCodes.ChallengeFailed,
            ContextFor("The entered OTP is incorrect. Please proceed to login page. (-MACM60)"));

        described.Should().StartWith("mStock did not accept the one-time code.");
        described.Should().Contain("mStock said:");
    }

    [Fact]
    public void The_raw_broker_text_is_still_carried_separately()
    {
        // The UI may want to render it on its own; folding it into the message must not be the
        // only copy.
        var error = Mapper.MapHttp(200, WrongOtpBody);

        error.VendorMessage.Should().Contain("The entered OTP is incorrect");
    }

    [Fact]
    public void A_challenge_failure_with_no_broker_text_does_not_grow_an_empty_quotation()
    {
        var context = new VendorErrorContext
        {
            VendorCode = "TwoFactorException",
            VendorMessage = null,
            HttpStatus = 200,
        };

        var described = Mapper.DescribeCanonicalCode(ConnectorErrorCodes.ChallengeFailed, context);

        described.Should().Be("mStock did not accept the one-time code.");
        described.Should().NotContain("mStock said:");
    }

    [Fact]
    public void The_brokers_text_is_not_repeated_when_it_already_says_what_we_say()
    {
        // Guards the duplicate-suppression path: two sentences describing one failure reads
        // like two failures.
        var context = new VendorErrorContext
        {
            VendorCode = "TwoFactorException",
            VendorMessage = "one-time code",
            HttpStatus = 200,
        };

        var described = Mapper.DescribeCanonicalCode(ConnectorErrorCodes.ChallengeFailed, context);

        described.Should().Be("mStock did not accept the one-time code.");
    }

    [Fact]
    public void Bad_credentials_keep_the_brokers_words_too()
    {
        var error = Mapper.MapHttp(
            200,
            """{"status":"error","message":"Invalid username or password (YYYY)","error_type":"MiraeException","data":null}""");

        error.Code.Should().Be(ConnectorErrorCodes.InvalidCredentials);
        error.Message.Should().Contain("Invalid username or password");

        Mapper.DescribeCanonicalCode(ConnectorErrorCodes.InvalidCredentials, ContextFor("Invalid username or password (YYYY)"))
            .Should().Contain("Invalid username or password");
    }
}
