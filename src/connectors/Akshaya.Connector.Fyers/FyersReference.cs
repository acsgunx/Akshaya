using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Fyers;

/// <summary>
/// One row of a FYERS symbol master, parsed.
/// </summary>
/// <param name="Definition">The canonical instrument, as the platform will hold it.</param>
/// <param name="SymbolTicker">The exact FYERS ticker — "NSE:SBIN-EQ". Authoritative for orders.</param>
/// <param name="FyToken">FYERS' own instrument identifier, carried in several payloads.</param>
public sealed record FyersInstrumentRecord(
    InstrumentDefinition Definition,
    string SymbolTicker,
    string FyToken);

/// <summary>
/// Instrument reference data from the FYERS symbol master.
///
/// The master is seven public CSV files, one per exchange-segment, served unauthenticated from
/// <c>public.fyers.in</c> and rebuilt daily. This connector reads the four covering its declared
/// venues: NSE and BSE, cash and equity derivatives. The commodity and currency files are
/// deliberately not read — the manifest does not claim those venues, and ingesting instruments
/// the connector cannot trade would put untradable names in the search box.
///
/// The ingest is streamed rather than buffered. NSE's F&amp;O file alone is tens of thousands of
/// rows, and holding all four as strings before parsing them is a needless spike on a process
/// that is also serving orders.
/// </summary>
public sealed partial class FyersReference : IConnectorReference
{
    /// <summary>
    /// The master files this connector reads, in the order it reads them.
    ///
    /// Cash first, on purpose. It is by far the smaller half and it is what a user searches for
    /// in the first thirty seconds after linking an account, so a partially completed ingest is
    /// still immediately useful rather than being a list of option contracts.
    /// </summary>
    private static readonly string[] MasterFiles =
    [
        "NSE_CM.csv",
        "BSE_CM.csv",
        "NSE_FO.csv",
        "BSE_FO.csv",
    ];

    private const int BatchSize = 2_000;

    private readonly FyersApi _api;
    private readonly FyersInstrumentCache _cache;

    internal FyersReference(FyersApi api, FyersInstrumentCache cache)
    {
        _api = api;
        _cache = cache;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<InstrumentDefinition> GetInstrumentsAsync(
        Venue? venue = null,
        AssetClass? assetClass = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var batch = new List<FyersInstrumentRecord>(BatchSize);
        var anyFileRead = false;

        foreach (var file in MasterFiles)
        {
            ct.ThrowIfCancellationRequested();

            var stream = await _api.GetSymbolMasterAsync(file, ct).ConfigureAwait(false);
            if (stream.IsFailure)
            {
                // One unavailable file must not abandon the ingest. A trader with the cash master
                // loaded can trade equities; refusing to load anything because the BSE derivatives
                // file 404'd would take that away for no reason. The miss is visible in the
                // connector's health, which reports how many rows were skipped and loaded.
                continue;
            }

            anyFileRead = true;

            await using var body = stream.Value;
            using var reader = new StreamReader(body);

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!TryParseRow(line, out var record))
                {
                    // Rows for instrument types this connector does not trade — debentures,
                    // sovereign gold bonds, mutual funds — land here alongside genuinely broken
                    // ones. Both are counted; a sudden jump in the count is the signal that the
                    // file's shape changed.
                    _cache.RecordSkippedRow();
                    continue;
                }

                batch.Add(record);

                if (batch.Count >= BatchSize)
                {
                    _cache.AddRange(batch);
                    foreach (var item in Filter(batch, venue, assetClass))
                    {
                        yield return item;
                    }

                    batch.Clear();
                }
            }
        }

        if (batch.Count > 0)
        {
            _cache.AddRange(batch);
            foreach (var item in Filter(batch, venue, assetClass))
            {
                yield return item;
            }
        }

