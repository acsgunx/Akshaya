using System.Diagnostics.CodeAnalysis;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Moomoo;

/// <summary>One security as OpenD describes it, translated to its canonical definition.</summary>
/// <param name="Definition">The canonical instrument.</param>
/// <param name="Market">Which of the two markets it trades in.</param>
/// <param name="Code">OpenD's code: <c>AAPL</c>, <c>00700</c>, <c>AAPL260918C200000</c>.</param>
/// <param name="Native">The SDK notation, <c>US.AAPL</c> — this connector's native symbol.</param>
internal sealed record MoomooInstrumentRecord(
    InstrumentDefinition Definition,
    MoomooMarket Market,
    string Code,
    string Native)
{
    public OpenDSecurity Security => new() { Market = Market.QotMarket, Code = Code };
}

/// <summary>
/// The SDK's <c>MARKET.CODE</c> notation, and the structural rules that need no lookup.
/// </summary>
internal static class MoomooNative
{
    public static string Qualify(MoomooMarket market, string code) => $"{market.Prefix}.{code}";

    /// <summary>
    /// Splits at the FIRST dot. US codes contain dots of their own — Berkshire's class B is
    /// <c>BRK.B</c>, so its native symbol is <c>US.BRK.B</c> — and splitting at the last dot would
    /// look for a market called "US.BRK".
    /// </summary>
    public static bool TrySplit(string? native, [NotNullWhen(true)] out MoomooMarket? market, [NotNullWhen(true)] out string? code)
    {
        market = null;
        code = null;

        var dot = native?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        if (native is null || dot <= 0 || dot == native.Length - 1)
        {
            return false;
        }

        var resolved = MoomooMaps.MarketForPrefix(native[..dot]);
        if (resolved.IsFailure)
        {
            return false;
        }

        market = resolved.Value;
        code = native[(dot + 1)..].Trim().ToUpperInvariant();
        return true;
    }

    /// <summary>
    /// The canonical symbol for a code. Hong Kong codes are the five-digit HKEX form — <c>00700</c>, not
    /// <c>700</c> — so the same share keys the same way whichever connector saw it first. Index codes are
    /// longer than five digits and left alone.
    /// </summary>
    public static string CanonicalSymbol(MoomooMarket market, string code)
    {
        var trimmed = code.Trim().ToUpperInvariant();
        return market == MoomooMarket.Hk && trimmed.Length < 5 && trimmed.All(char.IsAsciiDigit)
            ? trimmed.PadLeft(5, '0')
            : trimmed;
    }

    /// <summary>
    /// A cash security's market and code, derived without any lookup. Only the ORDER direction can do
    /// this: OpenD addresses a US security by symbol alone, so any US MIC reaches it. Decoding needs the
    /// listing venue, which the symbol does not carry — that direction always goes through the cache.
    /// </summary>
    public static Result<(MoomooMarket Market, string Code)> ForCashKey(InstrumentKey key)
    {
        if (key.AssetClass is not (AssetClass.Equity or AssetClass.Etf or AssetClass.Index))
        {
            return Result<(MoomooMarket, string)>.Failure(ConnectorErrors.InstrumentNotFound(key));
        }

        var market = MoomooMaps.MarketForVenue(key.Venue);
        if (market.IsFailure || string.IsNullOrWhiteSpace(key.Symbol))
        {
            return Result<(MoomooMarket, string)>.Failure(ConnectorErrors.InstrumentNotFound(key));
        }

        var symbol = key.Symbol.Trim().ToUpperInvariant();
        if (market.Value == MoomooMarket.Hk && !symbol.All(char.IsAsciiDigit))
        {
            return Result<(MoomooMarket, string)>.Failure(ConnectorErrors.InstrumentNotFound(key));
        }

        return (market.Value, CanonicalSymbol(market.Value, symbol));
    }

