using System.Runtime.CompilerServices;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Tiger;

/// <summary>
/// Instrument reference data through Tiger's contract methods.
///
/// Tiger can list a market's symbols, but that list carries no listing venue, currency or lot — the things a
/// canonical definition needs — and describing each contract one by one would be tens of thousands of requests. So
/// <see cref="GetInstrumentsAsync"/> yields nothing, and contracts are described on demand: when a key is resolved,
/// a search names a symbol, or an order, fill or position carries one. Enumerating instruments is the canonical
/// master's job.
/// </summary>
public sealed class TigerReference : IConnectorReference
{
    private readonly TigerChannel _channel;
    private readonly TigerOptions _options;
    private readonly TigerInstrumentCache _cache;
    private readonly TigerInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;

    internal TigerReference(
        TigerChannel channel,
        TigerOptions options,
        TigerInstrumentCache cache,
        TigerInstrumentResolver resolver,
        Func<Result<BrokerSession>> requireSession)
    {
        _channel = channel;
        _options = options;
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
    /// Strict about the key: a contract Tiger lists on a different venue from the one a key names is not found, never
    /// substituted. An Etf key resolves to the stock on the same venue and symbol, which is how Tiger describes an ETF.
    /// </remarks>
    public async Task<Result<InstrumentDefinition>> ResolveAsync(InstrumentKey key, CancellationToken ct = default)
    {
        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<InstrumentDefinition>.Failure(account.Error);
        }

        var api = _channel.Trading(account.Value.Credentials);
        var record = await _resolver.ForKeyAsync(api, key, account.Value.Credentials, ct).ConfigureAwait(false);

        if (record.IsFailure)
        {
            return Result<InstrumentDefinition>.Failure(record.Error);
        }

        return TigerInstrumentResolver.Matches(key, record.Value.Definition.Key)
            ? record.Value.Definition
            : Result<InstrumentDefinition>.Failure(ConnectorErrors.InstrumentNotFound(key));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Searches what has already been described, then asks Tiger to describe the query as a symbol. Tiger's contract
    /// lookup matches a symbol exactly — it is not a free-text search — so a partial name finds nothing until the
    /// canonical master is consulted instead.
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

        var account = TigerAccount.Require(_requireSession);
        if (account.IsFailure)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Failure(account.Error);
        }

        var found = _cache.Search(query, limit);
        if (found.Count > 0)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Success(found);
        }

        var api = _channel.Trading(account.Value.Credentials);
        var symbol = query.Trim().ToUpperInvariant();

        var biz = TigerBiz.New()
            .Add("account", account.Value.AccountId)
            .Add("symbols", new List<string> { symbol })
            .Add("sec_type", TigerMaps.SecurityTypeStock)
            .Add("lang", _options.Language);

        var response = await api
            .CallAsync<TigerPage<TigerContract>>(TigerMaps.MethodContracts, biz, account.Value.Credentials, isTradeWrite: false, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            // A symbol Tiger does not know is a refusal, not a failure of the search.
            return response.Error.Code is ConnectorErrorCodes.InstrumentNotFound
                ? Result<IReadOnlyList<InstrumentDefinition>>.Success([])
                : Result<IReadOnlyList<InstrumentDefinition>>.Failure(response.Error);
        }

        var definitions = new List<InstrumentDefinition>();

        foreach (var contract in response.Value.Items ?? [])
        {
            var record = await _resolver.ForContractAsync(api, contract.Fields(), account.Value.Credentials, ct).ConfigureAwait(false);
            if (record.IsSuccess)
            {
                definitions.Add(record.Value.Definition);
            }
        }

        return Result<IReadOnlyList<InstrumentDefinition>>.Success([.. definitions.Take(limit)]);
    }
}
