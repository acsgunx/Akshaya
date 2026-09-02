using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.MStock;

/// <summary>
/// The instrument master.
///
/// mStock publishes it as one CSV covering every tradable contract on NSE and BSE — well over
/// two hundred thousand rows once the weekly option chains are in it, and several times that
/// on an expiry week. It is streamed and parsed row by row: buffering it produces a
/// large-object-heap allocation measured in hundreds of megabytes, and doing that on an API
/// process that is also routing orders is not acceptable.
///
/// Parsing is by COLUMN NAME, never by position. mStock has added columns mid-file-format
/// before, and a positional parser silently reads the wrong column when that happens — which
/// means silently wrong strikes and expiries, which means orders on the wrong contract.
/// </summary>
public sealed class MStockReference : IConnectorReference
{
    private readonly MStockApi _api;
    private readonly MStockOptions _options;
    private readonly MStockInstrumentCache _cache;

    /// <summary>Creates the reference-data facet.</summary>
    internal MStockReference(MStockApi api, MStockOptions options, MStockInstrumentCache cache)
    {
        _api = api;
        _options = options;
        _cache = cache;
    }

    /// <inheritdoc />
    /// <remarks>
    /// This is also what populates <see cref="MStockInstrumentCache"/>, which the symbol
    /// translator, the chart routes and the streaming socket all depend on. Enumerating with a
    /// filter still caches every row seen, because the next caller's filter will be different.
    ///
    /// The contract's signature has no failure channel — you cannot return a
    /// <c>Result</c> from an <see cref="IAsyncEnumerable{T}"/> — so a download failure throws
    /// <see cref="MStockReferenceException"/>. That is the single place in this connector that
    /// throws for a broker failure, and it does so only because yielding an empty sequence
    /// would present "mStock is down" as "mStock lists no instruments", which would wipe the
    /// platform's instrument master on the next ingest.
    /// </remarks>
    public async IAsyncEnumerable<InstrumentDefinition> GetInstrumentsAsync(
        Venue? venue = null,
        AssetClass? assetClass = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var download = await _api
            .GetRawStreamAsync(_options.ScriptMasterPath, query: null, ct)
            .ConfigureAwait(false);

        if (download.IsFailure)
        {
            throw new MStockReferenceException(download.Error);
        }

        await using var stream = download.Value;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var headerLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (headerLine is null)
        {
            throw new MStockReferenceException(new Error(
                ConnectorErrorCodes.BrokerUnavailable,
                "mStock's script master came back empty."));
        }

        var header = MStockCsv.BuildHeader(headerLine);
        var batch = new List<MStockInstrumentRecord>(capacity: 4096);

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (!MStockCsv.TryParseRow(line, header, out var record))
            {
                // A row we cannot parse is skipped rather than fatal. One malformed line in
                // two hundred thousand must not abort the nightly ingest; the parse failures
                // are counted so the ingest job can alarm if the rate is abnormal.
                _cache.RecordSkippedRow();
                continue;
            }

            batch.Add(record);
            if (batch.Count == batch.Capacity)
            {
                _cache.AddRange(batch);
                batch.Clear();
            }

            if (venue is { } wantedVenue && record.Definition.Key.Venue != wantedVenue)
            {
                continue;
            }

            if (assetClass is { } wantedClass && record.Definition.Key.AssetClass != wantedClass)
            {
                continue;
            }

            yield return record.Definition;
        }

        if (batch.Count > 0)
        {
            _cache.AddRange(batch);
        }

        _cache.MarkLoaded();
    }

    /// <inheritdoc />
    public async Task<Result<InstrumentDefinition>> ResolveAsync(
        InstrumentKey key,
        CancellationToken ct = default)
    {
        var ensured = await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<InstrumentDefinition>.Failure(ensured.Error);
        }

        return _cache.TryGetDefinition(key, out var definition)
            ? definition
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

        var ensured = await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<IReadOnlyList<InstrumentDefinition>>.Failure(ensured.Error);
        }

        return Result<IReadOnlyList<InstrumentDefinition>>.Success(_cache.Search(query, limit));
    }

    /// <summary>
    /// Loads the master once per connector if nobody has already. The download is expensive
    /// enough that a resolve which triggers it is worth doing exactly once.
    /// </summary>
    private async Task<Result> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_cache.IsLoaded)
        {
            return Result.Success();
        }

        try
        {
            await foreach (var _ in GetInstrumentsAsync(ct: ct).ConfigureAwait(false))
            {
                // Enumerated purely for the side effect of filling the cache; the rows
                // themselves are held there, not here.
            }
        }
        catch (MStockReferenceException ex)
        {
            return Result.Failure(ex.Error);
        }

        return Result.Success();
    }
}