        // Only claim the master is loaded if at least one file actually arrived. Marking it
        // loaded after four failed downloads would silence the symbol translator's "load the
        // master" guidance while leaving it with nothing to look anything up in.
        if (anyFileRead)
        {
            _cache.MarkLoaded();
        }
    }

    /// <inheritdoc />
    public Task<Result<InstrumentDefinition>> ResolveAsync(InstrumentKey key, CancellationToken ct = default) =>
        Task.FromResult(_cache.TryGetDefinition(key, out var definition)
            ? Result<InstrumentDefinition>.Success(definition)
            : Result<InstrumentDefinition>.Failure(_cache.IsLoaded
                ? ConnectorErrors.InstrumentNotFound(key)
                : new Error(
                    ConnectorErrorCodes.InstrumentNotFound,
                    $"{key} could not be resolved because the FYERS symbol master has not been "
                    + "ingested yet. Run IConnectorReference.GetInstrumentsAsync first.")));

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<InstrumentDefinition>>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken ct = default) =>
        Task.FromResult(string.IsNullOrWhiteSpace(query)
            ? Result<IReadOnlyList<InstrumentDefinition>>.Success([])
            : Result<IReadOnlyList<InstrumentDefinition>>.Success(_cache.Search(query, limit)));

    private static IEnumerable<InstrumentDefinition> Filter(
        List<FyersInstrumentRecord> batch,
        Venue? venue,
        AssetClass? assetClass)
    {
        foreach (var record in batch)
        {
            var key = record.Definition.Key;

            if (venue is { } wanted && key.Venue != wanted)
            {
                continue;
            }

            if (assetClass is { } wantedClass && key.AssetClass != wantedClass)
            {
                continue;
            }

            yield return record.Definition;
        }
    }

    /// <summary>
    /// Parses one master row.
    ///
    /// The files are headerless and POSITIONAL: twenty-one comma-separated columns whose meaning
    /// comes entirely from their index. The constants in <see cref="Column"/> are therefore the
    /// contract with the vendor, and the field-count check below is what stops a column being
    /// inserted upstream and every price silently becoming a lot size.
    ///
    /// Returns false rather than throwing for anything unreadable. A single malformed row must
    /// cost one instrument, not the whole master.
    /// </summary>
    internal static bool TryParseRow(string line, [NotNullWhen(true)] out FyersInstrumentRecord? record)
    {
        record = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var fields = line.Split(',');
        if (fields.Length < Column.Count)
        {
            return false;
        }

        var ticker = fields[Column.SymbolTicker].Trim().ToUpperInvariant();
        if (ticker.Length == 0)
        {
            return false;
        }

        var separator = ticker.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == ticker.Length - 1)
        {
            return false;
        }

        var venue = FyersMaps.ToCanonicalVenue(ticker[..separator]);
        if (venue.IsFailure)
        {
            return false;
        }

        if (!TryReadInt(fields[Column.InstrumentType], out var instrumentType))
        {
            return false;
        }

        var assetClass = FyersMaps.ToCanonicalAssetClass(instrumentType);
        if (assetClass.IsFailure)
        {
            return false;
        }

        var body = ticker[(separator + 1)..];
        var key = BuildKey(fields, venue.Value, assetClass.Value, body);
        if (key is not { } instrumentKey)
        {
            return false;
        }

        var definition = new InstrumentDefinition
        {
            Key = instrumentKey,
            Name = Blank(fields[Column.Name]) ?? body,

            // Every venue this connector reaches settles in rupees, and the manifest declares
            // INR and nothing else. Reading a currency from the file would be inventing one.
            Currency = Currency.Inr,
            Isin = Blank(fields[Column.Isin]),
            LotSize = TryReadDecimal(fields[Column.LotSize], out var lot) && lot > 0m ? lot : 1m,
            TickSize = TryReadDecimal(fields[Column.TickSize], out var tick) && tick > 0m ? tick : 0.01m,

            // FYERS reports the derivative contract size in the lot-size column and has no
            // separate multiplier, so the two are the same number for F&O and 1 for cash.
            Multiplier = instrumentKey.IsDerivative && lot > 0m ? lot : 1m,
        };

        record = new FyersInstrumentRecord(
            definition,
            ticker,
            fields[Column.FyToken].Trim());

        return true;
    }

    private static InstrumentKey? BuildKey(string[] fields, Venue venue, AssetClass assetClass, string body)
    {
        switch (assetClass)
        {
            case AssetClass.Index:
                // NOT the underlying-symbol column. For NSE:NIFTYBANK-INDEX that column reads
                // "BANKNIFTY" — the name its DERIVATIVES use — so taking it would produce a key
                // that no longer encodes back to the ticker it came from.
                return new InstrumentKey(
                    venue,
                    body.EndsWith(FyersSymbolTranslator.IndexSuffix, StringComparison.Ordinal)
                        ? body[..^FyersSymbolTranslator.IndexSuffix.Length]
                        : body,
                    AssetClass.Index);

            case AssetClass.Equity or AssetClass.Etf:
                {
                    // The settlement series is stripped so a scrip has one canonical symbol however
                    // it is held; see FyersSymbolTranslator for why.
                    var series = CashSeriesPattern().Match(body);
                    return new InstrumentKey(
                        venue,
                        series.Success ? body[..series.Index] : body,
                        assetClass);
                }

            case AssetClass.Future or AssetClass.Option:
                {
                    var underlying = Blank(fields[Column.UnderlyingSymbol]);
                    if (underlying is null)
                    {
                        return null;
                    }

                    if (!TryReadLong(fields[Column.ExpiryEpoch], out var epoch)
                        || FyersTime.FromEpoch(epoch) is not { } expiryInstant)
                    {
                        return null;
                    }

                    // The expiry epoch is the contract's settlement instant in IST. Converting it in
                    // UTC would roll a 14:30-UTC expiry back onto the previous day for every evening
                    // contract, and an option keyed to the wrong date is a different option.
                    var expiry = FyersTime.VenueDate(expiryInstant, FyersTime.India);

                    if (assetClass == AssetClass.Future)
                    {
                        return new InstrumentKey(
                            venue,
                            underlying.ToUpperInvariant(),
                            AssetClass.Future,
                            expiry);
                    }

                    if (!TryReadDecimal(fields[Column.Strike], out var strike) || strike < 0m)
                    {
                        return null;
                    }

                    var right = FyersMaps.ToCanonicalOptionRight(fields[Column.OptionType]);
                    if (right.IsFailure)
                    {
                        return null;
                    }

                    return new InstrumentKey(
                        venue,
                        underlying.ToUpperInvariant(),
                        AssetClass.Option,
                        expiry,
                        strike,
                        right.Value);
                }

            default:
                return null;
        }
    }

    private static string? Blank(string value)
    {
        var trimmed = value.Trim();

        // The master writes the literal string "None" for an absent value in several columns.
        return trimmed.Length == 0 || string.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    private static bool TryReadInt(string value, out int result) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryReadLong(string value, out long result) =>
        long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryReadDecimal(string value, out decimal result) =>
        decimal.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    [GeneratedRegex(@"-[A-Z]{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CashSeriesPattern();

    /// <summary>
    /// Column indices in the symbol-master CSV, in the order FYERS documents them.
    ///
    /// Named constants rather than magic numbers because the file has no header row to check
    /// them against: if the vendor inserts a column, the only thing that catches it is a human
    /// reading this list against the documentation, and that is far likelier to happen when the
    /// list exists.
    /// </summary>
    private static class Column
    {
        public const int FyToken = 0;
        public const int Name = 1;
        public const int InstrumentType = 2;
        public const int LotSize = 3;
        public const int TickSize = 4;
        public const int Isin = 5;
        public const int ExpiryEpoch = 8;
        public const int SymbolTicker = 9;
        public const int UnderlyingSymbol = 13;
        public const int Strike = 15;
        public const int OptionType = 16;

        /// <summary>Total columns FYERS documents. Three trailing ones are reserved and unread.</summary>
        public const int Count = 21;
    }
}

/// <summary>
/// The parsed symbol master, shared by reference, market data and orders.
///
/// One cache per connector instance, held by <see cref="FyersConnector"/> and passed by
/// reference to every facet that needs it. Reads vastly outnumber writes — the master is written
/// once a day and read on every order — so a reader-writer lock is the right shape here rather
/// than a lock or a concurrent dictionary per index.
/// </summary>
public sealed class FyersInstrumentCache : IFyersInstrumentLookup, IDisposable
{
    private readonly Dictionary<string, FyersInstrumentRecord> _byTicker =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<InstrumentKey, FyersInstrumentRecord> _byKey = [];

    private readonly Dictionary<string, FyersInstrumentRecord> _byToken =
        new(StringComparer.Ordinal);

    private readonly List<FyersInstrumentRecord> _all = [];

    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);

    private int _skippedRows;
    private bool _disposed;

    /// <summary>True once a symbol-master pass has completed with at least one file read.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>
    /// Rows the parser did not keep. Includes instrument types this connector does not trade, so
    /// a non-zero value is expected; a sudden change in it is what is worth an alert.
    /// </summary>
    public int SkippedRows => Volatile.Read(ref _skippedRows);

    /// <summary>Number of instruments held.</summary>
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

    /// <summary>Adds a batch. Batching keeps the write lock out of the per-row hot path.</summary>
    public void AddRange(IReadOnlyList<FyersInstrumentRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        _gate.EnterWriteLock();
        try
        {
            foreach (var record in records)
            {
                _all.Add(record);
                _byTicker[record.SymbolTicker] = record;
                _byKey[record.Definition.Key] = record;

                if (record.FyToken.Length > 0)
                {
                    _byToken[record.FyToken] = record;
                }
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>Marks the master as loaded, which switches the translator off its fallback path.</summary>
    public void MarkLoaded() => IsLoaded = true;

    /// <summary>Counts a row the parser did not keep.</summary>
    public void RecordSkippedRow() => Interlocked.Increment(ref _skippedRows);

    /// <inheritdoc />
    public bool TryGetByNative(string symbolTicker, out InstrumentKey key)
    {
        _gate.EnterReadLock();
        try
        {
            if (_byTicker.TryGetValue(symbolTicker, out var record))
            {
                key = record.Definition.Key;
                return true;
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        key = default;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetNative(InstrumentKey key, [NotNullWhen(true)] out string? symbolTicker)
    {
        _gate.EnterReadLock();
        try
        {
            if (_byKey.TryGetValue(key, out var record))
            {
                symbolTicker = record.SymbolTicker;
                return true;
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        symbolTicker = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetByToken(string fyToken, out InstrumentKey key)
    {
        _gate.EnterReadLock();
        try
        {
            if (_byToken.TryGetValue(fyToken, out var record))
            {
                key = record.Definition.Key;
                return true;
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        key = default;
        return false;
    }

    /// <summary>The full definition for a canonical key, when the master has been ingested.</summary>
    public bool TryGetDefinition(InstrumentKey key, [NotNullWhen(true)] out InstrumentDefinition? definition)
    {
        _gate.EnterReadLock();
        try
        {
            if (_byKey.TryGetValue(key, out var record))
            {
                definition = record.Definition;
                return true;
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        definition = null;
        return false;
    }

    /// <summary>
    /// Substring search over ticker and name, prefix matches first.
    ///
    /// A linear scan, which is the honest shape for a few hundred thousand records behind a
    /// search box: it costs a few milliseconds and needs no index to keep in step with a daily
    /// rebuild. The platform's own instrument master is what serves search at scale — see
    /// AddInstrumentMaster in the API's composition root — and this exists to fill it.
    /// </summary>
    public IReadOnlyList<InstrumentDefinition> Search(string query, int limit)
    {
        if (limit <= 0)
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
                if (record.Definition.Key.Symbol.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
                    || record.SymbolTicker.Contains($":{needle}", StringComparison.OrdinalIgnoreCase))
                {
                    prefix.Add(record.Definition);
                    if (prefix.Count >= limit)
                    {
                        return prefix;
                    }
                }
                else if (contains.Count < limit
                         && (record.SymbolTicker.Contains(needle, StringComparison.OrdinalIgnoreCase)
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

    /// <inheritdoc />
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
