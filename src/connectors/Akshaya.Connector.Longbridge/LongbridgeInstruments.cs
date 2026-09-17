using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Longbridge;

/// <summary>One security this connector can describe: its canonical definition, market and Longbridge symbol.</summary>
internal sealed record LongbridgeInstrumentRecord(InstrumentDefinition Definition, LongbridgeMarket Market, string Native);

/// <summary>
/// Longbridge symbology.
///
/// A Longbridge symbol is <c>CODE.REGION</c>: <c>AAPL.US</c>, <c>700.HK</c>, <c>D05.SG</c>. Two things separate
/// it from the canonical key. Hong Kong codes drop their leading zeros (<c>700.HK</c> is the canonical
/// <c>00700</c>, as every other connector here writes it), and a US symbol names no listing exchange, so
/// decoding one takes static information. A US option's symbol is OCC-shaped with the strike in thousandths:
/// <c>AAPL230317C150000.US</c> is the 17 March 2023 150 call.
/// </summary>
internal static partial class LongbridgeNative
{
    /// <summary>US option strikes are written in thousandths of a dollar.</summary>
    public const decimal OptionStrikeScale = 1000m;

    /// <summary>Shares per standard US equity option.</summary>
    public const decimal StandardOptionMultiplier = 100m;

    public static string Qualify(LongbridgeMarket market, string code) => $"{code}.{market.Suffix}";

    /// <summary>Splits on the LAST dot, so <c>BRK.B.US</c> is code <c>BRK.B</c> in the US.</summary>
    public static bool TrySplit(string native, [NotNullWhen(true)] out LongbridgeMarket? market, out string code)
    {
        market = null;
        code = string.Empty;

        var dot = native.LastIndexOf('.');
        if (dot <= 0 || dot == native.Length - 1)
        {
            return false;
        }

        var region = LongbridgeMaps.MarketForSuffix(native[(dot + 1)..]);
        if (region.IsFailure)
        {
            return false;
        }

        code = native[..dot].Trim().ToUpperInvariant();
        if (code.Length == 0)
        {
            return false;
        }

        market = region.Value;
        return true;
    }

