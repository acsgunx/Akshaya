using System.Runtime.CompilerServices;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Paper;

/// <summary>
/// Instrument reference data, served from whatever universe the injected
/// <see cref="IMarketDataSource"/> was built with.
///
/// The Paper connector has no instrument master of its own on purpose. Its universe IS the
/// backtest's universe: if a strategy can be given prices for an instrument, it can trade it,
/// and if it cannot, resolution fails rather than succeeding against an instrument with no
/// prices behind it. A paper account that accepted orders on instruments the tape never
/// mentions would produce a backtest full of orders that never fill and no explanation why.
/// </summary>
/// <param name="source">The universe and its definitions.</param>
public sealed class PaperReference(IMarketDataSource source) : IConnectorReference
{
    /// <inheritdoc />
    /// <remarks>
    /// Not session-gated, unlike the trading facets. Reference data is not privileged — the
    /// host's ingest job walks it before any user has signed in — and refusing it would make a
    /// cold start look like an outage.
    /// </remarks>
    public async IAsyncEnumerable<InstrumentDefinition> GetInstrumentsAsync(
        Venue? venue = null,
        AssetClass? assetClass = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var definition in source.Instruments)
        {
            ct.ThrowIfCancellationRequested();

            if (venue is { } v && definition.Key.Venue != v)
            {
                continue;
            }

            if (assetClass is { } a && definition.Key.AssetClass != a)
            {
                continue;
            }

            yield return definition;
        }

        // The contract makes this an async stream because real masters are streamed from a
        // network read. Ours is already in memory, so there is nothing to await — but the
        // signature must not change, or every consumer would need two code paths.
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Result<InstrumentDefinition>> ResolveAsync(
        InstrumentKey key,
        CancellationToken ct = default) =>
        Task.FromResult(source.Resolve(key));

    /// <inheritdoc />
    /// <remarks>
    /// Case-insensitive substring match over symbol, name and ISIN, in the source's declared
    /// order. Deliberately not fuzzy: a search that helpfully returns a near-miss is one
    /// mis-click away from an order on the wrong instrument, and this connector is a rehearsal
    /// for exactly that muscle memory.
    /// </remarks>
    public Task<Result<IReadOnlyList<InstrumentDefinition>>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(Result<IReadOnlyList<InstrumentDefinition>>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "A search needs a non-empty query.")));
        }

        if (limit <= 0)
        {
            return Task.FromResult(Result<IReadOnlyList<InstrumentDefinition>>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "The result limit must be positive.")));
        }

        var needle = query.Trim();
        var matches = new List<InstrumentDefinition>();

        foreach (var definition in source.Instruments)
        {
            if (matches.Count >= limit)
            {
                break;
            }

            if (Contains(definition.Key.Symbol, needle)
                || Contains(definition.Name, needle)
                || Contains(definition.Isin, needle))
            {
                matches.Add(definition);
            }
        }

        return Task.FromResult(Result<IReadOnlyList<InstrumentDefinition>>.Success(matches));
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
