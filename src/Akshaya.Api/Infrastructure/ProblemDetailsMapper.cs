using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Identity.Domain;
using Akshaya.SharedKernel;

namespace Akshaya.Api.Infrastructure;

/// <summary>
/// THE ONE PLACE canonical <see cref="ConnectorErrorCodes"/> become HTTP status codes.
///
/// Exactly one mapping table, for two reasons. First, an endpoint that invents its own status
/// for a failure is an endpoint whose clients cannot be written generically — the Angular app
/// decides between "prompt for re-auth", "show the risk message" and "retry in a moment" from
/// the status alone, and it can only do that if every endpoint agrees. Second, adding a
/// canonical error code is a contract change that needs an ADR; having one table makes the
/// omission of a new code obvious rather than scattering it across twenty endpoints.
///
/// The vendor's own code and message ride along in the ProblemDetails extensions, because
/// support's first question is always "what did the broker actually say".
/// </summary>
public static class ProblemDetailsMapper
{
    /// <summary>
    /// Canonical code to HTTP status. Every mapping below is a decision about what the CLIENT
    /// should do, not about what went wrong internally.
    /// </summary>
    public static int StatusFor(string errorCode) => errorCode switch
    {
        // The broker genuinely cannot do this. Not a retry, not a fix — a capability gap, so
        // the client should disable the control rather than tell the user to try again.
        ConnectorErrorCodes.NotSupported => StatusCodes.Status501NotImplemented,

        // The user must authenticate again. All three map to 401 so one client handler covers
        // them; the code in the payload says which flavour of re-auth prompt to show.
        ConnectorErrorCodes.InvalidCredentials => StatusCodes.Status401Unauthorized,
        ConnectorErrorCodes.SessionExpired => StatusCodes.Status401Unauthorized,
        ConnectorErrorCodes.ReauthRequired => StatusCodes.Status401Unauthorized,

        // The request was well-formed and was refused on its merits. 422, not 400: there is
        // nothing malformed to fix, and telling the user to check their JSON would be absurd.
        ConnectorErrorCodes.RiskRejected => StatusCodes.Status422UnprocessableEntity,
        ConnectorErrorCodes.InsufficientFunds => StatusCodes.Status422UnprocessableEntity,

        ConnectorErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,

        // Conflict with the current state of the world. The same request will succeed later.
        ConnectorErrorCodes.MarketClosed => StatusCodes.Status409Conflict,

        ConnectorErrorCodes.InstrumentNotFound => StatusCodes.Status404NotFound,
        ConnectorErrorCodes.OrderNotFound => StatusCodes.Status404NotFound,

        // Someone else's outage. 503 tells a client it is worth retrying with backoff.
        ConnectorErrorCodes.BrokerUnavailable => StatusCodes.Status503ServiceUnavailable,
        ConnectorErrorCodes.GatewayUnavailable => StatusCodes.Status503ServiceUnavailable,

        // 504, and the client must NOT resubmit — the order may exist. The payload carries the
        // order id so the UI can show "checking with your broker" instead of "failed".
        ConnectorErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,

        ConnectorErrorCodes.InvalidRequest => StatusCodes.Status400BadRequest,
        ConnectorErrorCodes.ChallengeFailed => StatusCodes.Status400BadRequest,
        ConnectorErrorCodes.OrderRejected => StatusCodes.Status422UnprocessableEntity,

        // ── Identity. The platform's own account, not a broker's. ──────────────────────────
        //
        // 409, not 400: the request was perfectly well formed and lost a race with an existing
        // account. It is also the one identity response that deliberately DOES confirm an
        // address is registered — sign-up cannot avoid it and stay usable, which is precisely
        // why sign-in below refuses to.
        IdentityErrorCodes.EmailAlreadyRegistered => StatusCodes.Status409Conflict,

        IdentityErrorCodes.InvalidCredentials => StatusCodes.Status401Unauthorized,
        IdentityErrorCodes.NotAuthenticated => StatusCodes.Status401Unauthorized,

        // 403, not 401: signing in again will not help, so the client must not offer to.
        IdentityErrorCodes.AccountDisabled => StatusCodes.Status403Forbidden,

        IdentityErrorCodes.InvalidRequest => StatusCodes.Status400BadRequest,
        IdentityErrorCodes.CredentialNotFound => StatusCodes.Status404NotFound,

        // 422: the record exists and is intact, but this deployment cannot read it. Nothing the
        // client sent was wrong, and retrying will not fix it — the user must re-enter and save.
        IdentityErrorCodes.CredentialUnreadable => StatusCodes.Status422UnprocessableEntity,

        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>Turns a canonical error into an RFC 7807 response.</summary>
    public static IResult ToProblem(Error error)
    {
        var status = StatusFor(error.Code);

        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = error.Code,
        };

        if (error.VendorCode is { Length: > 0 })
        {
            extensions["vendorCode"] = error.VendorCode;
        }

        if (error.VendorMessage is { Length: > 0 })
        {
            extensions["vendorMessage"] = error.VendorMessage;
        }

        if (error.Context is { Count: > 0 })
        {
            foreach (var (key, value) in error.Context)
            {
                // Never overwrite the reserved keys above with connector-supplied context.
                extensions.TryAdd(key, value);
            }
        }

        return Results.Problem(
            detail: error.Message,
            statusCode: status,
            title: TitleFor(status),
            type: $"https://docs.akshaya.dev/errors/{error.Code}",
            extensions: extensions);
    }

    /// <summary>Unwraps a <see cref="Result{T}"/> into either a 200 with the mapped body or a problem.</summary>
    public static IResult ToHttp<TValue, TResponse>(this Result<TValue> result, Func<TValue, TResponse> map)
    {
        ArgumentNullException.ThrowIfNull(map);

        return result.IsSuccess ? Results.Ok(map(result.Value)) : ToProblem(result.Error);
    }

    /// <summary>Unwraps a <see cref="Result{T}"/> whose value is already the response body.</summary>
    public static IResult ToHttp<TValue>(this Result<TValue> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : ToProblem(result.Error);

    /// <summary>Unwraps a void <see cref="Result"/> into 204 or a problem.</summary>
    public static IResult ToHttp(this Result result) =>
        result.IsSuccess ? Results.NoContent() : ToProblem(result.Error);

    /// <summary>
    /// A 400 for problems the caller can fix by reading the docs. Kept distinct from the
    /// canonical mapping above: validation failures never reach a broker and never get a
    /// vendor code.
    /// </summary>
    public static IResult ValidationProblem(IEnumerable<string> messages) =>
        Results.Problem(
            detail: string.Join(" ", messages),
            statusCode: StatusCodes.Status400BadRequest,
            title: "The request was not valid.",
            type: "https://docs.akshaya.dev/errors/validation");

    private static string TitleFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "The request was not valid.",
        StatusCodes.Status401Unauthorized => "Your broker session needs attention.",
        StatusCodes.Status404NotFound => "Not found.",
        StatusCodes.Status409Conflict => "Not possible right now.",
        StatusCodes.Status422UnprocessableEntity => "The order was refused.",
        StatusCodes.Status429TooManyRequests => "Too many requests.",
        StatusCodes.Status501NotImplemented => "Your broker does not support this.",
        StatusCodes.Status503ServiceUnavailable => "Your broker is unavailable.",
        StatusCodes.Status504GatewayTimeout => "Your broker did not answer in time.",
        _ => "Something went wrong.",
    };
}
