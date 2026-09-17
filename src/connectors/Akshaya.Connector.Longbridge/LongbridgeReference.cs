using System.Runtime.CompilerServices;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Longbridge;

/// <summary>
/// Instrument reference data from Longbridge's static-information request.
///
/// Longbridge publishes NO security master. Static information answers for symbols the caller already names,
/// and no request lists a market's securities. So <see cref="GetInstrumentsAsync"/> yields nothing, and every
/// security this connector describes is resolved on demand — when a key is resolved, a search probes a code,
/// or an order, fill or position names one. Enumerating instruments is the canonical master's job.
/// </summary>
public sealed class LongbridgeReference : IConnectorReference
{
    private readonly LongbridgeChannel _channel;
    private readonly LongbridgeInstrumentCache _cache;
    private readonly LongbridgeInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;

    internal LongbridgeReference(
        LongbridgeChannel channel,
        LongbridgeInstrumentCache cache,
        LongbridgeInstrumentResolver resolver,
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
    /// Strict about the key. Longbridge may list a fund on a different US venue from the one a key names, and
    /// handing back a definition whose key differs from the one asked for would let a caller believe two
    /// different keys were the same instrument.
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

        return record.Value.Definition.Key == key
            ? record.Value.Definition
            : Result<InstrumentDefinition>.Failure(ConnectorErrors.InstrumentNotFound(key));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Searches what this connector has already described first. With nothing there, the query is tried as a
    /// code in each market it could belong to — a ticker in the US, a number in Hong Kong, a short alphanumeric
    /// code in Singapore — because what a trader types into a search box first is usually a code.
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

        var code = query.Trim().ToUpperInvariant();
        var probes = new List<string>();

        if (code.Any(char.IsAsciiLetter) && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
        {
            probes.Add(LongbridgeNative.Qualify(LongbridgeMarket.Us, code));
        }

        if (code.Length <= 5 && code.All(char.IsAsciiDigit))
        {
            probes.Add(LongbridgeNative.Qualify(LongbridgeMarket.Hk, LongbridgeNative.ToNativeCode(LongbridgeMarket.Hk, code)));
        }

        if (code.Length is >= 2 and <= 4 && code.All(char.IsAsciiLetterOrDigit) && code.Any(char.IsAsciiDigit))
        {
            probes.Add(LongbridgeNative.Qualify(LongbridgeMarket.Sg, code));
        }

        if (probes.Count == 0)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Success([]);
        }

        var ensured = await _resolver.EnsureAsync(_channel, probes, ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            // Longbridge may refuse a whole batch over one symbol it does not know. Ask for each on its own before
            // concluding; if every one of those fails too, the failure is real and is reported.
            var lastFailure = ensured.Error;
            var anySucceeded = false;

            foreach (var probe in probes)
            {
                var single = await _resolver.EnsureAsync(_channel, [probe], ct).ConfigureAwait(false);
                if (single.IsSuccess)
                {
                    anySucceeded = true;
                }
                else
                {
                    lastFailure = single.Error;
                }
            }

            if (!anySucceeded)
            {
                return Result<IReadOnlyList<InstrumentDefinition>>.Failure(lastFailure);
            }
        }

        var results = new List<InstrumentDefinition>();
        foreach (var probe in probes)
        {
            if (_cache.TryGetByNative(probe, out var record))
            {
                results.Add(record.Definition);
            }
        }

        return Result<IReadOnlyList<InstrumentDefinition>>.Success([.. results.Take(limit)]);
    }
}
