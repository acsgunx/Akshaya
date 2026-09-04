using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Domain;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Modules.Trading.Application;

/// <summary>
/// Moves an open position from one margin product to another — most often intraday to
/// delivery, when a trader decides to keep a position that would otherwise be squared off at
/// the session's end.
///
/// THIS IS NOT AN ORDER, and the difference drives every decision below.
///
///  * Nothing trades. No fill is generated, the position does not change size, and no
///    <see cref="Order"/> aggregate is created. Writing one would put a phantom fill in the
///    blotter and double the position in every P&amp;L that reads from trades.
///  * It is still ORDER-AFFECTING, so it is audited, it respects the kill switch, and it is
///    never retried automatically. Converting twice converts twice.
///  * It goes through the risk gate ONLY for the kill switch. The position already exists; the
///    quantity, price-band and order-value rules have nothing to say about a settlement basis,
///    and running them here would refuse a conversion because of a limit that was about
///    opening exposure, not carrying it.
///
/// The direction matters more than it looks. Intraday-to-delivery INCREASES the capital
/// required (delivery is not leveraged), so it can fail on margin — which is exactly when a
/// trader most wants to do it, at 15:15 with a losing position. The broker's error is
/// surfaced verbatim rather than paraphrased.
/// </summary>
public sealed class ConvertPositionHandler(
    BrokerLinkResolver linkResolver,
    IKillSwitch killSwitch,
    IAuditSink audit,
    IClock clock,
    ILogger<ConvertPositionHandler> logger)
{
    public async Task<Result> HandleAsync(ConvertPositionCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.From == command.To)
        {
            return Result.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"The position is already held as {command.From}; there is nothing to convert."));
        }

        if (command.Quantity.Value <= 0m)
        {
            return Result.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "The quantity to convert must be greater than zero."));
        }

        // The kill switch stops NEW exposure. A conversion does not open a position, but
        // intraday-to-delivery does commit capital overnight and takes the position out of the
        // automatic square-off — which is emphatically not what an operator who just hit the
        // kill switch wants happening on their account.
        if (await killSwitch.IsEngagedAsync(command.TenantId, ct))
        {
            var state = await killSwitch.GetAsync(command.TenantId, ct);

            return Result.Failure(new Error(
                ConnectorErrorCodes.RiskRejected,
                state.Reason is { Length: > 0 } reason
                    ? $"Trading is halted for this account: {reason}"
                    : "Trading is halted for this account by the kill switch."));
        }

        var linkResult = await linkResolver.GetLinkAsync(command.TenantId, command.BrokerLinkId, ct);
        if (linkResult.IsFailure)
        {
            return Result.Failure(linkResult.Error);
        }

        var connectorResult = await linkResolver.ConnectAsync(linkResult.Value, ct);
        if (connectorResult.IsFailure)
        {
            return Result.Failure(connectorResult.Error);
        }

        await using var connector = connectorResult.Value;

        // Asked of the manifest, not of the connector id. A broker that cannot convert says so
        // here and the UI hides the action, rather than the user finding out from a failed call.
        if (!connector.Manifest.Orders.PositionConversion)
        {
            return Result.Failure(new Error(
                ConnectorErrorCodes.NotSupported,
                $"{connector.Manifest.DisplayName} does not support converting a position between "
                + "products. The equivalent is to close the position and re-open it under the "
                + "product you want — which is a real trade, with real costs, so it is left as a "
                + "deliberate decision rather than done silently on your behalf."));
        }

        if (!connector.Manifest.Orders.Supports(command.To))
        {
            return Result.Failure(new Error(
                ConnectorErrorCodes.NotSupported,
                $"{connector.Manifest.DisplayName} does not offer the {command.To} product."));
        }

        var request = new ConvertPositionRequest
        {
            Instrument = command.Instrument,
            Side = command.Side,
            Quantity = command.Quantity,
            From = command.From,
            To = command.To,
        };

        var result = await connector.Portfolio.ConvertPositionAsync(request, ct);

        await audit.RecordAsync(
            new AuditRecord
            {
                At = clock.UtcNow,
                TenantId = command.TenantId,
                Actor = command.Actor,
                Action = "position.convert",
                Subject = command.Instrument.ToString(),
                Succeeded = result.IsSuccess,
                Detail = result.IsSuccess
                    ? $"Converted {command.Quantity} {command.Instrument} from {command.From} to {command.To}."
                    : $"Conversion refused: {result.Error.Message}",
                Context = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["brokerLinkId"] = command.BrokerLinkId,
                    ["connectorId"] = connector.Manifest.Id,
                    ["side"] = command.Side.ToString(),
                    ["quantity"] = command.Quantity.ToString(),
                    ["from"] = command.From.ToString(),
                    ["to"] = command.To.ToString(),
                },
            },
            ct);

        if (result.IsFailure)
        {
            logger.LogWarning(
                "Position conversion refused for {Instrument} on link {LinkId}: {Reason}",
                command.Instrument,
                command.BrokerLinkId,
                result.Error.Message);
        }

        return result;
    }
}