/// <summary>
/// Thrown when the script master cannot be downloaded or read.
///
/// It exists because <see cref="IConnectorReference.GetInstrumentsAsync"/> returns a bare
/// <see cref="IAsyncEnumerable{T}"/> and therefore has nowhere to put a
/// <see cref="Result"/> failure. Callers that need the canonical error read
/// <see cref="Error"/>; <see cref="MStockReference.ResolveAsync"/> and
/// <see cref="MStockReference.SearchAsync"/> already convert it back into a Result.
/// </summary>
public sealed class MStockReferenceException : Exception
{
    /// <summary>Creates the exception from a canonical error.</summary>
    public MStockReferenceException(Error error)
        : base(error.ToString()) => Error = error;

    /// <summary>Creates the exception with a message only.</summary>
    public MStockReferenceException(string message)
        : base(message) => Error = new Error(ConnectorErrorCodes.Unknown, message);

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public MStockReferenceException(string message, Exception innerException)
        : base(message, innerException) => Error = new Error(ConnectorErrorCodes.Unknown, message);

    /// <summary>Creates an empty exception. Present to satisfy the exception design guidelines.</summary>
    public MStockReferenceException()
        : this("The mStock script master could not be read.")
    {
    }

    /// <summary>The canonical error this exception carries.</summary>
    public Error Error { get; }
}

/// <summary>
/// One parsed script-master row: the canonical definition plus the native identifiers the rest
/// of the connector addresses mStock with.
/// </summary>
public sealed record MStockInstrumentRecord(
    InstrumentDefinition Definition,
    string TradingSymbol,
    string Exchange,
    uint InstrumentToken);

/// <summary>
/// The in-memory instrument master.
///
/// Shared by every facet of one connector instance: the symbol translator resolves monthly
/// expiries through it, the chart routes need its numeric tokens, and the streaming socket
/// receives ticks that carry a token and nothing else. It is fed by
/// <see cref="MStockReference.GetInstrumentsAsync"/>.
///
/// Thread-safe by construction — the ingest writes while quote and order paths read.
/// </summary>
public sealed class MStockInstrumentCache : IMStockInstrumentLookup, IDisposable
{
    private readonly Dictionary<string, MStockInstrumentRecord> _byNative =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<InstrumentKey, MStockInstrumentRecord> _byKey = [];

    private readonly Dictionary<uint, MStockInstrumentRecord> _byToken = [];

