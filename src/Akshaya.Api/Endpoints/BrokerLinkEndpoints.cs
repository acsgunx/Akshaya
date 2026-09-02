using System.Collections.Concurrent;
using Akshaya.Api.Contracts;
using Akshaya.Api.Infrastructure;
using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using FluentValidation;

namespace Akshaya.Api.Endpoints;

/// <summary>
/// The generic broker-login wizard's endpoints: begin a link, answer whatever step the
/// connector asks for, and manage the links that result. Nothing here is broker-specific — see
/// <see cref="AuthStepDto"/> for why that is possible at all.
/// </summary>
public static class BrokerLinkEndpoints
{
    public static IEndpointRouteBuilder MapBrokerLinkEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/links").WithTags("Broker links");

        group.MapGet("/", async (
            ICurrentUserAccessor user,
            IBrokerLinkStore links,
            CancellationToken ct) =>
        {
            var all = await links.ListAsync(user.TenantId, user.UserId, ct);
            return Results.Ok(all.Select(BrokerLinkDto.From).ToArray());
        });

        group.MapPost("/", async (
            BeginLinkRequestDto request,
            ICurrentUserAccessor user,
            IValidator<BeginLinkRequestDto> validator,
            IConnectorFactory connectors,
            PendingLinkAuthStore pending,
            IBrokerLinkStore links,
            IClock clock,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return ProblemDetailsMapper.ValidationProblem(validation.Errors.Select(e => e.ErrorMessage));
            }

            var connectorResult = connectors.CreateUnauthenticated(request.ConnectorId);
            if (connectorResult.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(connectorResult.Error);
            }

            await using var connector = connectorResult.Value;

            var context = new AuthContext
            {
                Credentials = new AuthCredentials(request.Credentials),
                RedirectUri = request.RedirectUri,
            };

            var stepResult = await connector.Auth.BeginAsync(context, ct);
            if (stepResult.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(stepResult.Error);
            }

            return await CompleteOrDeferAsync(
                stepResult.Value,
                request.ConnectorId,
                request.Nickname,
                request.Credentials,
                request.RedirectUri,
                user,
                pending,
                links,
                clock,
                ct);
        });

        group.MapPost("/{id}/continue", async (
            string id,
            ContinueLinkRequestDto request,
            ICurrentUserAccessor user,
            IValidator<ContinueLinkRequestDto> validator,
            IConnectorFactory connectors,
            PendingLinkAuthStore pending,
            IBrokerLinkStore links,
            IClock clock,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return ProblemDetailsMapper.ValidationProblem(validation.Errors.Select(e => e.ErrorMessage));
            }

            if (!pending.TryGet(id, out var flow) || !string.Equals(flow.TenantId, user.TenantId, StringComparison.Ordinal))
            {
                // Not-found rather than forbidden, for the same reason BrokerLinkResolver hides
                // cross-tenant ids: confirming the token exists to someone who should not know
                // is its own small leak.
                return ProblemDetailsMapper.ToProblem(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    "This login attempt has expired or does not exist. Start the link again."));
            }

            var connectorResult = connectors.CreateUnauthenticated(flow.ConnectorId);
            if (connectorResult.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(connectorResult.Error);
            }

            await using var connector = connectorResult.Value;

            var context = new AuthContext
            {
                Credentials = new AuthCredentials(flow.Credentials),
                RedirectUri = flow.RedirectUri,
                State = request.State,
            };

            var stepResult = await connector.Auth.ContinueAsync(context, request.Response, ct);
            if (stepResult.IsFailure)
            {
                return ProblemDetailsMapper.ToProblem(stepResult.Error);
            }

            return await CompleteOrDeferAsync(
                stepResult.Value,
                flow.ConnectorId,
                flow.Nickname,
                flow.Credentials,
                flow.RedirectUri,
                user,
                pending,
                links,
                clock,
                ct,
                existingPendingId: id);
        });

        group.MapDelete("/{id}", async (
            string id,
            ICurrentUserAccessor user,
            IBrokerLinkStore links,
            IConnectorFactory connectors,
            CancellationToken ct) =>
        {
            var link = await links.GetAsync(id, ct);
            if (link is null || !string.Equals(link.TenantId, user.TenantId, StringComparison.Ordinal))
            {
                return ProblemDetailsMapper.ToProblem(new Error(
                    ConnectorErrorCodes.InvalidRequest,
                    $"No broker link '{id}' exists for this account."));
            }

            // Best-effort: revoking at the broker matters, but a broker that is unreachable
            // right now must never stop the user removing the link on our side. Only the Auth
            // facet is usable on an unauthenticated connector, which is exactly what revoke needs.
            if (link.Session is { } session)
            {
                var connectorResult = connectors.CreateUnauthenticated(link.ConnectorId);
                if (connectorResult.IsSuccess)
                {
                    await using var connector = connectorResult.Value;
                    await connector.Auth.RevokeAsync(session, ct);
                }
            }

            await links.RemoveAsync(id, ct);
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>
    /// The one place an <see cref="AuthStep"/> becomes either a persisted, usable
    /// <see cref="BrokerLink"/> or a pending flow the client must continue.
    /// </summary>
    private static async Task<IResult> CompleteOrDeferAsync(
        AuthStep step,
        string connectorId,
        string? nickname,
        IReadOnlyDictionary<string, string> credentials,
        string? redirectUri,
        ICurrentUserAccessor user,
        PendingLinkAuthStore pending,
        IBrokerLinkStore links,
        IClock clock,
        CancellationToken ct,
        string? existingPendingId = null)
    {
        if (step is AuthStep.Completed completed)
        {
            var now = clock.UtcNow;
            var link = new BrokerLink
            {
                Id = Guid.CreateVersion7().ToString(),
                TenantId = user.TenantId,
                UserId = user.UserId,
                ConnectorId = connectorId,
                Nickname = nickname,
                Session = completed.Session,
                CreatedAt = now,
                LastAuthenticatedAt = now,
                IsActive = true,
            };

            await links.SaveAsync(link, ct);

            if (existingPendingId is not null)
            {
                pending.Remove(existingPendingId);
            }

            return Results.Ok(AuthStepDto.From(step, link.Id));
        }

        // Still mid-flow. Keep (or refresh) the pending entry under a stable token so the next
        // continue call can find the original credentials — required for connectors like a
        // password+OTP flow, where the OTP call must resupply the same password.
        var id = existingPendingId ?? Guid.CreateVersion7().ToString();
        pending.Save(id, new PendingLinkAuth(
            connectorId,
            nickname,
            credentials,
            redirectUri,
            user.TenantId,
            user.UserId,
            clock.UtcNow));

        return Results.Ok(AuthStepDto.From(step, id));
    }
}

/// <summary>
/// One credential set kept in memory while a multi-step login is in progress, keyed by an
/// opaque token handed to the client as <see cref="AuthStepDto.LinkId"/>.
///
/// DEV-ONLY DESIGN NOTE. This is in-process and unencrypted, which is acceptable only because
/// the whole platform is single-instance in this phase; a multi-instance deployment needs this
/// moved to a shared, encrypted store, or the second request of a two-leg login — a
/// <see cref="Akshaya.Connectors.Abstractions.AuthModel.PasswordOtp"/> flow's OTP step being the
/// case that forced this design — can land on an instance that never saw the first. Entries
/// expire after a short window: nobody should be answering an OTP prompt twenty minutes after it
/// was sent, and an unbounded dictionary of stale credentials is a liability with no upside.
/// </summary>
public sealed class PendingLinkAuthStore(IClock clock)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ConcurrentDictionary<string, PendingLinkAuth> _flows = new(StringComparer.Ordinal);

    public void Save(string id, PendingLinkAuth flow) => _flows[id] = flow;

    public bool TryGet(string id, out PendingLinkAuth flow)
    {
        if (_flows.TryGetValue(id, out var found) && _clock.UtcNow - found.StartedAt <= Ttl)
        {
            flow = found;
            return true;
        }

        _flows.TryRemove(id, out _);
        flow = default!;
        return false;
    }

    public void Remove(string id) => _flows.TryRemove(id, out _);
}

/// <summary>One in-flight, not-yet-completed broker login.</summary>
public sealed record PendingLinkAuth(
    string ConnectorId,
    string? Nickname,
    IReadOnlyDictionary<string, string> Credentials,
    string? RedirectUri,
    string TenantId,
    string UserId,
    DateTimeOffset StartedAt);
