using Akshaya.Connectors.Abstractions;

namespace Akshaya.Modules.Portfolio.Ports;

/// <summary>
/// One broker account to fetch a portfolio from.
///
/// This module deliberately does NOT depend on the trading core's broker-link store. It needs
/// four facts and a session, so that is what the port asks for; the host wires it to whatever
/// owns link lifecycle. Keeping the dependency this thin is what lets the portfolio view be
/// tested against fakes without a trading module in the graph.
/// </summary>
/// <param name="Id">Link id, used as the per-broker breakdown key.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="UserId">Owning user.</param>
/// <param name="ConnectorId">Opaque connector id. Never compared to a literal anywhere in this module.</param>
/// <param name="DisplayName">What the user calls this account. For the breakdown UI only.</param>
/// <param name="Session">The decrypted session. Never logged.</param>
public sealed record PortfolioLink(
    string Id,
    string TenantId,
    string UserId,
    string ConnectorId,
    string DisplayName,
    BrokerSession Session);

/// <summary>Supplies the links a blended portfolio should be assembled from.</summary>
public interface IPortfolioLinkProvider
{
    Task<IReadOnlyList<PortfolioLink>> GetLinksAsync(
        string tenantId,
        string userId,
        CancellationToken ct = default);
}