    private readonly List<MStockInstrumentRecord> _all = [];

    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);

    private int _skippedRows;

    /// <summary>True once a full script-master pass has completed.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>How many rows the parser could not read. Non-zero is worth an alert.</summary>
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
    public void AddRange(IReadOnlyList<MStockInstrumentRecord> records)
    {
        _gate.EnterWriteLock();
        try
        {
            foreach (var record in records)
            {
                _all.Add(record);
                _byNative[NativeKey(record.TradingSymbol, record.Exchange)] = record;
                _byKey[record.Definition.Key] = record;

                if (record.InstrumentToken != 0)
                {
                    _byToken[record.InstrumentToken] = record;
                }
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>Marks the master as fully loaded.</summary>
    public void MarkLoaded() => IsLoaded = true;

    /// <summary>Counts a row the parser could not read.</summary>
    public void RecordSkippedRow() => Interlocked.Increment(ref _skippedRows);

    /// <inheritdoc />
    public bool TryGetByNative(string tradingSymbol, string? exchange, out InstrumentKey key)
    {
        _gate.EnterReadLock();
        try
        {
            if (exchange is not null
                && _byNative.TryGetValue(NativeKey(tradingSymbol, exchange), out var exact))
            {
                key = exact.Definition.Key;
                return true;
            }

            // With no exchange hint, a unique match across segments is still a safe answer;
            // an ambiguous one is not, and is reported as a miss rather than a coin toss.
            MStockInstrumentRecord? single = null;
            foreach (var candidate in _all)
            {
                if (!string.Equals(candidate.TradingSymbol, tradingSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (single is not null)
                {
                    key = default;
                    return false;
                }

                single = candidate;
            }

            if (single is not null)
            {
                key = single.Definition.Key;
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
    public bool TryGetNative(InstrumentKey key, out string tradingSymbol, out string exchange)
    {
        _gate.EnterReadLock();
        try
        {
            if (_byKey.TryGetValue(key, out var record))
            {
                tradingSymbol = record.TradingSymbol;
                exchange = record.Exchange;
                return true;
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        tradingSymbol = string.Empty;
        exchange = string.Empty;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetToken(InstrumentKey key, out uint instrumentToken)
    {
        _gate.EnterReadLock();
        try
        {
            if (_byKey.TryGetValue(key, out var record) && record.InstrumentToken != 0)
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

    /// <summary>Looks up the full definition for a canonical key.</summary>
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
    /// Substring search over symbol and name, exact-prefix matches first. Good enough for a
    /// search box; the platform's own instrument index does the clever ranking.
    /// </summary>
    public IReadOnlyList<InstrumentDefinition> Search(string query, int limit)
    {
        var needle = query.Trim();
        if (needle.Length == 0 || limit <= 0)
        {
            return [];
        }

        var prefix = new List<InstrumentDefinition>();
        var contains = new List<InstrumentDefinition>();

        _gate.EnterReadLock();
        try
        {
            foreach (var record in _all)
            {
                var symbol = record.Definition.Key.Symbol;
                var name = record.Definition.Name;

                if (symbol.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
                    || record.TradingSymbol.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                {
                    prefix.Add(record.Definition);
                }
                else if (symbol.Contains(needle, StringComparison.OrdinalIgnoreCase)
                         || name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    contains.Add(record.Definition);
                }

                if (prefix.Count >= limit)
                {
                    break;
                }
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        if (prefix.Count >= limit)
        {
            return prefix.GetRange(0, limit);
        }

        prefix.AddRange(contains.Take(limit - prefix.Count));
        return prefix;
    }

    private static string NativeKey(string tradingSymbol, string exchange) =>
        $"{exchange.ToUpperInvariant()}:{tradingSymbol.ToUpperInvariant()}";

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}

/// <summary>
/// Script-master CSV parsing. Column lookup is by name; rows are RFC 4180 quoted, because
/// company names contain commas ("MAHINDRA &amp; MAHINDRA FIN. SERV. LTD, NCD").
/// </summary>
internal static class MStockCsv
{
    /// <summary>Builds a column-name to index map from the header line.</summary>
    public static Dictionary<string, int> BuildHeader(string headerLine)
    {
        var columns = SplitLine(headerLine);
        var header = new Dictionary<string, int>(columns.Count, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < columns.Count; i++)
        {
            var name = columns[i].Trim().Replace(" ", "_", StringComparison.Ordinal);
            if (name.Length > 0)
            {
                header[name] = i;
            }
        }

        return header;
    }

    /// <summary>
    /// Parses one row. Returns false rather than throwing: the caller counts the failures and
    /// carries on, because one bad line must not abort a two-hundred-thousand-row ingest.
    /// </summary>
    public static bool TryParseRow(
        string line,
        IReadOnlyDictionary<string, int> header,
        [NotNullWhen(true)] out MStockInstrumentRecord? record)
    {
        record = null;

        var fields = SplitLine(line);

        var tradingSymbol = Field(fields, header, "tradingsymbol", "trading_symbol", "symbol");
        var exchange = Field(fields, header, "exchange", "exch_seg", "segment_exchange");
        var instrumentType = Field(fields, header, "instrument_type", "instrumenttype", "series");

        if (string.IsNullOrWhiteSpace(tradingSymbol) || string.IsNullOrWhiteSpace(exchange))
        {
            return false;
        }

        var segment = Field(fields, header, "segment");
        var venue = MStockMaps.ToCanonicalVenue(exchange);
        if (venue.IsFailure)
        {
            return false;
        }

        var assetClass = MStockMaps.ToCanonicalAssetClass(instrumentType ?? string.Empty, segment);
        if (assetClass.IsFailure)
        {
            return false;
        }

        var name = Field(fields, header, "name", "company_name") ?? tradingSymbol;
        var expiry = ParseDate(Field(fields, header, "expiry", "expiry_date"));
        var strike = ParseDecimal(Field(fields, header, "strike", "strike_price"));

        OptionRight? right = null;
        if (assetClass.Value == AssetClass.Option)
        {
            var parsedRight = MStockMaps.ToCanonicalOptionRight(instrumentType ?? string.Empty);
            if (parsedRight.IsSuccess)
            {
                right = parsedRight.Value;
            }
            else if (tradingSymbol.EndsWith("CE", StringComparison.OrdinalIgnoreCase))
            {
                right = OptionRight.Call;
            }
            else if (tradingSymbol.EndsWith("PE", StringComparison.OrdinalIgnoreCase))
            {
                right = OptionRight.Put;
            }
            else
            {
                return false;
            }
        }

        // For derivatives the canonical symbol is the UNDERLYING (the master's `name`
        // column), not the contract's trading symbol: NIFTY, not NIFTY25SEP25000CE. The
        // contract's identity is carried by the expiry, strike and right fields, which is what
        // makes an option chain expressible as a set of keys sharing a symbol.
        var isDerivative = assetClass.Value is AssetClass.Future or AssetClass.Option;
        var canonicalSymbol = isDerivative
            ? (Field(fields, header, "name") ?? tradingSymbol).Trim().ToUpperInvariant()
            : StripSeries(tradingSymbol, exchange);

        if (isDerivative && expiry is null)
        {
            return false;
        }

        var key = new InstrumentKey(
            venue.Value,
            canonicalSymbol,
            assetClass.Value,
            expiry,
            assetClass.Value == AssetClass.Option ? strike : null,
            right);

        var lotSize = ParseDecimal(Field(fields, header, "lot_size", "lotsize")) ?? 1m;
        var tickSize = ParseDecimal(Field(fields, header, "tick_size", "ticksize")) ?? 0.05m;

        var definition = new InstrumentDefinition
        {
            Key = key,
            Name = name.Trim(),
            Currency = Currency.Inr,
            Isin = Field(fields, header, "isin"),
            LotSize = lotSize <= 0m ? 1m : lotSize,
            TickSize = tickSize <= 0m ? 0.05m : tickSize,

            // In Indian F&O the lot size IS the contract multiplier — one NIFTY contract is
            // seventy-five index units — so they carry the same number rather than one being
            // silently left at 1 and the notional coming out seventy-five times too small.
            Multiplier = isDerivative ? (lotSize <= 0m ? 1m : lotSize) : 1m,
            TradingHoursId = segment ?? exchange.ToUpperInvariant(),

            // Indian cash equity settles T+1; derivatives cash-settle or physically settle on
            // expiry and carry no rolling settlement cycle.
            SettlementDays = isDerivative ? 0 : 1,
            IsTradable = true,
        };

        record = new MStockInstrumentRecord(
            definition,
            tradingSymbol.Trim().ToUpperInvariant(),
            exchange.Trim().ToUpperInvariant(),
            ParseToken(Field(fields, header, "instrument_token", "token", "exchange_token")));

        return true;
    }

    /// <summary>
    /// NSE cash symbols carry a series suffix that is not part of the instrument's identity.
    /// BSE ones do not, so stripping unconditionally would corrupt any BSE scrip that happens
    /// to end in "-EQ".
    /// </summary>
    private static string StripSeries(string tradingSymbol, string exchange)
    {
        var symbol = tradingSymbol.Trim().ToUpperInvariant();
        var isNse = exchange.Trim().StartsWith("NSE", StringComparison.OrdinalIgnoreCase);

        return isNse && symbol.EndsWith(MStockSymbolTranslator.NseCashSuffix, StringComparison.Ordinal)
            ? symbol[..^MStockSymbolTranslator.NseCashSuffix.Length]
            : symbol;
    }

    private static string? Field(
        IReadOnlyList<string> fields,
        IReadOnlyDictionary<string, int> header,
        params ReadOnlySpan<string> names)
    {
        foreach (var name in names)
        {
            if (header.TryGetValue(name, out var index) && index < fields.Count)
            {
                var value = fields[index].Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // The master has shipped all three of these formats at various times.
        string[] formats = ["yyyy-MM-dd", "dd-MMM-yyyy", "dd/MM/yyyy", "yyyy-MM-ddTHH:mm:ss"];

        if (DateOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        return MStockTime.Parse(value) is { } parsed ? DateOnly.FromDateTime(parsed.DateTime) : null;
    }

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static uint ParseToken(string? value) =>
        uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0u;

    /// <summary>RFC 4180 field splitting, including doubled quotes inside a quoted field.</summary>
    internal static List<string> SplitLine(string line)
    {
        var fields = new List<string>(16);
        var builder = new StringBuilder(32);
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        builder.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    builder.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(builder.ToString());
                    builder.Clear();
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        fields.Add(builder.ToString());
        return fields;
    }
}
