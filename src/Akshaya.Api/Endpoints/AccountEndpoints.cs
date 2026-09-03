using System.Security.Claims;
using Akshaya.Api.Contracts;
using Akshaya.Api.Infrastructure;
using Akshaya.Modules.Identity.Application;
using Akshaya.Modules.Identity.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Akshaya.Api.Endpoints;

/// <summary>
/// Sign-up, sign-in, sign-out, and the saved-broker-login vault.
///
/// The session is an HTTP-ONLY COOKIE, never a token the browser can read. The Angular app
/// holds no credential of any kind: an XSS bug in a page that renders broker names and prices
/// would otherwise hand an attacker a bearer token, and behind that token sit saved broker
/// credentials. The cookie is SameSite=Lax and, outside development, Secure.
///
/// There is no endpoint that returns a saved secret. <c>BrokerCredentialVault.RevealAsync</c>
/// is reachable only from the link flow inside a single request — see its doc comment.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/account").WithTags("Account");

        group.MapPost("/register", async (
            RegisterRequestDto request,
            IValidator<RegisterRequestDto> validator,
            UserAccountService accounts,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return ProblemDetailsMapper.ValidationProblem(validation.Errors.Select(e => e.ErrorMessage));
            }

            var result = await accounts.RegisterAsync(request.Email, request.Password, request.DisplayName, ct);
            if (result.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(result.Error);
            }

            // Registering signs you in. A separate sign-in step after sign-up is a form the
            // user just filled in, asked for again, for no security benefit whatsoever.
            await SignInCookieAsync(http, result.Value);
            return Results.Ok(UserProfileDto.From(result.Value));
        })
        .AllowAnonymous();

        group.MapPost("/sign-in", async (
            SignInRequestDto request,
            IValidator<SignInRequestDto> validator,
            UserAccountService accounts,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return ProblemDetailsMapper.ValidationProblem(validation.Errors.Select(e => e.ErrorMessage));
            }

            var result = await accounts.SignInAsync(request.Email, request.Password, ct);
            if (result.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(result.Error);
            }

            await SignInCookieAsync(http, result.Value);
            return Results.Ok(UserProfileDto.From(result.Value));
        })
        .AllowAnonymous();

        group.MapPost("/sign-out", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        })
        .AllowAnonymous();

        // The app calls this on boot to decide between the sign-in screen and the shell.
        // 204 rather than 401 for a signed-out visitor: "nobody is signed in" is the expected
        // answer to this question, not an error worth a console entry on every cold load.
        group.MapGet("/me", async (
            ICurrentUserAccessor user,
            UserAccountService accounts,
            CancellationToken ct) =>
        {
            if (!user.IsAuthenticated)
            {
                return Results.NoContent();
            }

            var account = await accounts.FindAsync(user.UserId, ct);
            return account is null
                // The cookie outlived the account it names — treat it as signed out rather
                // than 500, so a user whose account was deleted can still reach sign-in.
                ? Results.NoContent()
                : Results.Ok(UserProfileDto.From(account));
        })
        .AllowAnonymous();

        MapCredentialEndpoints(group);

        return app;
    }

    /// <summary>
    /// The saved-login vault's read and delete surface. There is deliberately no create here:
    /// credentials are saved as a side effect of a SUCCESSFUL broker link (see
    /// BrokerLinkEndpoints), because saving a login that has never worked is how a user ends
    /// up with a one-click button that one-click fails.
    /// </summary>
    private static void MapCredentialEndpoints(RouteGroupBuilder group)
    {
        var credentials = group.MapGroup("/credentials").RequireAuthorization();

        credentials.MapGet("/", async (
            ICurrentUserAccessor user,
            BrokerCredentialVault vault,
            CancellationToken ct) =>
        {
            var saved = await vault.ListAsync(user.UserId, ct);
            return Results.Ok(saved.Select(SavedCredentialDto.From).ToList());
        });

        credentials.MapDelete("/{id}", async (
            string id,
            ICurrentUserAccessor user,
            BrokerCredentialVault vault,
            CancellationToken ct) =>
        {
            var result = await vault.DeleteAsync(user.UserId, id, ct);
            return result.IsSuccess ? Results.NoContent() : ProblemDetailsMapper.ToProblem(result.Error);
        });
    }

    /// <summary>
    /// Issues the session cookie.
    ///
    /// The tenant id is a claim rather than something re-read per request: every downstream
    /// store is tenant-scoped, and a tenant resolved from the request body or a header instead
    /// of from a signed cookie is a cross-tenant data leak waiting for its first curl.
    /// </summary>
    private static Task SignInCookieAsync(HttpContext http, UserAccount account)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.Id),
                new Claim(AkshayaClaims.TenantId, account.TenantId),
                new Claim(ClaimTypes.Email, account.Email),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        return http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                // Survives a browser restart. A trading session that ends when the laptop lid
                // closes means re-authenticating every broker link the next morning too.
                IsPersistent = true,
            });
    }
}

/// <summary>Claim types this application issues itself.</summary>
public static class AkshayaClaims
{
    public const string TenantId = "akshaya:tenant";
}