    public static Error NotFound(string native) => new(
        ConnectorErrorCodes.InstrumentNotFound,
        $"moomoo reported {native}, which this connector cannot describe: it is delisted, outside the declared "
        + "venues, or OpenD has no static information for it.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["native"] = native });
}

/// <summary>Builds canonical records from OpenD static information.</summary>
internal static class MoomooInstrumentFactory
{
    /// <param name="info">One static-info entry.</param>
    /// <param name="venueOfNative">
    /// The listing venue of an already-cached security. An option's canonical venue is its UNDERLYING's
    /// listing venue — the same rule every connector here follows, so AAPL options key under XNAS
    /// whichever broker reported them — and OpenD's option rows name the underlying but not its exchange.
    /// </param>
    public static Result<MoomooInstrumentRecord> Build(OpenDSecurityStaticInfo info, Func<string, Venue?> venueOfNative)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (info.Basic is not { Security: { } security } basic || string.IsNullOrWhiteSpace(security.Code))
        {
            return Result<MoomooInstrumentRecord>.Failure(MoomooErrors.MissingField(MoomooProtoId.QotGetStaticInfo, "basic.security"));
        }

        var market = MoomooMaps.MarketForQotMarket(security.Market);
        if (market.IsFailure)
        {
            return Result<MoomooInstrumentRecord>.Failure(market.Error);
        }

        var code = security.Code.Trim().ToUpperInvariant();
        var native = MoomooNative.Qualify(market.Value, code);

        // OpenD answers for an unknown code with a row marked delisted rather than an error.
        if (basic.Delisting == true)
        {
            return Result<MoomooInstrumentRecord>.Failure(MoomooNative.NotFound(native));
        }

        var assetClass = MoomooMaps.ToCanonicalAssetClass(basic.SecType ?? 0);
        if (assetClass.IsFailure)
        {
            return Result<MoomooInstrumentRecord>.Failure(assetClass.Error);
        }

        var lotSize = basic.LotSize is > 0 ? basic.LotSize.Value : 1;
        InstrumentKey key;
        decimal definitionLot;
        decimal multiplier;

        if (assetClass.Value == AssetClass.Option)
        {
            if (info.OptionExData is not { Owner: { } owner } option
                || MoomooTime.ParseDate(option.StrikeTime) is not { } expiry
                || MoomooNumber.Decimal(option.StrikePrice, 4) is not { } strike
                || strike <= 0m)
            {
                return Result<MoomooInstrumentRecord>.Failure(MoomooErrors.MissingField(MoomooProtoId.QotGetStaticInfo, "optionExData"));
            }

            var right = MoomooMaps.ToCanonicalOptionRight(option.Type);
            var ownerMarket = MoomooMaps.MarketForQotMarket(owner.Market);
            if (right.IsFailure || ownerMarket.IsFailure)
            {
                return Result<MoomooInstrumentRecord>.Failure(right.IsFailure ? right.Error : ownerMarket.Error);
            }

            var ownerNative = MoomooNative.Qualify(ownerMarket.Value, owner.Code.Trim().ToUpperInvariant());
            Venue? venue = ownerMarket.Value == MoomooMarket.Hk ? Venue.Hkex : venueOfNative(ownerNative);
            if (venue is null)
            {
                return Result<MoomooInstrumentRecord>.Failure(MoomooNative.NotFound(ownerNative));
            }

            key = new InstrumentKey(
                venue.Value,
                MoomooNative.CanonicalSymbol(ownerMarket.Value, owner.Code),
                AssetClass.Option,
                expiry,
                strike,
                right.Value);

            // For options OpenD's lotSize IS the contract multiplier (100 on a US equity option). The
            // tradable increment is one contract.
            definitionLot = 1m;
            multiplier = lotSize;
        }
        else
        {
            Venue venue;
            if (market.Value == MoomooMarket.Hk)
            {
                // Hong Kong has one exchange, so the market alone is a certain answer.
                venue = Venue.Hkex;
            }
            else
            {
                var mapped = MoomooMaps.ToCanonicalVenue(basic.ExchType ?? 0);
                if (mapped.IsFailure)
                {
                    return Result<MoomooInstrumentRecord>.Failure(mapped.Error);
                }

                venue = mapped.Value;
            }

            key = new InstrumentKey(venue, MoomooNative.CanonicalSymbol(market.Value, code), assetClass.Value);
            definitionLot = lotSize;
            multiplier = 1m;
        }

