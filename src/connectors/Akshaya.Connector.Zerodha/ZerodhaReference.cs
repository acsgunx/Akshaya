using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// One row of the Kite instrument master, parsed.
/// </summary>
/// <param name="Definition">The canonical instrument, as the platform will hold it.</param>
/// <param name="QualifiedSymbol">The exchange-qualified Kite symbol — "NSE:INFY".</param>
/// <param name="InstrumentToken">
/// Kite's numeric identifier. The socket and the historical-candle route accept nothing else,
/// which is why the master is a hard requirement for both rather than a nicety.
/// </param>
public sealed record ZerodhaInstrumentRecord(
    InstrumentDefinition Definition,
    string QualifiedSymbol,
    uint InstrumentToken);

/// <summary>
/// Instrument reference data from the Kite instrument master.
///
/// The master is a CSV dump per exchange segment. This connector reads the four covering its
/// declared venues — NSE, BSE, NFO and BFO — rather than the consolidated <c>/instruments</c>
/// dump, which also carries MCX, currency and mutual-fund rows the connector has no venue for.
///
/// Two things about this ingest differ from the other Indian connectors here:
///
/// * The files are GZIPPED, and whether they arrive that way depends on how the HttpClient was
///   configured — a caller-supplied one may have decompression turned off. The stream is sniffed
///   for the gzip magic bytes rather than assumed either way, because feeding a CSV parser a
///   binary blob produces a hundred thousand skipped rows and no error.
/// * Kite ships a HEADER ROW and QUOTES its name column, so columns are located by NAME from the
///   header rather than by position. A vendor inserting a column then costs nothing instead of
///   silently shifting every price by one field.
/// </summary>
public sealed class ZerodhaReference : IConnectorReference
{
    /// <summary>
    /// The exchange segments this connector reads, in the order it reads them.
    ///
    /// Cash first, on purpose. It is by far the smaller half and it is what a user searches for in
    /// the first thirty seconds after linking an account, so a partially completed ingest is still
    /// immediately useful rather than being a list of option contracts.
    /// </summary>
    private static readonly string[] Segments =
    [
        ZerodhaMaps.ExchangeNse,
        ZerodhaMaps.ExchangeBse,
        ZerodhaMaps.ExchangeNfo,
        ZerodhaMaps.ExchangeBfo,
    ];

    private readonly ZerodhaApi _api;
    private readonly ZerodhaOptions _options;
    private readonly SharedInstrumentMaster<ZerodhaInstrumentCache> _master;
    private readonly IClock _clock;

    internal ZerodhaReference(
        ZerodhaApi api,
        ZerodhaOptions options,
        SharedInstrumentMaster<ZerodhaInstrumentCache> master,
        IClock clock)
    {
        _api = api;
        _options = options;
        _master = master;
        _clock = clock;
    }

    private ZerodhaInstrumentCache Cache => _master.Cache;

    /// <inheritdoc />
    /// <remarks>
    /// Served from the process-wide cache, which is loaded first if this process has no fresh copy,
    /// so the platform's search index and this connector's own token lookups share one download.
    /// A master that cannot be loaded at all throws <see cref="ZerodhaReferenceException"/>: the
    /// contract has no failure channel, and an empty sequence would read as "Kite lists nothing".
    /// </remarks>
    public async IAsyncEnumerable<InstrumentDefinition> GetInstrumentsAsync(
        Venue? venue = null,
        AssetClass? assetClass = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var loaded = await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            throw new ZerodhaReferenceException(loaded.Error);
        }

