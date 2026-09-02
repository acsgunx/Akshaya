using Akshaya.Connectors.Abstractions;

namespace Akshaya.Connectors.Sdk;

/// <summary>
/// Everything known about a failed vendor call at the moment we decide what it means.
/// Passed as one record rather than five parameters so adding a signal later (a vendor
/// request id, a response header) is not a breaking change for every connector's mapper.
/// </summary>
/// <param name="HttpStatus">Null for non-HTTP transports (a gateway socket, a gRPC proxy).</param>
/// <param name="VendorCode">The broker's own code, verbatim, if the payload carried one.</param>
/// <param name="VendorMessage">The broker's own message, verbatim.</param>
/// <param name="Path">Request path or operation name, for mappers whose meaning is endpoint-dependent.</param>
/// <param name="RawBody">Truncated response body, for mappers that must sniff an unstructured payload.</param>
public readonly record struct VendorErrorContext(
    int? HttpStatus,
    string? VendorCode,
    string? VendorMessage,
    string? Path = null,
    string? RawBody = null);

/// <summary>
/// Translates a broker's private error vocabulary into the closed canonical set in
/// <see cref="ConnectorErrorCodes"/>.
///
/// This is where "TSA-1017", "e-1023", "Invalid session key" and HTTP 401 all become
/// <c>connector.session_expired</c>. It is a separate injected interface rather than a method
/// on the connector for two reasons: the mapping table is the part of a connector that changes
/// most often (vendors add codes without telling anyone), and the conformance suite tests the
/// mapper directly against recorded vendor fixtures without needing a live broker.
///
/// Returning null is meaningful: it means "I have no opinion", and the caller falls back to
/// the HTTP status mapping. A mapper must not guess — a mis-mapped
/// <see cref="ConnectorErrorCodes.RateLimited"/> becomes an automatic retry of something that
/// should never have been retried.
/// </summary>
public interface IVendorErrorMapper
{
    /// <summary>
    /// The canonical code for this failure, or null to defer to the transport-level mapping.
    /// Implementations must be pure and must not throw.
    /// </summary>
    string? MapToCanonicalCode(VendorErrorContext context);

    /// <summary>
    /// A user-facing sentence for the canonical code. The vendor's own text still rides along
    /// in <see cref="Akshaya.SharedKernel.Error.VendorMessage"/>; this is the part the trader
    /// reads first, so it must be in the platform's voice and never contain a vendor code.
    /// </summary>
    string DescribeCanonicalCode(string canonicalCode, VendorErrorContext context);
}
