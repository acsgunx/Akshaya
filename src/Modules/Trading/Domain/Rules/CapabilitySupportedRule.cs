using Akshaya.Connectors.Abstractions;

namespace Akshaya.Modules.Trading.Domain.Rules;

/// <summary>
/// Checks the order against what the connector's MANIFEST says the broker can do: the venue,
/// the asset class, the order type, the time-in-force, the position effect and the variety.
///
/// PREVENTS: the silent capability mismatch. A trader places a stop-limit good-till-cancelled
/// order through a broker that supports neither; the broker's API accepts the payload, quietly
/// drops the stop leg and books a plain day limit order. The trader believes they are
/// protected, they are not, and they find out when the market gaps. Rejecting locally, with the
/// specific unsupported field named, is the only outcome that leaves the trader informed.
///
/// This is also the rule that makes plug-and-play real. It reads the manifest and NOTHING else;
/// adding a broker never adds a branch here.
/// </summary>
public sealed class CapabilitySupportedRule : IRiskRule
{
    public string Name => RiskRuleNames.CapabilitySupported;

    public int Order => 30;

    public Task<RiskDecision> EvaluateAsync(RiskEvaluationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var manifest = context.Manifest;
        var request = context.Request;
        var orders = manifest.Orders;

        if (!manifest.SupportsVenue(request.Instrument.Venue))
        {
            return Deny(
                $"This broker link cannot reach {request.Instrument.Venue}.",
                "venue",
                request.Instrument.Venue.ToString());
        }

        if (!manifest.SupportsAssetClass(request.Instrument.AssetClass))
        {
            return Deny(
                $"This broker link does not trade {request.Instrument.AssetClass}.",
                "assetClass",
                request.Instrument.AssetClass.ToString());
        }

        if (!orders.Supports(request.OrderType))
        {
            return Deny(
                $"This broker does not support {request.OrderType} orders. Supported: "
                + $"{string.Join(", ", orders.Types)}.",
                "orderType",
                request.OrderType.ToString());
        }

        if (!orders.Supports(request.TimeInForce))
        {
            return Deny(
                $"This broker does not support a {request.TimeInForce} time-in-force. Supported: "
                + $"{string.Join(", ", orders.TimeInForce)}.",
                "timeInForce",
                request.TimeInForce.ToString());
        }

        // PositionEffect is a flags enum but the manifest declares whole COMBINATIONS, because
        // the concept fragments by market and only certain combinations are real products.
        // Exact membership is therefore the right test, not a bitwise subset check.
        if (!orders.Supports(request.PositionEffect))
        {
            return Deny(
                $"This broker does not support the {request.PositionEffect} position effect. Supported: "
                + $"{string.Join(", ", orders.PositionEffects)}.",
                "positionEffect",
                request.PositionEffect.ToString());
        }

        if (!orders.Varieties.Contains(request.Variety))
        {
            return Deny(
                $"This broker does not support {request.Variety} orders. Supported: "
                + $"{string.Join(", ", orders.Varieties)}.",
                "variety",
                request.Variety.ToString());
        }

        return Task.FromResult(RiskDecision.Allow());
    }

    private Task<RiskDecision> Deny(string reason, string field, string value) =>
        Task.FromResult(RiskDecision.Deny(
            Name,
            reason,
            // NotSupported rather than RiskRejected: this is a capability gap, not a limit
            // breach, and the API maps it to 501 so the client can disable the control rather
            // than telling the user to try a smaller order.
            ConnectorErrorCodes.NotSupported,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["field"] = field,
                ["value"] = value,
            }));
}