        foreach (var definition in Filter(Cache.Snapshot(), venue, assetClass))
        {
            yield return definition;
        }
    }

    /// <inheritdoc />
    public async Task<Result<InstrumentDefinition>> ResolveAsync(InstrumentKey key, CancellationToken ct = default)
    {
        var loaded = await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<InstrumentDefinition>.Failure(loaded.Error);
        }

        return Cache.TryGetDefinition(key, out var definition)
            ? Result<InstrumentDefinition>.Success(definition)
            : Result<InstrumentDefinition>.Failure(ConnectorErrors.InstrumentNotFound(key));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<InstrumentDefinition>>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Success([]);
        }

        var loaded = await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Failure(loaded.Error);
        }

        return Result<IReadOnlyList<InstrumentDefinition>>.Success(Cache.Search(query, limit));
    }

    /// <summary>
    /// Loads the master into the process-wide cache unless a fresh copy is already there. The
    /// history route, the option chain and the socket call this before looking up a token, so the
    /// first chart after a restart waits for the download instead of failing.
    /// </summary>
    internal Task<Result> EnsureLoadedAsync(CancellationToken ct) =>
        _master.EnsureLoadedAsync(DownloadAsync, _clock, ct);

    /// <summary>
    /// Downloads and parses every segment, then swaps the result into the cache in one step.
    /// </summary>
    private async Task<Result> DownloadAsync(CancellationToken ct)
    {
        var records = new List<ZerodhaInstrumentRecord>(capacity: 65_536);
        var skipped = 0;
        Error? firstFailure = null;
        var anyFileRead = false;

        foreach (var segment in Segments)
        {
            ct.ThrowIfCancellationRequested();

            var path = string.Format(CultureInfo.InvariantCulture, _options.InstrumentsPathFormat, segment);
            var stream = await _api.GetRawStreamAsync(path, ct).ConfigureAwait(false);
            if (stream.IsFailure)
            {
                // One unavailable segment must not abandon the load. A trader with the cash master
                // loaded can trade equities; refusing to load anything because the BFO dump timed
                // out would take that away for no reason. The FIRST failure is kept, because when
                // every segment fails it is the reason worth showing — an unregistered IP, say.
                firstFailure ??= stream.Error;
                continue;
            }

            anyFileRead = true;

            await using var raw = stream.Value;
            await using var body = await WrapIfCompressedAsync(raw, ct).ConfigureAwait(false);
            using var reader = new StreamReader(body);

            var header = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            var columns = MasterColumns.FromHeader(header);
            if (columns is null)
            {
                // A file whose header we cannot read is a file whose columns we would be guessing
                // at. Skipping it loudly beats importing a hundred thousand mis-parsed rows.
                skipped++;
                continue;
            }

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!TryParseRow(line, columns, out var record))
                {
                    // Rows for instrument types this connector does not trade land here alongside
                    // genuinely broken ones. Both are counted; a sudden jump in the count is the
                    // signal that the file's shape changed.
                    skipped++;
                    continue;
                }

                records.Add(record);
            }
        }

        // Only claim the master is loaded if something actually arrived. Marking it loaded after
        // four failed downloads would leave the socket and the history route with nothing to look
        // a token up in, and nothing would retry for twelve hours.
        if (!anyFileRead || records.Count == 0)
        {
            return Result.Failure(firstFailure ?? new Error(
                ConnectorErrorCodes.BrokerUnavailable,
                "Kite's instrument list could not be read. Try again in a few minutes."));
        }

        Cache.Replace(records, skipped);
        return Result.Success();
    }

    /// <summary>
    /// Wraps the response in a <see cref="GZipStream"/> when it is actually gzipped.
    ///
    /// Kite serves the master compressed. Whether it arrives that way depends on the HttpClient's
    /// <c>AutomaticDecompression</c> setting, which a caller-supplied client controls and this
    /// connector does not. Sniffing the two magic bytes handles both cases and costs one buffered
    /// read; assuming either one produces a silent, total failure in the other case.
    /// </summary>
    private static async Task<Stream> WrapIfCompressedAsync(Stream source, CancellationToken ct)
    {
        var head = new byte[2];
        var read = await source.ReadAtLeastAsync(head, 2, throwOnEndOfStream: false, ct).ConfigureAwait(false);

        // Put the bytes we consumed back in front of the rest, so whichever reader comes next
        // sees the whole stream.
        var replayed = new ConcatenatedStream(head.AsMemory(0, read).ToArray(), source);

        return read == 2 && head[0] == 0x1F && head[1] == 0x8B
            ? new GZipStream(replayed, CompressionMode.Decompress)
            : replayed;
    }

    private static IEnumerable<InstrumentDefinition> Filter(
        IReadOnlyList<ZerodhaInstrumentRecord> batch,
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
    /// Parses one master row against the column map read from the header.
    ///
    /// Returns false rather than throwing for anything unreadable. A single malformed row must
    /// cost one instrument, not the whole master.
    /// </summary>
    internal static bool TryParseRow(
        string line,
        MasterColumns columns,
        [NotNullWhen(true)] out ZerodhaInstrumentRecord? record)
    {
        record = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var fields = ZerodhaCsv.SplitLine(line);

        var tradingSymbol = columns.Read(fields, MasterColumns.TradingSymbol);
        var exchange = columns.Read(fields, MasterColumns.Exchange);
        if (string.IsNullOrWhiteSpace(tradingSymbol) || string.IsNullOrWhiteSpace(exchange))
        {
            return false;
        }

        var venue = ZerodhaMaps.ToCanonicalVenue(exchange);
        if (venue.IsFailure)
        {
            return false;
        }

        var assetClass = ZerodhaMaps.ToCanonicalAssetClass(
            columns.Read(fields, MasterColumns.InstrumentType),
            columns.Read(fields, MasterColumns.Segment));

        if (assetClass.IsFailure)
        {
            return false;
        }

        if (!uint.TryParse(
                columns.Read(fields, MasterColumns.InstrumentToken),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var token))
        {
            // Without a token the instrument can be ordered but neither streamed nor charted, and
            // half an instrument in the cache is worse than none: it would satisfy a lookup that
            // then fails downstream for a reason nothing here explains.
            return false;
        }

        var key = BuildKey(fields, columns, venue.Value, assetClass.Value, tradingSymbol.ToUpperInvariant());
        if (key is not { } instrumentKey)
        {
            return false;
        }

        var lotSize = ReadDecimal(columns.Read(fields, MasterColumns.LotSize));
        var tickSize = ReadDecimal(columns.Read(fields, MasterColumns.TickSize));

        var definition = new InstrumentDefinition
        {
            Key = instrumentKey,

            // Kite leaves `name` empty on many derivative rows and fills it with the underlying on
            // others; the trading symbol is always there and is what a user recognises.
            Name = Blank(columns.Read(fields, MasterColumns.Name)) ?? tradingSymbol,

            // Every venue this connector reaches settles in rupees, and the manifest declares INR
            // and nothing else. Reading a currency from the file would be inventing one.
            Currency = Currency.Inr,

            // Kite's master carries no ISIN except on holdings. Left null rather than guessed; the
            // portfolio facet fills it in from the holdings response, which does have it.
            Isin = null,

            LotSize = lotSize > 0m ? lotSize : 1m,
            TickSize = tickSize > 0m ? tickSize : 0.01m,

            // Kite reports the derivative contract size in the lot-size column and has no separate
            // multiplier, so the two are the same number for F&O and 1 for cash.
            Multiplier = instrumentKey.IsDerivative && lotSize > 0m ? lotSize : 1m,

            // Index rows are quoted but not tradable. Saying so keeps them searchable — a user
            // charting NIFTY 50 needs to find it — while telling the order ticket not to offer it.
            IsTradable = instrumentKey.AssetClass != AssetClass.Index,
        };

        record = new ZerodhaInstrumentRecord(
            definition,
            ZerodhaInstrument.Qualify(exchange.ToUpperInvariant(), tradingSymbol.ToUpperInvariant()),
            token);

        return true;
    }

    private static InstrumentKey? BuildKey(
        string[] fields,
        MasterColumns columns,
        Venue venue,
        AssetClass assetClass,
        string tradingSymbol)
    {
        switch (assetClass)
        {
            // Cash and indices carry the trading symbol verbatim; there is nothing to decode.
            case AssetClass.Equity or AssetClass.Etf or AssetClass.Index:
                return new InstrumentKey(venue, tradingSymbol, assetClass);

            case AssetClass.Future or AssetClass.Option:
                {
                    if (ZerodhaTime.ParseDate(columns.Read(fields, MasterColumns.Expiry)) is not { } expiry)
                    {
                        return null;
                    }

                    // The `name` column carries the UNDERLYING on derivative rows ("NIFTY" for
                    // NIFTY26SEP23900CE), which is exactly the canonical symbol. Deriving it from the
                    // trading symbol instead would mean re-implementing the expiry regex here.
                    var underlying = Blank(columns.Read(fields, MasterColumns.Name))?.ToUpperInvariant();
                    if (underlying is null)
                    {
                        return null;
                    }

                    if (assetClass == AssetClass.Future)
                    {
                        return new InstrumentKey(venue, underlying, AssetClass.Future, expiry);
                    }

                    var strike = ReadDecimal(columns.Read(fields, MasterColumns.Strike));
                    if (strike <= 0m)
                    {
                        return null;
                    }

                    var right = ZerodhaMaps.ToCanonicalOptionRight(
                        columns.Read(fields, MasterColumns.InstrumentType));

                    return right.IsFailure
                        ? null
                        : new InstrumentKey(venue, underlying, AssetClass.Option, expiry, strike, right.Value);
                }

            default:
                return null;
        }
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal ReadDecimal(string? value) =>
        decimal.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;
}

/// <summary>
/// The instrument master's columns, located by NAME from its header row.
///
/// Kite ships a header, so there is no reason to hard-code positions — and every reason not to.
/// A vendor inserting a column ahead of <c>tick_size</c> would, with positional parsing, silently
/// turn every lot size into a tick size and every instrument into a subtly wrong one. With this,
/// it is a no-op.
/// </summary>
public sealed class MasterColumns
{
    public const string InstrumentToken = "instrument_token";
    public const string TradingSymbol = "tradingsymbol";
    public const string Name = "name";
    public const string Expiry = "expiry";
    public const string Strike = "strike";
    public const string TickSize = "tick_size";
    public const string LotSize = "lot_size";
    public const string InstrumentType = "instrument_type";
    public const string Segment = "segment";
    public const string Exchange = "exchange";

    /// <summary>Columns without which a row cannot be understood at all.</summary>
    private static readonly string[] Required =
        [InstrumentToken, TradingSymbol, InstrumentType, Segment, Exchange];

    private readonly Dictionary<string, int> _indices;

    private MasterColumns(Dictionary<string, int> indices)
    {
        _indices = indices;
    }

    /// <summary>Builds the column map, or null when the header is missing or unusable.</summary>
    public static MasterColumns? FromHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var fields = ZerodhaCsv.SplitLine(header);
        var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < fields.Length; i++)
        {
            indices[fields[i].Trim()] = i;
        }

        foreach (var column in Required)
        {
            if (!indices.ContainsKey(column))
            {
                return null;
            }
        }

        return new MasterColumns(indices);
    }

    /// <summary>The named column's value, or null when the row is short or the column absent.</summary>
    public string? Read(string[] fields, string column) =>
        _indices.TryGetValue(column, out var index) && index < fields.Length ? fields[index] : null;
}

