using Akshaya.Api.Infrastructure;
using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using FluentValidation;

namespace Akshaya.Api.Endpoints;

/// <summary>
/// Per-tenant risk configuration and the kill switch. Every write here is audited by the
/// domain services it calls — <see cref="Akshaya.Modules.Trading.Application.KillSwitch"/> for the switch, and this file's own
/// audit call for policy edits — because both are exactly the kind of change an incident
/// review needs to be able to reconstruct.
/// </summary>
public static class RiskEndpoints
{
    public static IEndpointRouteBuilder MapRiskEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/risk").WithTags("Risk");

        group.MapGet("/policy", async (
            ICurrentUserAccessor user,
            IRiskPolicyStore policies,
            CancellationToken ct) =>
        {
            var policy = await policies.GetAsync(user.TenantId, ct);
            return Results.Ok(RiskPolicyDto.From(policy));
        });

        group.MapPut("/policy", async (
            RiskPolicyDto request,
            ICurrentUserAccessor user,
            IValidator<RiskPolicyDto> validator,
            IRiskPolicyStore policies,
            IAuditSink audit,
            IClock clock,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return ProblemDetailsMapper.ValidationProblem(validation.Errors.Select(e => e.ErrorMessage));
            }

            // TenantId and NormalisationCurrency are never taken from the request body: a client
            // that could set another tenant's policy would be an authorisation bypass, and a
            // policy's normalisation currency is a one-time, operator-set decision, not
            // something a risk-settings screen should be able to silently change out from under
            // every existing limit.
            var existing = await policies.GetAsync(user.TenantId, ct);
            var policy = request.ToPolicy(user.TenantId, existing.NormalisationCurrency);

            await policies.SaveAsync(policy, OrderActors.User, ct);

            await audit.RecordAsync(
                new AuditRecord
                {
                    At = clock.UtcNow,
                    TenantId = user.TenantId,
                    Actor = OrderActors.User,
                    Action = "risk.policy.update",
                    Subject = user.TenantId,
                    Detail = $"{policy.EnabledRules.Count} rule(s) enabled.",
                },
                ct);

            return Results.Ok(RiskPolicyDto.From(policy));
        });

        group.MapGet("/kill-switch", async (
            ICurrentUserAccessor user,
            IKillSwitch killSwitch,
            CancellationToken ct) =>
        {
            var state = await killSwitch.GetAsync(user.TenantId, ct);
            return Results.Ok(state);
        });

        group.MapPost("/kill-switch/engage", async (
            KillSwitchRequestDto request,
            ICurrentUserAccessor user,
            IValidator<KillSwitchRequestDto> validator,
            IKillSwitch killSwitch,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return ProblemDetailsMapper.ValidationProblem(validation.Errors.Select(e => e.ErrorMessage));
            }

            await killSwitch.EngageAsync(user.TenantId, OrderActors.User, request.Reason, ct);
            return Results.Ok(await killSwitch.GetAsync(user.TenantId, ct));
        });

        group.MapPost("/kill-switch/disengage", async (
            KillSwitchRequestDto request,
            ICurrentUserAccessor user,
            IValidator<KillSwitchRequestDto> validator,
            IKillSwitch killSwitch,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return ProblemDetailsMapper.ValidationProblem(validation.Errors.Select(e => e.ErrorMessage));
            }

            await killSwitch.DisengageAsync(user.TenantId, OrderActors.User, request.Reason, ct);
            return Results.Ok(await killSwitch.GetAsync(user.TenantId, ct));
        });

        return app;
    }
}

/// <summary>Why the caller is flipping the kill switch. Required in both directions — see <see cref="Akshaya.Modules.Trading.Application.KillSwitch"/>.</summary>
public sealed record KillSwitchRequestDto
{
    public required string Reason { get; init; }
}

public sealed class KillSwitchRequestDtoValidator : AbstractValidator<KillSwitchRequestDto>
{
    public KillSwitchRequestDtoValidator() =>
        RuleFor(r => r.Reason).NotEmpty().WithMessage("A reason is required for every kill-switch change.");
}

/// <summary>
/// A tenant's pre-trade limits, as the risk-settings screen edits them.
///
/// <see cref="TenantId"/> and the normalisation currency are deliberately absent: both are
/// server-controlled (see the handler above), never something a settings form can change.
/// </summary>
public sealed record RiskPolicyDto
{
    public IReadOnlySet<string> EnabledRules { get; init; } = RiskRuleNames.All;

    public Money? MaxOrderValue { get; init; }

    public decimal? MaxQuantity { get; init; }

    public int? MaxOpenPositions { get; init; }

    public Money? DailyLossLimit { get; init; }

    public IReadOnlySet<string> AllowedInstruments { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> DeniedInstruments { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public decimal? PriceBandPercent { get; init; }

    public bool AllowOrdersWhenVenueClosed { get; init; } = true;

    public bool RejectWhenPriceUnavailable { get; init; }

    public static RiskPolicyDto From(RiskPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return new RiskPolicyDto
        {
            EnabledRules = policy.EnabledRules,
            MaxOrderValue = policy.MaxOrderValue,
            MaxQuantity = policy.MaxQuantity,
            MaxOpenPositions = policy.MaxOpenPositions,
            DailyLossLimit = policy.DailyLossLimit,
            AllowedInstruments = policy.AllowedInstruments,
            DeniedInstruments = policy.DeniedInstruments,
            PriceBandPercent = policy.PriceBandPercent,
            AllowOrdersWhenVenueClosed = policy.AllowOrdersWhenVenueClosed,
            RejectWhenPriceUnavailable = policy.RejectWhenPriceUnavailable,
        };
    }

    public RiskPolicy ToPolicy(string tenantId, Currency normalisationCurrency) => new()
    {
        TenantId = tenantId,
        NormalisationCurrency = normalisationCurrency,
        EnabledRules = EnabledRules,
        MaxOrderValue = MaxOrderValue,
        MaxQuantity = MaxQuantity,
        MaxOpenPositions = MaxOpenPositions,
        DailyLossLimit = DailyLossLimit,
        AllowedInstruments = AllowedInstruments,
        DeniedInstruments = DeniedInstruments,
        PriceBandPercent = PriceBandPercent,
        AllowOrdersWhenVenueClosed = AllowOrdersWhenVenueClosed,
        RejectWhenPriceUnavailable = RejectWhenPriceUnavailable,
    };
}

public sealed class RiskPolicyDtoValidator : AbstractValidator<RiskPolicyDto>
{
    public RiskPolicyDtoValidator()
    {
        RuleFor(p => p.MaxQuantity).GreaterThan(0m).When(p => p.MaxQuantity is not null);
        RuleFor(p => p.MaxOpenPositions).GreaterThan(0).When(p => p.MaxOpenPositions is not null);
        RuleFor(p => p.PriceBandPercent).GreaterThan(0m).When(p => p.PriceBandPercent is not null);
        RuleFor(p => p.MaxOrderValue!.Value.Amount).GreaterThan(0m).When(p => p.MaxOrderValue is not null);
        RuleFor(p => p.DailyLossLimit!.Value.Amount).GreaterThan(0m).When(p => p.DailyLossLimit is not null);
    }
}
