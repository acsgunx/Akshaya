using System.Net;
using System.Text;
using Akshaya.Connector.MStock;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Akshaya.Connector.MStock.Tests;

/// <summary>
/// Replays a scripted mStock through the REAL auth facet — the HTTP layer is stubbed, nothing
/// else is. Everything above it (the envelope reader, the DTOs, the error mapper, the session
/// builder) is the production code path.
///
/// PREVENTS: the failure mode that has now bitten twice — every DTO parsing correctly in
/// isolation while the flow they belong to still cannot sign a user in. The shape tests prove
/// the payloads deserialise; only this proves that a correct username and password actually
/// produce a usable session.
/// </summary>
public sealed class MStockLoginFlowTests
{
    /// <summary>Answers each request from a queue of canned bodies, and records what was sent.</summary>
    private sealed class ScriptedHandler(params (HttpStatusCode Status, string Body)[] responses)
        : HttpMessageHandler
    {
        private int _next;

        public List<string> RequestPaths { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var (status, body) = responses[Math.Min(_next++, responses.Length - 1)];

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 3, 9, 30, 0, TimeSpan.FromHours(5.5));

    /// <summary>The body a live account returns from /connect/login. Verbatim.</summary>
    private const string LoginSuccess =
        """
        {"status":"success","data":{"ugid":"5544454f-5148-46f5-aca0-dee98ad5995c","is_kyc":true,"is_activate":false,"is_password_reset":true,"is_error":false,"cid":"1111","nm":"","flag":0}}
        """;

    /// <summary>The documented /session/token body, trimmed to what the connector reads.</summary>
    private const string SessionSuccess =
        """
        {"status":"success","data":{"user_type":"individual","email":"trader@example.com","user_name":"A Trader","broker":"MIRAE","exchanges":["NSE","NFO"],"products":["CNC","MIS"],"order_types":["MARKET","LIMIT"],"user_id":"538","api_key":"AK-1","access_token":"ACCESS-TOKEN","public_token":"PUBLIC-TOKEN","enctoken":"ENC-TOKEN","refresh_token":"REFRESH-TOKEN","login_time":"2026-09-03 09:30:00","meta":{"demat_consent":"physical"}}}
        """;

    private static (MStockAuth Auth, ScriptedHandler Handler) Build(
        params (HttpStatusCode Status, string Body)[] responses)
    {
        var options = new MStockOptions();
        var errors = new MStockErrorMapper();
        var handler = new ScriptedHandler(responses);

        // ONE HttpClient for the whole flow, so the second leg sees whatever headers the first
        // leg left behind — exactly as the real facet reuses its api instance.
        var http = new HttpClient(handler);

        return (
            new MStockAuth(
                options,
                errors,
                new FixedClock(Now),
                _ => MStockApi.Create(options, errors, session: null, httpClient: http)),
            handler);
    }

    private static AuthContext Credentials(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_key"] = "AK-1",
            ["username"] = "MA123456",
            ["password"] = "a-real-password",
        };

        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return new AuthContext { Credentials = new AuthCredentials(values) };
    }

    [Fact]
    public async Task A_correct_username_and_password_produce_an_OTP_challenge()
    {
        // THE test this file exists for. Not "does the DTO parse" — does a good login actually
        // move the flow forward.
        var (auth, handler) = Build((HttpStatusCode.OK, LoginSuccess));

        var step = await auth.BeginAsync(Credentials());

        step.IsSuccess.Should().BeTrue(
            step.IsFailure ? $"the login should have been accepted, but: {step.Error}" : string.Empty);

        step.Value.Should().BeOfType<AuthStep.ChallengeRequired>();
        ((AuthStep.ChallengeRequired)step.Value).Kind.Should().Be(ChallengeKind.SmsOtp);

        handler.RequestPaths.Should().ContainSingle().Which.Should().Be("/openapi/typea/connect/login");
    }

    [Fact]
    public async Task The_login_leg_sends_only_the_username_and_password()
    {
        // The documented request body is username + password. Sending the api_key here too
        // would be harmless but wrong; sending it as an Authorization header is what the
        // documentation explicitly does NOT do on this route.
        var (auth, handler) = Build((HttpStatusCode.OK, LoginSuccess));

        await auth.BeginAsync(Credentials());

        handler.RequestBodies[0].Should().Contain("username=MA123456");
        handler.RequestBodies[0].Should().Contain("password=a-real-password");
    }