/// <summary>
/// A stream that replays a prefix already read from an underlying stream, then continues from it.
///
/// Needed because detecting gzip means consuming the first two bytes, and neither
/// <see cref="GZipStream"/> nor a <see cref="StreamReader"/> can be handed a stream that is two
/// bytes in. Read-only and forward-only, which is all a CSV ingest needs.
/// </summary>
internal sealed class ConcatenatedStream(byte[] prefix, Stream rest) : Stream
{
    private int _prefixPosition;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_prefixPosition < prefix.Length)
        {
            var take = Math.Min(buffer.Length, prefix.Length - _prefixPosition);
            prefix.AsSpan(_prefixPosition, take).CopyTo(buffer);
            _prefixPosition += take;
            return take;
        }

        return rest.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_prefixPosition < prefix.Length)
        {
            var take = Math.Min(buffer.Length, prefix.Length - _prefixPosition);
            prefix.AsMemory(_prefixPosition, take).CopyTo(buffer);
            _prefixPosition += take;
            return take;
        }

        return await rest.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override void Flush()
    {
        // Read-only.
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            rest.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await rest.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Thrown when the instrument master cannot be loaded at all.
///
/// It exists because <see cref="IConnectorReference.GetInstrumentsAsync"/> returns a bare
/// <see cref="IAsyncEnumerable{T}"/> with nowhere to put a <see cref="Result"/> failure, and an
/// empty sequence would read as "Kite lists no instruments". Callers that need the canonical error
/// read <see cref="Error"/>.
/// </summary>
public sealed class ZerodhaReferenceException : Exception
{
    /// <summary>Creates the exception from a canonical error.</summary>
    public ZerodhaReferenceException(Error error)
        : base(error.ToString()) => Error = error;

    /// <summary>Creates the exception with a message only.</summary>
    public ZerodhaReferenceException(string message)
        : base(message) => Error = new Error(ConnectorErrorCodes.Unknown, message);

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public ZerodhaReferenceException(string message, Exception innerException)
        : base(message, innerException) => Error = new Error(ConnectorErrorCodes.Unknown, message);

    /// <summary>Creates an empty exception. Present to satisfy the exception design guidelines.</summary>
    public ZerodhaReferenceException()
        : this("Kite's instrument list could not be read.")
    {
    }

    /// <summary>The canonical error this exception carries.</summary>
    public Error Error { get; }
}

/// <summary>
/// The parsed instrument master, shared by reference, market data, orders, portfolio and the
/// stream.
///
/// One cache per PROCESS and endpoint, held through <see cref="SharedInstrumentMaster{TCache}"/>
/// and passed to every facet that needs it. Connectors are built per request, so a cache owned by
/// one instance was empty on every request. Reads vastly outnumber writes — the master is written
/// once a day and read on every order and every tick — so a reader-writer lock is the right shape
/// here rather than a lock or a concurrent dictionary per index.
/// </summary>
public sealed class ZerodhaInstrumentCache : IZerodhaInstrumentLookup, IDisposable
{
    // Not readonly: Replace swaps in a whole new generation under the write lock.
    private Dictionary<string, ZerodhaInstrumentRecord> _bySymbol =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<InstrumentKey, ZerodhaInstrumentRecord> _byKey = [];

    private Dictionary<uint, ZerodhaInstrumentRecord> _byToken = [];

    private List<ZerodhaInstrumentRecord> _all = [];

    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);

    private int _skippedRows;
    private bool _disposed;

    /// <summary>True once a master pass has completed with at least one segment read.</summary>
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
    public void AddRange(IReadOnlyList<ZerodhaInstrumentRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        _gate.EnterWriteLock();
        try
        {
            foreach (var record in records)
            {
                Index(record, _all, _bySymbol, _byKey, _byToken);
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>
    /// Replaces the whole master with a freshly parsed one and marks it loaded.
    ///
    /// REPLACE, never append: the cache is process-wide and reloaded twice a day, and appending
    /// would double every row by the afternoon and keep expired contracts forever. The new
    /// generation is built outside the lock and published in one swap, so a reader never sees a
    /// half-built index.
    /// </summary>
    public void Replace(IReadOnlyList<ZerodhaInstrumentRecord> records, int skippedRows)
    {
        ArgumentNullException.ThrowIfNull(records);

        var all = new List<ZerodhaInstrumentRecord>(records.Count);
        var bySymbol = new Dictionary<string, ZerodhaInstrumentRecord>(records.Count, StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<InstrumentKey, ZerodhaInstrumentRecord>(records.Count);
        var byToken = new Dictionary<uint, ZerodhaInstrumentRecord>(records.Count);

        foreach (var record in records)
        {
            Index(record, all, bySymbol, byKey, byToken);
        }

        _gate.EnterWriteLock();
        try
        {
            _all = all;
            _bySymbol = bySymbol;
            _byKey = byKey;
            _byToken = byToken;
            Volatile.Write(ref _skippedRows, skippedRows);
            IsLoaded = true;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>A point-in-time copy of every row, safe to enumerate while a reload runs.</summary>
    public IReadOnlyList<ZerodhaInstrumentRecord> Snapshot()
    {
        _gate.EnterReadLock();
        try
        {
            return _all.ToArray();
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    private static void Index(
        ZerodhaInstrumentRecord record,
        List<ZerodhaInstrumentRecord> all,
        Dictionary<string, ZerodhaInstrumentRecord> bySymbol,
        Dictionary<InstrumentKey, ZerodhaInstrumentRecord> byKey,
        Dictionary<uint, ZerodhaInstrumentRecord> byToken)
    {
        all.Add(record);
        bySymbol[record.QualifiedSymbol] = record;
        byKey[record.Definition.Key] = record;
        byToken[record.InstrumentToken] = record;
    }

    /// <summary>Marks the master as loaded, which switches the translator off its fallback path.</summary>
    public void MarkLoaded() => IsLoaded = true;

    /// <summary>Counts a row the parser did not keep.</summary>
    public void RecordSkippedRow() => Interlocked.Increment(ref _skippedRows);

    /// <inheritdoc />
    public bool TryGetByNative(string qualifiedSymbol, out InstrumentKey key)
    {
        _gate.EnterReadLock();
        try
        {
            if (_bySymbol.TryGetValue(qualifiedSymbol, out var record))
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
    public bool TryGetNative(InstrumentKey key, [NotNullWhen(true)] out string? qualifiedSymbol)
    {
        _gate.EnterReadLock();
        try
        {
            if (_byKey.TryGetValue(key, out var record))
            {
                qualifiedSymbol = record.QualifiedSymbol;
                return true;
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        qualifiedSymbol = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetToken(InstrumentKey key, out uint instrumentToken)
    {
        _gate.EnterReadLock();
        try
        {
            if (_byKey.TryGetValue(key, out var record))
            {
                instrumentToken = record.InstrumentToken;
                return true;
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        instrumentToken = 0;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetByToken(uint instrumentToken, out InstrumentKey key)
    {
        _gate.EnterReadLock();
        try
        {
            if (_byToken.TryGetValue(instrumentToken, out var record))
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
    /// Every option contract on one underlying expiring on one date, strike order.
    ///
    /// This is what lets the market-data facet assemble an option chain: Kite publishes no chain
    /// endpoint, but it publishes every contract in the master, and a chain is exactly "the
    /// contracts, plus their quotes".
    /// </summary>
    public IReadOnlyList<ZerodhaInstrumentRecord> OptionsFor(InstrumentKey underlying, DateOnly expiry)
    {
        var matches = new List<ZerodhaInstrumentRecord>();

        _gate.EnterReadLock();
        try
        {
            foreach (var record in _all)
            {
                var key = record.Definition.Key;

                if (key.AssetClass == AssetClass.Option
                    && key.Expiry == expiry
                    && key.Venue == underlying.Venue
                    && string.Equals(key.Symbol, underlying.Symbol, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(record);
                }
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        matches.Sort(static (left, right) =>
            (left.Definition.Key.Strike ?? 0m).CompareTo(right.Definition.Key.Strike ?? 0m));

        return matches;
    }

    /// <summary>
    /// Substring search over symbol and name, prefix matches first.
    ///
    /// A linear scan, which is the honest shape for a few hundred thousand records behind a search
    /// box: it costs a few milliseconds and needs no index to keep in step with a daily rebuild.
    /// The platform's own instrument master is what serves search at scale — see
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
                if (record.Definition.Key.Symbol.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                {
                    prefix.Add(record.Definition);
                    if (prefix.Count >= limit)
                    {
                        return prefix;
                    }
                }
                else if (contains.Count < limit
                         && (record.QualifiedSymbol.Contains(needle, StringComparison.OrdinalIgnoreCase)
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
