using System.Runtime.CompilerServices;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Moomoo;

/// <summary>
/// Instrument reference data from OpenD's static-information protocol.
///
/// OpenD has no downloadable master. Instead, static information requested for a whole market and
/// security type returns every security of that type — which IS the master, one request per list. This
/// connector reads US equities and ETFs, and Hong Kong equities, ETFs and indices. Options are not
/// enumerated: a single US underlying has thousands of contracts, and they are loaded per expiry from the
/// option chain when something asks for them.
///
/// The listing exchange on each US row is what makes decoding possible at all: OpenD's symbols are
/// <c>US.AAPL</c> with no venue, and the canonical key needs one.
/// </summary>
public sealed class MoomooReference : IConnectorReference
{
    /// <summary>What an ingest reads, in order. The US first: it is where most moomoo accounts trade.</summary>
    private static readonly (MoomooMarket Market, int SecType)[] Plan =
    [
        (MoomooMarket.Us, MoomooMaps.SecurityTypeEquity),
        (MoomooMarket.Us, MoomooMaps.SecurityTypeTrust),
        (MoomooMarket.Hk, MoomooMaps.SecurityTypeEquity),
        (MoomooMarket.Hk, MoomooMaps.SecurityTypeTrust),
        (MoomooMarket.Hk, MoomooMaps.SecurityTypeIndex),
    ];

    private readonly MoomooChannel _channel;
    private readonly MoomooOptions _options;
    private readonly MoomooInstrumentCache _cache;
    private readonly MoomooInstrumentResolver _resolver;
    private readonly Func<Result<BrokerSession>> _requireSession;
    private readonly ILogger _logger;

    internal MoomooReference(
        MoomooChannel channel,
        MoomooOptions options,
        MoomooInstrumentCache cache,
        MoomooInstrumentResolver resolver,
        Func<Result<BrokerSession>> requireSession,
        ILogger logger)
    {
        _channel = channel;
        _options = options;
        _cache = cache;
        _resolver = resolver;
        _requireSession = requireSession;
        _logger = logger;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<InstrumentDefinition> GetInstrumentsAsync(
        Venue? venue = null,
        AssetClass? assetClass = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_requireSession().IsFailure)
        {
            yield break;
        }

        MoomooMarket? onlyMarket = null;
        if (venue is { } requestedVenue)
        {
            var market = MoomooMaps.MarketForVenue(requestedVenue);
            if (market.IsFailure)
            {
                yield break;
            }

            onlyMarket = market.Value;
        }

        var anyRead = false;

        foreach (var (market, secType) in Plan)
        {
            if (onlyMarket is not null && market != onlyMarket)
            {
                continue;
            }

            if (assetClass is { } wanted && MoomooMaps.ToCanonicalAssetClass(secType) is { IsSuccess: true } mapped && mapped.Value != wanted)
            {
                continue;
            }

            var response = await _channel.RequestAsync<OpenDGetStaticInfoC2S, OpenDGetStaticInfoS2C>(
                MoomooProtoId.QotGetStaticInfo,
                new OpenDGetStaticInfoC2S { Market = market.QotMarket, SecType = secType },
                _options.InstrumentListTimeout,
                ct).ConfigureAwait(false);

            if (response.IsFailure)
            {
                // One list failing must not abandon the rest. A trader with US equities loaded can trade
                // them; the miss is visible in the connector's health and in this log.
                _logger.LogWarning(
                    "{ConnectorId}: could not read the {Market} security list (type {SecType}): {Error}",
                    MoomooAuth.ConnectorId,
                    market.Prefix,
                    secType,
                    response.Error.ToString());
                continue;
            }

            anyRead = true;

            foreach (var record in _resolver.Accept(response.Value.StaticInfoList ?? []))
            {
                if (venue is { } exact && record.Definition.Key.Venue != exact)
                {
                    continue;
                }

                yield return record.Definition;
            }
        }

        // Claim the master only when something arrived; marking it loaded after every list failed would
        // silence the guidance that it needs loading.
        if (anyRead)
        {
            _cache.MarkLoaded();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Strict about the venue. OpenD may list a fund on a different US venue from the one a key names —
    /// see <see cref="MoomooMaps.ToCanonicalVenue"/> — and handing back a definition whose key differs from
    /// the one asked for would let a caller believe two different keys were the same instrument.
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
    /// Before an ingest has run, the query is tried as a code in each market — what a trader types into a
    /// search box first is usually a ticker — so search is useful straight after linking.
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
        if (found.Count > 0 || _cache.IsLoaded)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Success(found);
        }

        var code = query.Trim().ToUpperInvariant();
        var probes = new List<OpenDSecurity>();

        if (code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
        {
            probes.Add(new OpenDSecurity { Market = MoomooMarket.Us.QotMarket, Code = code });
        }

        if (code.All(char.IsAsciiDigit) && code.Length <= 5)
        {
            probes.Add(new OpenDSecurity { Market = MoomooMarket.Hk.QotMarket, Code = MoomooNative.CanonicalSymbol(MoomooMarket.Hk, code) });
        }

        if (probes.Count == 0)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Success([]);
        }

        var ensured = await _resolver.EnsureAsync(_channel, probes, ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Failure(ensured.Error);
        }

        var results = new List<InstrumentDefinition>();
        foreach (var probe in probes)
        {
            if (MoomooMaps.MarketForQotMarket(probe.Market) is { IsSuccess: true } market
                && _cache.TryGetByNative(MoomooNative.Qualify(market.Value, probe.Code), out var record))
            {
                results.Add(record.Definition);
            }
        }

        return Result<IReadOnlyList<InstrumentDefinition>>.Success([.. results.Take(limit)]);
    }
}