    [Fact]
    public async Task The_OTP_completes_the_login_and_yields_a_usable_session()
    {
        var (auth, handler) = Build(
            (HttpStatusCode.OK, LoginSuccess),
            (HttpStatusCode.OK, SessionSuccess));

        await auth.BeginAsync(Credentials());
        var step = await auth.ContinueAsync(Credentials(), "123456");

        step.IsSuccess.Should().BeTrue(
            step.IsFailure ? $"the OTP should have been accepted, but: {step.Error}" : string.Empty);

        var completed = step.Value.Should().BeOfType<AuthStep.Completed>().Subject;
        completed.Session.AccessToken.Should().Be("ACCESS-TOKEN");
        completed.Session.RefreshToken.Should().Be("REFRESH-TOKEN");
        completed.Session.ConnectorId.Should().Be(MStockAuth.ConnectorId);
    }

    [Fact]
    public async Task The_session_leg_sends_the_source_string_not_a_hash()
    {
        // mStock documents `checksum` as the request SOURCE ("L"), not a SHA-256 of anything.
        // A 64-character hex digest here is rejected.
        var (auth, handler) = Build(
            (HttpStatusCode.OK, LoginSuccess),
            (HttpStatusCode.OK, SessionSuccess));

        await auth.BeginAsync(Credentials());
        await auth.ContinueAsync(Credentials(), "123456");

        var sessionBody = handler.RequestBodies[1];
        sessionBody.Should().Contain("checksum=L");
        sessionBody.Should().Contain("request_token=123456");
        sessionBody.Should().Contain("api_key=AK-1");
    }

    [Fact]
    public async Task A_stored_authenticator_secret_skips_the_OTP_entirely()
    {
        // mStock's own note: "If TOTP is enabled, OTP will not be triggered for login trading
        // API requests." A user with TOTP on who is shown an SMS prompt waits forever for a
        // message that is never sent.
        var (auth, handler) = Build(
            (HttpStatusCode.OK, LoginSuccess),
            (HttpStatusCode.OK, SessionSuccess));

        // A valid base32 secret; the TOTP itself is generated by the connector.
        var step = await auth.BeginAsync(Credentials((MStockAuth.TotpSecretField, "JBSWY3DPEHPK3PXP")));

        step.IsSuccess.Should().BeTrue(
            step.IsFailure ? $"the TOTP login should have completed, but: {step.Error}" : string.Empty);
        step.Value.Should().BeOfType<AuthStep.Completed>();

        handler.RequestPaths.Should().HaveCount(2);
        handler.RequestPaths[1].Should().Be("/openapi/typea/session/verifytotp");
    }

    [Fact]
    public async Task A_rejected_password_is_reported_as_invalid_credentials()
    {
        var (auth, _) = Build((HttpStatusCode.OK,
            """{"status":"error","message":"Invalid username or password (MACM1)","error_type":"MiraeException","data":null}"""));

        var step = await auth.BeginAsync(Credentials());

        step.IsFailure.Should().BeTrue();
        step.Error.Code.Should().Be(ConnectorErrorCodes.InvalidCredentials);
    }

    [Fact]
    public async Task A_wrong_OTP_is_reported_as_a_failed_challenge_not_a_dead_session()
    {
        // The user must be able to retype the code. Reporting this as SessionExpired would
        // bounce them back to the start of the login for a single mistyped digit.
        var (auth, _) = Build(
            (HttpStatusCode.OK, LoginSuccess),
            (HttpStatusCode.OK,
                """{"status":"error","message":"The entered OTP is incorrect. Please proceed to login page. (-MACM60)","error_type":"MiraeException","data":null}"""));

        await auth.BeginAsync(Credentials());
        var step = await auth.ContinueAsync(Credentials(), "000000");

        step.IsFailure.Should().BeTrue();
        step.Error.Code.Should().Be(ConnectorErrorCodes.ChallengeFailed);

        // And the broker's own words survive for support, even though the trader is shown ours.
        step.Error.VendorMessage.Should().Contain("The entered OTP is incorrect");
    }

    [Fact]
    public async Task An_unclassifiable_failure_shows_the_brokers_own_words()
    {
        // Previously this produced "mStock reported an error." and discarded a perfectly good
        // message. Our wording beats a vendor's only when we understood the failure well enough
        // to write one.
        var (auth, _) = Build((HttpStatusCode.OK,
            """{"status":"error","message":"Ledger balance is being recomputed, try in a minute.","error_type":"MiraeException","data":null}"""));

        var step = await auth.BeginAsync(Credentials());

        step.IsFailure.Should().BeTrue();
        step.Error.Message.Should().Contain("Ledger balance is being recomputed");
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