        var definition = new InstrumentDefinition
        {
            Key = key,
            Name = string.IsNullOrWhiteSpace(basic.Name) ? code : basic.Name.Trim(),
            Currency = market.Value.Currency,

            // Hong Kong trades in board lots (100 shares of 00700) and the risk gate enforces whole
            // lots from this. Odd lots trade on a separate book this connector does not route to.
            LotSize = definitionLot,
            TickSize = market.Value.TickSize,
            Multiplier = multiplier,
            SettlementDays = market.Value.SettlementDays,

            // Indices are quoted, not traded. Keeping them searchable matters: a trader charting the
            // Hang Seng has to be able to find it.
            IsTradable = assetClass.Value != AssetClass.Index,
        };

        return new MoomooInstrumentRecord(definition, market.Value, code, native);
    }
}

/// <summary>
/// Every security this connector has had described to it, shared by every facet of one connector.
///
/// A reader-writer lock rather than concurrent dictionaries, because reads vastly outnumber writes and
/// the three indices must change together — a native lookup that succeeded while the key index had not
/// caught up would translate an order one way and its fill another.
/// </summary>
internal sealed class MoomooInstrumentCache : IDisposable
{
    private readonly Dictionary<string, MoomooInstrumentRecord> _byNative = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<InstrumentKey, MoomooInstrumentRecord> _byKey = [];
    private readonly HashSet<string> _outOfScope = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MoomooInstrumentRecord> _all = [];
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);

    private int _skippedRows;
    private int _loaded;
    private bool _disposed;

    /// <summary>True once a whole-market ingest has completed for at least one market.</summary>
    public bool IsLoaded => Volatile.Read(ref _loaded) == 1;

    /// <summary>Static-info rows not kept: warrants, OTC names, delisted codes. Non-zero is normal.</summary>
    public int SkippedRows => Volatile.Read(ref _skippedRows);

    public int Count
    {
        get
        {
            _gate.EnterReadLock();
            try
            {
                return _all.Count;
            }
            finally
            {
                _gate.ExitReadLock();
            }
        }
    }

    public void Add(MoomooInstrumentRecord record) => AddRange([record]);

    /// <summary>First description wins; static information does not change during a session.</summary>
    public void AddRange(IReadOnlyCollection<MoomooInstrumentRecord> records)
    {
        _gate.EnterWriteLock();
        try
        {
            foreach (var record in records)
            {
                if (_byNative.TryAdd(record.Native, record))
                {
                    _byKey.TryAdd(record.Definition.Key, record);
                    _all.Add(record);
                    _outOfScope.Remove(record.Native);
                }
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>
    /// Remembers a security OpenD described but this connector does not trade — an OTC name, a warrant.
    /// Order and position reads skip rows for these instead of failing, exactly as the Indian connectors
    /// skip rows for segments outside their venues. A security OpenD could not describe at all is NOT
    /// recorded here, so it still fails loudly.
    /// </summary>
    public void MarkOutOfScope(string native)
    {
        _gate.EnterWriteLock();
        try
        {
            _outOfScope.Add(native);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public bool IsOutOfScope(string native)
    {
        _gate.EnterReadLock();
        try
        {
            return _outOfScope.Contains(native);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public void MarkLoaded() => Volatile.Write(ref _loaded, 1);

    public void RecordSkippedRow() => Interlocked.Increment(ref _skippedRows);

    public bool TryGetByNative(string native, [NotNullWhen(true)] out MoomooInstrumentRecord? record)
    {
        _gate.EnterReadLock();
        try
        {
            return _byNative.TryGetValue(native, out record);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public bool TryGetByKey(InstrumentKey key, [NotNullWhen(true)] out MoomooInstrumentRecord? record)
    {
        _gate.EnterReadLock();
        try
        {
            return _byKey.TryGetValue(key, out record);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>
    /// One option contract within a market, matched on everything but the venue. OpenD's US option
    /// market is not split by listing exchange, so a contract keyed on ARCX:SPY by another broker is the
    /// same contract moomoo keys on the venue it reports for SPY.
    /// </summary>
    public bool TryFindOption(
        MoomooMarket market,
        string underlying,
        DateOnly expiry,
        decimal strike,
        OptionRight right,
        [NotNullWhen(true)] out MoomooInstrumentRecord? record)
    {
        _gate.EnterReadLock();
        try
        {
            foreach (var candidate in _all)
            {
                var key = candidate.Definition.Key;
                if (candidate.Market == market
                    && key.AssetClass == AssetClass.Option
                    && key.Expiry == expiry
                    && key.Strike == strike
                    && key.Right == right
                    && string.Equals(key.Symbol, underlying, StringComparison.OrdinalIgnoreCase))
                {
                    record = candidate;
                    return true;
                }
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        record = null;
        return false;
    }

    /// <summary>Every cached option on one underlying and expiry, in strike order.</summary>
    public IReadOnlyList<MoomooInstrumentRecord> OptionsFor(MoomooMarket market, string underlying, DateOnly expiry)
    {
        var matches = new List<MoomooInstrumentRecord>();

        _gate.EnterReadLock();
        try
        {
            foreach (var candidate in _all)
            {
                var key = candidate.Definition.Key;
                if (candidate.Market == market
                    && key.AssetClass == AssetClass.Option
                    && key.Expiry == expiry
                    && string.Equals(key.Symbol, underlying, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(candidate);
                }
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        matches.Sort(static (left, right) => (left.Definition.Key.Strike ?? 0m).CompareTo(right.Definition.Key.Strike ?? 0m));
        return matches;
    }

    /// <summary>Symbol prefix matches first, then name and native-symbol substring matches. Options are not searched.</summary>
    public IReadOnlyList<InstrumentDefinition> Search(string query, int limit)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var needle = query.Trim();
        var prefix = new List<InstrumentDefinition>();
        var contains = new List<InstrumentDefinition>();

        _gate.EnterReadLock();
        try
        {
            foreach (var record in _all)
            {
                if (record.Definition.Key.AssetClass == AssetClass.Option)
                {
                    continue;
                }

                if (record.Definition.Key.Symbol.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                {
                    prefix.Add(record.Definition);
                    if (prefix.Count >= limit)
                    {
                        return prefix;
                    }
                }
                else if (contains.Count < limit
                         && (record.Native.Contains(needle, StringComparison.OrdinalIgnoreCase)
                             || record.Definition.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                {
                    contains.Add(record.Definition);
                }
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        foreach (var candidate in contains)
        {
            if (prefix.Count >= limit)
            {
                break;
            }

            prefix.Add(candidate);
        }

        return prefix;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}

/// <summary>
/// Canonical identity to OpenD's <c>MARKET.CODE</c> notation and back, over the instrument cache.
///
/// Cash securities encode STRUCTURALLY — any US MIC to <c>US.</c>, HKEX to <c>HK.</c> — so orders on
/// equities never wait for a lookup. Everything else needs the cache: decoding, because the listing
/// venue is not in the symbol, and option contracts, whose codes only OpenD's option chain knows.
/// The facets fill the cache through <see cref="MoomooInstrumentResolver"/> before they translate.
/// </summary>
internal sealed class MoomooSymbolTranslator(MoomooInstrumentCache cache) : ISymbolTranslator
{
    public Result<string> ToNative(InstrumentKey key)
    {
        if (cache.TryGetByKey(key, out var record))
        {
            return record.Native;
        }

        var cash = MoomooNative.ForCashKey(key);
        return cash.IsSuccess
            ? MoomooNative.Qualify(cash.Value.Market, cash.Value.Code)
            : Result<string>.Failure(ConnectorErrors.InstrumentNotFound(key));
    }

    public Result<InstrumentKey> ToCanonical(string nativeSymbol, string? nativeExchange = null) =>
        cache.TryGetByNative(nativeSymbol, out var record)
            ? record.Definition.Key
            : Result<InstrumentKey>.Failure(MoomooNative.NotFound(nativeSymbol));
}

/// <summary>
/// Fills the instrument cache from OpenD on demand: static information for securities it has not seen,
/// and the option chain for contracts it cannot name.
/// </summary>
internal sealed class MoomooInstrumentResolver(MoomooInstrumentCache cache, MoomooOptions options)
{
    /// <summary>
    /// Makes sure every security listed is in the cache, with one static-info request per chunk of
    /// unknown ones. Options are resolved AFTER their underlyings, because an option's canonical venue is
    /// its underlying's listing venue.
    /// </summary>
    public async Task<Result> EnsureAsync(IMoomooRequester requester, IEnumerable<OpenDSecurity> securities, CancellationToken ct)
    {
        var missing = new List<OpenDSecurity>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var security in securities)
        {
            if (string.IsNullOrWhiteSpace(security.Code) || MoomooMaps.MarketForQotMarket(security.Market) is not { IsSuccess: true } market)
            {
                continue;
            }

            var normalised = new OpenDSecurity { Market = market.Value.QotMarket, Code = security.Code.Trim().ToUpperInvariant() };
            var native = MoomooNative.Qualify(market.Value, normalised.Code);

            if (seen.Add(native) && !cache.TryGetByNative(native, out _) && !cache.IsOutOfScope(native))
            {
                missing.Add(normalised);
            }
        }

        if (missing.Count == 0)
        {
            return Result.Success();
        }

        var options = new List<OpenDSecurityStaticInfo>();

        foreach (var chunk in missing.Chunk(MaxChunk))
        {
            var response = await requester.RequestAsync<OpenDGetStaticInfoC2S, OpenDGetStaticInfoS2C>(
                MoomooProtoId.QotGetStaticInfo,
                new OpenDGetStaticInfoC2S { SecurityList = [.. chunk] },
                ct).ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result.Failure(response.Error);
            }

            foreach (var info in response.Value.StaticInfoList ?? [])
            {
                if (info.Basic?.SecType == MoomooMaps.SecurityTypeDerivative)
                {
                    options.Add(info);
                    continue;
                }

                Keep(info);
            }
        }

        if (options.Count > 0)
        {
            var owners = options.Select(o => o.OptionExData?.Owner).OfType<OpenDSecurity>().ToList();
            var ownersLoaded = await EnsureAsync(requester, owners, ct).ConfigureAwait(false);
            if (ownersLoaded.IsFailure)
            {
                return ownersLoaded;
            }

            foreach (var info in options)
            {
                Keep(info);
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// The record for a key, looking it up if needed. Cash keys resolve by static information; option keys
    /// load their chain.
    /// </summary>
    public async Task<Result<MoomooInstrumentRecord>> ForKeyAsync(IMoomooRequester requester, InstrumentKey key, CancellationToken ct)
    {
        if (cache.TryGetByKey(key, out var cached))
        {
            return cached;
        }

        if (key.AssetClass == AssetClass.Option)
        {
            if (key is not { Expiry: { } expiry, Strike: { } strike, Right: { } right })
            {
                return Result<MoomooInstrumentRecord>.Failure(ConnectorErrors.InstrumentNotFound(key));
            }

            var underlying = MoomooNative.ForCashKey(key with { AssetClass = AssetClass.Equity, Expiry = null, Strike = null, Right = null });
            if (underlying.IsFailure)
            {
                return Result<MoomooInstrumentRecord>.Failure(ConnectorErrors.InstrumentNotFound(key));
            }

            var (market, code) = underlying.Value;
            var loaded = await EnsureOptionChainAsync(requester, market, code, expiry, ct).ConfigureAwait(false);
            if (loaded.IsFailure)
            {
                return Result<MoomooInstrumentRecord>.Failure(loaded.Error);
            }

            return cache.TryFindOption(market, code, expiry, strike, right, out var option)
                ? option
                : Result<MoomooInstrumentRecord>.Failure(ConnectorErrors.InstrumentNotFound(key));
        }

        var cash = MoomooNative.ForCashKey(key);
        if (cash.IsFailure)
        {
            return Result<MoomooInstrumentRecord>.Failure(cash.Error);
        }

        var native = MoomooNative.Qualify(cash.Value.Market, cash.Value.Code);
        var ensured = await EnsureAsync(
            requester,
            [new OpenDSecurity { Market = cash.Value.Market.QotMarket, Code = cash.Value.Code }],
            ct).ConfigureAwait(false);

        if (ensured.IsFailure)
        {
            return Result<MoomooInstrumentRecord>.Failure(ensured.Error);
        }

        return cache.TryGetByNative(native, out var record)
            ? record
            : Result<MoomooInstrumentRecord>.Failure(ConnectorErrors.InstrumentNotFound(key));
    }

    /// <summary>Loads every contract on one underlying for one expiry into the cache.</summary>
    public async Task<Result> EnsureOptionChainAsync(
        IMoomooRequester requester,
        MoomooMarket market,
        string underlyingCode,
        DateOnly expiry,
        CancellationToken ct)
    {
        var owner = new OpenDSecurity { Market = market.QotMarket, Code = underlyingCode };

        var ownerLoaded = await EnsureAsync(requester, [owner], ct).ConfigureAwait(false);
        if (ownerLoaded.IsFailure)
        {
            return ownerLoaded;
        }

        var day = MoomooTime.FormatDate(expiry);
        var chain = await requester.RequestAsync<OpenDGetOptionChainC2S, OpenDGetOptionChainS2C>(
            MoomooProtoId.QotGetOptionChain,
            new OpenDGetOptionChainC2S { Owner = owner, BeginTime = day, EndTime = day },
            ct).ConfigureAwait(false);

        if (chain.IsFailure)
        {
            return Result.Failure(chain.Error);
        }

        foreach (var entry in chain.Value.OptionChain ?? [])
        {
            foreach (var item in entry.Option ?? [])
            {
                if (item.Call is { } call)
                {
                    Keep(call);
                }

                if (item.Put is { } put)
                {
                    Keep(put);
                }
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Builds and caches a batch of static-info rows — a whole-market list — returning the records kept.
    /// Rows that cannot be kept are counted, and ones outside the declared venues remembered as such.
    /// </summary>
    public IReadOnlyList<MoomooInstrumentRecord> Accept(IEnumerable<OpenDSecurityStaticInfo> infos)
    {
        var kept = new List<MoomooInstrumentRecord>();
        foreach (var info in infos)
        {
            if (Keep(info) is { } record)
            {
                kept.Add(record);
            }
        }

        return kept;
    }

    private int MaxChunk => Math.Max(1, options.MaxStaticInfoSecurities);

    private MoomooInstrumentRecord? Keep(OpenDSecurityStaticInfo info)
    {
        var built = MoomooInstrumentFactory.Build(info, VenueOf);
        if (built.IsSuccess)
        {
            cache.Add(built.Value);
            return cache.TryGetByNative(built.Value.Native, out var stored) ? stored : built.Value;
        }

        cache.RecordSkippedRow();

        if (built.Error.Code == ConnectorErrorCodes.NotSupported
            && info.Basic?.Security is { } security
            && MoomooMaps.MarketForQotMarket(security.Market) is { IsSuccess: true } market)
        {
            cache.MarkOutOfScope(MoomooNative.Qualify(market.Value, security.Code.Trim().ToUpperInvariant()));
        }

        return null;
    }

    private Venue? VenueOf(string native) =>
        cache.TryGetByNative(native, out var record) ? record.Definition.Key.Venue : null;
}
