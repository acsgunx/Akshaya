using Akshaya.SharedKernel;

namespace Akshaya.Modules.Portfolio.Ports;

/// <summary>Cross-venue identifiers for one instrument. Either may be absent.</summary>
/// <param name="Isin">ISO 6166 security identifier.</param>
/// <param name="Figi">OpenFIGI identifier.</param>
public readonly record struct InstrumentIdentity(string? Isin, string? Figi)
{
    public static readonly InstrumentIdentity Unknown = new(null, null);

    public bool HasAny => !string.IsNullOrWhiteSpace(Isin) || !string.IsNullOrWhiteSpace(Figi);
}

/// <summary>
/// Resolves an <see cref="InstrumentKey"/> to its cross-venue identifiers.
///
/// THIS IS WHAT MAKES BLENDING REAL. The same company held at two brokers arrives as two
/// different canonical keys — one venue's listing and another's — and only an ISIN or a FIGI
/// can prove they are one position. Without it the dashboard shows the user two half-positions
/// and no total.
///
/// It is a separate port because the answer comes from the instrument master, which is
/// populated by a scheduled ingest with completely different failure modes from a portfolio
/// fetch. When it cannot answer, blending falls back to the canonical key, which is correct but
/// conservative: it will show two rows where one was possible, and it will never merge two
/// instruments that are not the same.
/// </summary>
public interface IInstrumentIdentityResolver
{
    ValueTask<InstrumentIdentity> ResolveAsync(InstrumentKey key, CancellationToken ct = default);
}

/// <summary>
/// Knows nothing, and says so. The default until the instrument master ships.
///
/// With this in place, cross-listed positions group by canonical instrument key only — the
/// conservative direction. It never merges two positions that are not provably the same
/// instrument, which is the error that matters: an over-merged portfolio reports exposure the
/// user does not have.
/// </summary>
public sealed class NullInstrumentIdentityResolver : IInstrumentIdentityResolver
{
    public static readonly NullInstrumentIdentityResolver Instance = new();

    public ValueTask<InstrumentIdentity> ResolveAsync(InstrumentKey key, CancellationToken ct = default) =>
        ValueTask.FromResult(InstrumentIdentity.Unknown);
}
