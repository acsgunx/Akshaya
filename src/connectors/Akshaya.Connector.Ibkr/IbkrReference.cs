using System.Runtime.CompilerServices;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Ibkr;

/// <summary>
/// Instrument reference data through the gateway.
///
/// IBKR publishes no downloadable master through the Client Portal API, and enumerating a market contract by contract
/// against a ten-requests-a-second limit would take hours. So <see cref="GetInstrumentsAsync"/> yields nothing, and
/// contracts are resolved on demand: when a key is resolved, a search runs, or an order, fill or position names a
/// conid. Enumerating instruments is the canonical master's job.
/// </summary>
public sealed class IbkrReference : IConnectorReference
{
    private readonly IbkrChannel _channel;
    private readonly IbkrInstrumentCache _cache;
    private readonly IbkrInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;

    internal IbkrReference(
        IbkrChannel channel,
        IbkrInstrumentCache cache,
        IbkrInstrumentResolver resolver,
        Func<Result<BrokerSession>> requireSession)
    {
        _channel = channel;
        _cache = cache;
        _resolver = resolver;
        _requireSession = requireSession;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<InstrumentDefinition> GetInstrumentsAsync(
        Venue? venue = null,
        AssetClass? assetClass = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Strict about the key: a contract IBKR lists on a different venue from the one a key names is not found, never
    /// substituted. An Etf key resolves to the stock on the same venue and symbol, which is how IBKR describes an ETF.
    /// </remarks>
    public async Task<Result<InstrumentDefinition>> ResolveAsync(InstrumentKey key, CancellationToken ct = default)
    {
        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<InstrumentDefinition>.Failure(session.Error);
        }

        var record = await _resolver.ForKeyAsync(_channel, key, ct).ConfigureAwait(false);
        if (record.IsFailure)
        {
            return Result<InstrumentDefinition>.Failure(record.Error);
        }

        return IbkrInstrumentResolver.Matches(key, record.Value.Definition.Key)
            ? record.Value.Definition
            : Result<InstrumentDefinition>.Failure(ConnectorErrors.InstrumentNotFound(key));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Searches what has already been described first; otherwise asks IBKR's symbol search for stocks and describes
    /// what comes back, keeping the contracts on the venues this connector trades.
    /// </remarks>
    public async Task<Result<IReadOnlyList<InstrumentDefinition>>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Success([]);
        }

        var session = _requireSession();
        if (session.IsFailure)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Failure(session.Error);
        }

        var found = _cache.Search(query, limit);
        if (found.Count > 0)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Success(found);
        }

        var api = await _channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsFailure)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Failure(api.Error);
        }

        var response = await api.Value.SendElementAsync(
            HttpMethod.Get,
            HttpConnectorPath.WithQuery("iserver/secdef/search", ("symbol", query.Trim()), ("secType", IbkrMaps.SecTypeStock)),
            body: null,
            ct).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Failure(response.Error);
        }

        // Nothing found is answered as an object carrying an error, not as an empty list.
        if (response.Value.ValueKind != JsonValueKind.Array)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Success([]);
        }

        var conids = (response.Value.Deserialize<List<IbkrSearchResult>>(IbkrJson.Options) ?? [])
            .Select(r => IbkrNumber.Integer(r.Conid) ?? 0)
            .Where(c => c > 0)
            .Distinct()
            .Take(Math.Max(limit, 1) * 2)
            .ToList();

        var ensured = await _resolver.EnsureConidsAsync(_channel, conids, ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Failure(ensured.Error);
        }

        var definitions = new List<InstrumentDefinition>();
        foreach (var conid in conids)
        {
            if (_cache.TryGetByConid(conid, out var record))
            {
                definitions.Add(record.Definition);
            }
        }

        return Result<IReadOnlyList<InstrumentDefinition>>.Success([.. definitions.Take(limit)]);
    }
}
