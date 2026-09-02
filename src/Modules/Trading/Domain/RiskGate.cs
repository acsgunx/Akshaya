using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Trading.Domain;

/// <summary>
/// The answer from one rule, or from the gate as a whole.
///
/// A denial ALWAYS names the rule that produced it. "Order rejected by risk" with no rule name
/// is the single most useless message a trading platform can emit: the trader cannot fix it,
/// support cannot explain it, and the operator cannot tell whether the policy is even working.
/// </summary>
public sealed record RiskDecision
{
    public required bool IsAllowed { get; init; }

    /// <summary>One of <see cref="RiskRuleNames"/>. Null only when allowed.</summary>
    public string? RuleName { get; init; }

    /// <summary>Plain-language reason, written to be shown to the trader unedited.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Canonical code for the HTTP mapper. Almost always <see cref="ConnectorErrorCodes.RiskRejected"/>;
    /// a few rules produce a more precise one (a capability gap is NotSupported, a closed venue
    /// is MarketClosed) so the API returns the status the client can actually act on.
    /// </summary>
    public string ErrorCode { get; init; } = ConnectorErrorCodes.RiskRejected;

    /// <summary>Extra machine-readable context: the limit, the observed value, the rate used.</summary>
    public IReadOnlyDictionary<string, string> Context { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    public static readonly RiskDecision Allowed = new() { IsAllowed = true };

    public static RiskDecision Allow() => Allowed;

    public static RiskDecision Deny(
        string ruleName,
        string reason,
        string errorCode = ConnectorErrorCodes.RiskRejected,
        IReadOnlyDictionary<string, string>? context = null) => new()
    {
        IsAllowed = false,
        RuleName = ruleName,
        Reason = reason,
        ErrorCode = errorCode,
        Context = context ?? new Dictionary<string, string>(StringComparer.Ordinal),
    };

    /// <summary>Projects a denial onto the platform-wide failure channel.</summary>
    public Error ToError()
    {
        if (IsAllowed)
        {
            throw new InvalidOperationException("An allowed RiskDecision has no error to project.");
        }

        var context = new Dictionary<string, string>(Context, StringComparer.Ordinal)
        {
            ["riskRule"] = RuleName ?? "unknown",
        };

        return new Error(ErrorCode, Reason ?? "Blocked by a pre-trade risk rule.", Context: context);
    }
}

/// <summary>
/// Everything a rule may look at. Assembled ONCE by the handler and passed to every rule, so
/// that ten rules do not make ten quote calls, and so that every rule judges the same instant.
/// </summary>
public sealed record RiskEvaluationContext
{
    public required string TenantId { get; init; }

    public required string UserId { get; init; }

    public required string BrokerLinkId { get; init; }

    /// <summary>Opaque connector id, for messages and audit only. Never switched on.</summary>
    public required string ConnectorId { get; init; }

    /// <summary>What the broker declares it can do. The single source of capability truth.</summary>
    public required ConnectorManifest Manifest { get; init; }

    public required PlaceOrderRequest Request { get; init; }

    public required RiskPolicy Policy { get; init; }

    /// <summary>The instant every rule judges against. Never <c>DateTimeOffset.Now</c>.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Reference data for the instrument, when the master has it. Null is tolerated by every rule.</summary>
    public InstrumentDefinition? Instrument { get; init; }

    /// <summary>Last traded price, for the price-band guard. Null when the feed could not answer.</summary>
    public Money? LastTradedPrice { get; init; }

    public RiskSnapshot Snapshot { get; init; } = RiskSnapshot.Empty;

    /// <summary>True when this order reduces or closes exposure rather than opening it.</summary>
    public bool IsReducingExposure { get; init; }
}

/// <summary>
/// One pre-trade check.
///
/// Rules are separate classes and not branches in one method so that each can be unit-tested in
/// isolation, switched off per tenant by name, and reviewed on its own. A rule is a pure
/// function of its context plus at most one injected collaborator.
/// </summary>
public interface IRiskRule
{
    /// <summary>One of <see cref="RiskRuleNames"/>.</summary>
    string Name { get; }

    /// <summary>
    /// Lower runs earlier. Cheap, local, catastrophic-if-missed rules run before anything that
    /// touches the network: engaging the kill switch must stop the very next order, not the
    /// next order that survives an FX lookup.
    /// </summary>
    int Order { get; }

    Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default);
}

/// <summary>
/// Runs the enabled rules in order and stops at the first denial.
///
/// SEQUENTIAL, NOT PARALLEL, and that is deliberate. Rules are ordered cheapest-and-most-final
/// first; running them in parallel would issue an FX conversion and a quote fetch for an order
/// the kill switch was about to refuse, and would make the reported failing rule
/// non-deterministic when two rules fail at once. A trader who fixes the reported problem and
/// resubmits must not be told about a different problem each time.
///
/// FAILS CLOSED. A rule that throws is treated as a denial, not as a pass: an exception in the
/// risk gate is the one place where "we could not check" and "it is fine" must never be
/// confused.
/// </summary>
public sealed class RiskGate
{
    private readonly IReadOnlyList<IRiskRule> _rules;
    private readonly ILogger<RiskGate> _logger;

    public RiskGate(IEnumerable<IRiskRule> rules, ILogger<RiskGate> logger)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(logger);

        _rules = [.. rules.OrderBy(r => r.Order).ThenBy(r => r.Name, StringComparer.Ordinal)];
        _logger = logger;
    }

    /// <summary>The rules this gate will run, in the order it will run them. Exposed for the risk endpoint.</summary>
    public IReadOnlyList<string> RuleNames => [.. _rules.Select(r => r.Name)];

    public async Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var rule in _rules)
        {
            if (!context.Policy.IsEnabled(rule.Name))
            {
                // Switched off for this tenant. Logged at debug rather than silently skipped so
                // that "why did this get through" is answerable from the logs alone.
                _logger.LogDebug(
                    "Risk rule {Rule} is disabled for tenant {TenantId}; skipping.",
                    rule.Name,
                    context.TenantId);
                continue;
            }

            RiskDecision decision;
            try
            {
                decision = await rule.EvaluateAsync(context, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Risk rule {Rule} threw; failing closed.", rule.Name);
                return RiskDecision.Deny(
                    rule.Name,
                    $"The {rule.Name} check could not be completed, so the order was not sent.");
            }

            if (!decision.IsAllowed)
            {
                _logger.LogWarning(
                    "Risk rule {Rule} denied order {ClientOrderId} for tenant {TenantId}: {Reason}",
                    rule.Name,
                    context.Request.ClientOrderId,
                    context.TenantId,
                    decision.Reason);

                return decision;
            }
        }

        return RiskDecision.Allow();
    }
}