    /// <summary>Canonical symbol to Longbridge's code: Hong Kong codes lose their leading zeros.</summary>
    public static string ToNativeCode(LongbridgeMarket market, string symbol)
    {
        var upper = symbol.Trim().ToUpperInvariant();
        if (market.Suffix != LongbridgeMarket.Hk.Suffix || upper.Length == 0 || !upper.All(char.IsAsciiDigit))
        {
            return upper;
        }

        var trimmed = upper.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    /// <summary>Longbridge's code to the canonical symbol: Hong Kong codes are five digits, zero-padded.</summary>
    public static string CanonicalSymbol(LongbridgeMarket market, string code)
    {
        var upper = code.Trim().ToUpperInvariant();
        return market.Suffix == LongbridgeMarket.Hk.Suffix && upper.Length is > 0 and < 5 && upper.All(char.IsAsciiDigit)
            ? upper.PadLeft(5, '0')
            : upper;
    }

    /// <summary>The market and Longbridge symbol for a cash key. Structural: no network.</summary>
    public static Result<(LongbridgeMarket Market, string Native)> ForCashKey(InstrumentKey key)
    {
        if (key.IsDerivative)
        {
            return Result<(LongbridgeMarket, string)>.Failure(LongbridgeErrors.InvalidRequest(
                $"{key} is a derivative; its Longbridge symbol comes from its contract, not its underlying."));
        }

        var market = LongbridgeMaps.MarketForVenue(key.Venue);
        if (market.IsFailure)
        {
            return Result<(LongbridgeMarket, string)>.Failure(market.Error);
        }

        if (string.IsNullOrWhiteSpace(key.Symbol))
        {
            return Result<(LongbridgeMarket, string)>.Failure(ConnectorErrors.InstrumentNotFound(key));
        }

        return Result<(LongbridgeMarket Market, string Native)>.Success(
            (market.Value, Qualify(market.Value, ToNativeCode(market.Value, key.Symbol))));
    }

    /// <summary>The Longbridge symbol for a US option key. Structural: the symbol carries the whole contract.</summary>
    public static Result<string> ForOptionKey(InstrumentKey key)
    {
        if (key is not { AssetClass: AssetClass.Option, Expiry: { } expiry, Strike: { } strike, Right: { } right })
        {
            return Result<string>.Failure(LongbridgeErrors.InvalidRequest(
                $"{key} is not a complete option key: an expiry, a strike and a right are all required."));
        }

        var market = LongbridgeMaps.MarketForVenue(key.Venue);
        if (market.IsFailure)
        {
            return Result<string>.Failure(market.Error);
        }

        if (market.Value.Suffix != LongbridgeMarket.Us.Suffix)
        {
            return Result<string>.Failure(ConnectorErrors.NotSupported(
                "options on non-US underlyings through this connector; Longbridge's Hong Kong option symbology is not mapped"));
        }

        var scaled = strike * OptionStrikeScale;
        if (scaled <= 0m || scaled != decimal.Truncate(scaled))
        {
            return Result<string>.Failure(ConnectorErrors.InstrumentNotFound(key));
        }

        var root = key.Symbol.Trim().ToUpperInvariant().Replace(".", string.Empty, StringComparison.Ordinal);
        var date = expiry.ToString("yyMMdd", CultureInfo.InvariantCulture);
        var side = right == OptionRight.Call ? "C" : "P";

        return $"{root}{date}{side}{scaled.ToString("0", CultureInfo.InvariantCulture)}.{LongbridgeMarket.Us.Suffix}";
    }

    public static bool TryParseOption(
        string native,
        [NotNullWhen(true)] out string? root,
        out DateOnly expiry,
        out OptionRight right,
        out decimal strike)
    {
        root = null;
        expiry = default;
        right = default;
        strike = 0m;

        var match = UsOption().Match(native);
        if (!match.Success
            || !DateOnly.TryParseExact(match.Groups["expiry"].Value, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out expiry)
            || !decimal.TryParse(match.Groups["strike"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var scaled)
            || scaled <= 0m)
        {
            return false;
        }

        root = match.Groups["root"].Value;
        right = match.Groups["right"].Value == "C" ? OptionRight.Call : OptionRight.Put;
        strike = scaled / OptionStrikeScale;
        return true;
    }

    public static Error NotFound(string native) => new(
        ConnectorErrorCodes.InstrumentNotFound,
        $"Longbridge returned no static information for '{native}'; it is not a security Longbridge recognises.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["native"] = native });

    [GeneratedRegex(@"^(?<root>[A-Z]+)(?<expiry>\d{6})(?<right>[CP])(?<strike>\d+)\.US$", RegexOptions.CultureInvariant)]
    private static partial Regex UsOption();
}

/// <summary>Static information to canonical definitions.</summary>
internal static class LongbridgeInstrumentFactory
{
    /// <summary>A cash security, or null when it is outside this connector's venues or asset classes.</summary>
    public static LongbridgeInstrumentRecord? Build(LbStaticInfo row)
    {
        if (!LongbridgeNative.TrySplit(row.Symbol.Trim().ToUpperInvariant(), out var market, out var code))
        {
            return null;
        }

        var assetClass = LongbridgeMaps.ToCanonicalAssetClass(row.Board);
        if (assetClass.IsFailure || assetClass.Value == AssetClass.Option)
        {
            return null;
        }

        Venue venue;
        if (market.Suffix == LongbridgeMarket.Us.Suffix)
        {
            // The listing exchange is what makes a US symbol decodable at all.
            var listed = LongbridgeMaps.ToCanonicalVenue(row.Exchange);
            if (listed.IsFailure)
            {
                return null;
            }

            venue = listed.Value;
        }
        else
        {
            venue = market.Suffix == LongbridgeMarket.Hk.Suffix ? Venue.Hkex : Venue.Sgx;
        }

        var currency = LongbridgeMaps.ToCanonicalCurrency(row.Currency) is { IsSuccess: true } listedIn
            ? listedIn.Value
            : market.Currency;

        return new LongbridgeInstrumentRecord(
            new InstrumentDefinition
            {
                Key = new InstrumentKey(venue, LongbridgeNative.CanonicalSymbol(market, code), assetClass.Value),
                Name = FirstNonEmpty(row.NameEn, row.NameHk, row.NameCn) ?? code,
                Currency = currency,

                // Hong Kong and Singapore trade in board lots, which the risk gate enforces through LotSize.
                LotSize = market.Suffix == LongbridgeMarket.Us.Suffix ? 1m : Math.Max(1, row.LotSize),
                TickSize = market.TickSize,
                Multiplier = 1m,
                SettlementDays = market.SettlementDays,
                IsTradable = assetClass.Value != AssetClass.Index,
            },
            market,
            LongbridgeNative.Qualify(market, code));
    }

    /// <summary>
    /// A US option contract. Its venue is its underlying's listing venue, the convention every connector here
    /// follows. The multiplier is the one Longbridge quoted when the contract came from a chain, else the
    /// standard 100: an adjusted contract seen only in a position or an order reports 100 until its chain loads.
    /// </summary>
    public static LongbridgeInstrumentRecord Option(
        string native,
        InstrumentKey underlying,
        Currency currency,
        DateOnly expiry,
        OptionRight right,
        decimal strike,
        decimal? multiplier) =>
        new(
            new InstrumentDefinition
            {
                Key = new InstrumentKey(underlying.Venue, underlying.Symbol, AssetClass.Option, expiry, strike, right),
                Name = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{underlying.Symbol} {expiry:yyyy-MM-dd} {strike} {(right == OptionRight.Call ? "Call" : "Put")}"),
                Currency = currency,
                LotSize = 1m,
                TickSize = 0.01m,
                Multiplier = multiplier is > 0m ? multiplier.Value : LongbridgeNative.StandardOptionMultiplier,
                SettlementDays = 1,
            },
            LongbridgeMarket.Us,
            native);

    private static string? FirstNonEmpty(params ReadOnlySpan<string?> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }
}

/// <summary>
/// Every security this connector instance has described, by Longbridge symbol and by canonical key. Shared
/// by reference across the facets, so an order placed on a security and the fill decoded for it can never
/// have been looked up twice and disagree.
/// </summary>
internal sealed class LongbridgeInstrumentCache
{
    private readonly ConcurrentDictionary<string, LongbridgeInstrumentRecord> _byNative = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<InstrumentKey, LongbridgeInstrumentRecord> _byKey = new();
    private readonly ConcurrentDictionary<string, byte> _outOfScope = new(StringComparer.OrdinalIgnoreCase);

    public void Add(LongbridgeInstrumentRecord record)
    {
        _byNative[record.Native] = record;
        _byKey[record.Definition.Key] = record;
        _outOfScope.TryRemove(record.Native, out _);
    }

    /// <summary>Longbridge knows the security, and it is outside the declared venues or asset classes.</summary>
    public void MarkOutOfScope(string native) => _outOfScope[native] = 0;

    public bool IsOutOfScope(string native) => _outOfScope.ContainsKey(native);

    public bool Knows(string native) => _byNative.ContainsKey(native) || _outOfScope.ContainsKey(native);

    public bool TryGetByNative(string native, [NotNullWhen(true)] out LongbridgeInstrumentRecord? record) =>
        _byNative.TryGetValue(native, out record);

    public bool TryGetByKey(InstrumentKey key, [NotNullWhen(true)] out LongbridgeInstrumentRecord? record) =>
        _byKey.TryGetValue(key, out record);

    /// <summary>Exact symbol first, then symbol prefix, then name; options are never search results.</summary>
    public IReadOnlyList<InstrumentDefinition> Search(string query, int limit)
    {
        var text = query.Trim();

        return
        [
            .. _byNative.Values
                .Where(r => r.Definition.Key.AssetClass != AssetClass.Option)
                .Select(r => (Record: r, Rank: Rank(r.Definition, text)))
                .Where(x => x.Rank < 3)
                .OrderBy(x => x.Rank)
                .ThenBy(x => x.Record.Definition.Key.Symbol, StringComparer.Ordinal)
                .Take(limit)
                .Select(x => x.Record.Definition),
        ];
    }

    private static int Rank(InstrumentDefinition definition, string text)
    {
        if (string.Equals(definition.Key.Symbol, text, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (definition.Key.Symbol.StartsWith(text, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return definition.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }
}

/// <summary>Fills the cache from the quote gateway, on demand: Longbridge publishes no security master.</summary>
internal sealed class LongbridgeInstrumentResolver(LongbridgeInstrumentCache cache, LongbridgeOptions options)
{
    /// <summary>
    /// Describes every symbol not already known. Cash securities come from static information, in batches;
    /// option contracts are built from their symbols once their underlyings are known. A symbol Longbridge
    /// returns nothing for stays unknown, so the caller reports it rather than silently dropping it.
    /// </summary>
    public async Task<Result> EnsureAsync(ILongbridgeQuoteRequester requester, IEnumerable<string> natives, CancellationToken ct)
    {
        var cash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contracts = new List<(string Native, string Root, DateOnly Expiry, OptionRight Right, decimal Strike)>();

        foreach (var raw in natives)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var native = raw.Trim().ToUpperInvariant();
            if (cache.Knows(native))
            {
                continue;
            }

            if (LongbridgeNative.TryParseOption(native, out var root, out var expiry, out var right, out var strike))
            {
                contracts.Add((native, root, expiry, right, strike));

                var underlying = LongbridgeNative.Qualify(LongbridgeMarket.Us, root);
                if (!cache.Knows(underlying))
                {
                    cash.Add(underlying);
                }
            }
            else if (LongbridgeNative.TrySplit(native, out _, out _))
            {
                cash.Add(native);
            }
            else
            {
                // A region this connector does not trade: known to be out of scope without asking.
                cache.MarkOutOfScope(native);
            }
        }

        foreach (var chunk in cash.Chunk(Math.Max(1, options.MaxSymbolsPerRequest)))
        {
            var response = await requester
                .RequestAsync(LongbridgeCommand.QuerySecurityStaticInfo, LbSecurityRequest.Many(chunk), ct)
                .ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result.Failure(response.Error);
            }

            try
            {
                Accept(LbStaticInfo.DecodeResponse(response.Value));
            }
            catch (InvalidDataException ex)
            {
                return Result.Failure(LongbridgeErrorMapper.MapException(ex, "QuerySecurityStaticInfo"));
            }
        }

        foreach (var contract in contracts)
        {
            var underlying = LongbridgeNative.Qualify(LongbridgeMarket.Us, contract.Root);

            if (cache.TryGetByNative(underlying, out var owner))
            {
                cache.Add(LongbridgeInstrumentFactory.Option(
                    contract.Native,
                    owner.Definition.Key,
                    owner.Definition.Currency,
                    contract.Expiry,
                    contract.Right,
                    contract.Strike,
                    multiplier: null));
            }
            else if (cache.IsOutOfScope(underlying))
            {
                cache.MarkOutOfScope(contract.Native);
            }
        }

        return Result.Success();
    }

    /// <summary>Caches every in-scope row and remembers the rest as out of scope.</summary>
    public IReadOnlyList<LongbridgeInstrumentRecord> Accept(IEnumerable<LbStaticInfo> rows)
    {
        var accepted = new List<LongbridgeInstrumentRecord>();

        foreach (var row in rows)
        {
            if (LongbridgeInstrumentFactory.Build(row) is { } record)
            {
                cache.Add(record);
                accepted.Add(record);
            }
            else if (!string.IsNullOrWhiteSpace(row.Symbol))
            {
                cache.MarkOutOfScope(row.Symbol.Trim().ToUpperInvariant());
            }
        }

        return accepted;
    }

    /// <summary>The record for a key, loading it if needed. The returned record's key may differ in venue; callers that need equality check.</summary>
    public async Task<Result<LongbridgeInstrumentRecord>> ForKeyAsync(
        ILongbridgeQuoteRequester requester,
        InstrumentKey key,
        CancellationToken ct)
    {
        if (cache.TryGetByKey(key, out var known))
        {
            return known;
        }

        string native;
        if (key.AssetClass == AssetClass.Option)
        {
            var option = LongbridgeNative.ForOptionKey(key);
            if (option.IsFailure)
            {
                return Result<LongbridgeInstrumentRecord>.Failure(option.Error);
            }

            native = option.Value;
        }
        else
        {
            var target = LongbridgeNative.ForCashKey(key);
            if (target.IsFailure)
            {
                return Result<LongbridgeInstrumentRecord>.Failure(target.Error);
            }

            native = target.Value.Native;
        }

        var ensured = await EnsureAsync(requester, [native], ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<LongbridgeInstrumentRecord>.Failure(ensured.Error);
        }

        if (cache.TryGetByNative(native, out var record))
        {
            return record;
        }

        return Result<LongbridgeInstrumentRecord>.Failure(cache.IsOutOfScope(native)
            ? ConnectorErrors.InstrumentNotFound(key)
            : LongbridgeNative.NotFound(native));
    }
}

/// <summary>
/// Canonical keys to Longbridge symbols and back. Encoding is structural; decoding reads the cache, because a
/// Longbridge symbol alone names neither a US listing venue nor an asset class, and a guessed key is an order
/// on the wrong instrument.
/// </summary>
internal sealed class LongbridgeSymbolTranslator(LongbridgeInstrumentCache cache) : ISymbolTranslator
{
    public Result<string> ToNative(InstrumentKey key)
    {
        if (key.AssetClass == AssetClass.Option)
        {
            return LongbridgeNative.ForOptionKey(key);
        }

        var cash = LongbridgeNative.ForCashKey(key);
        return cash.IsFailure ? Result<string>.Failure(cash.Error) : cash.Value.Native;
    }

    public Result<InstrumentKey> ToCanonical(string nativeSymbol, string? nativeExchange = null)
    {
        var native = nativeSymbol?.Trim().ToUpperInvariant() ?? string.Empty;

        return cache.TryGetByNative(native, out var record)
            ? record.Definition.Key
            : Result<InstrumentKey>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                $"'{native}' has not been resolved through Longbridge reference data. A Longbridge symbol names neither "
                + "a US listing venue nor an asset class, so it cannot be decoded without it.",
                VendorCode: null,
                VendorMessage: null,
                Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["native"] = native }));
    }
}
