using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Tiger;

/// <summary>One security this connector can describe: its canonical definition, market and Tiger symbol.</summary>
internal sealed record TigerInstrumentRecord(InstrumentDefinition Definition, TigerMarket Market, string Symbol, string? Identifier)
{
    /// <summary>The key this record is cached under: market and symbol, or an option's identifier.</summary>
    public string Native => Identifier ?? TigerNative.Qualify(Market, Symbol);
}

/// <summary>
/// Tiger symbology.
///
/// A stock is a symbol and a market: <c>AAPL</c> in US, <c>00700</c> in HK, <c>D05</c> in SG — the same spelling the
/// canonical key uses, which is why there is no translation table here. An option is an OCC identifier: the
/// underlying padded to six characters, the expiry as <c>yyMMdd</c>, <c>C</c> or <c>P</c>, and the strike in
/// thousandths over eight digits — <c>AAPL  260116C00150000</c>.
/// </summary>
internal static class TigerNative
{
    public const decimal StrikeScale = 1000m;

    public const decimal StandardOptionMultiplier = 100m;

    public static string Qualify(TigerMarket market, string symbol) => $"{market.Code}:{symbol.Trim().ToUpperInvariant()}";

    public static Result<(TigerMarket Market, string Symbol)> ForCashKey(InstrumentKey key)
    {
        if (key.IsDerivative)
        {
            return Result<(TigerMarket, string)>.Failure(TigerErrors.InvalidRequest(
                $"{key} is a derivative; its Tiger contract is named by its own fields, not by its underlying."));
        }

        var market = TigerMaps.MarketForVenue(key.Venue);
        if (market.IsFailure)
        {
            return Result<(TigerMarket, string)>.Failure(market.Error);
        }

        return string.IsNullOrWhiteSpace(key.Symbol)
            ? Result<(TigerMarket, string)>.Failure(ConnectorErrors.InstrumentNotFound(key))
            : Result<(TigerMarket Market, string Symbol)>.Success((market.Value, key.Symbol.Trim().ToUpperInvariant()));
    }

    /// <summary>The OCC identifier for an option key.</summary>
    public static Result<string> ForOptionKey(InstrumentKey key)
    {
        if (key is not { AssetClass: AssetClass.Option, Expiry: { } expiry, Strike: { } strike, Right: { } right })
        {
            return Result<string>.Failure(TigerErrors.InvalidRequest(
                $"{key} is not a complete option key: an expiry, a strike and a right are all required."));
        }

        var market = TigerMaps.MarketForVenue(key.Venue);
        if (market.IsFailure)
        {
            return Result<string>.Failure(market.Error);
        }

        var scaled = strike * StrikeScale;
        if (scaled <= 0m || scaled != decimal.Truncate(scaled))
        {
            return Result<string>.Failure(ConnectorErrors.InstrumentNotFound(key));
        }

        return Identifier(key.Symbol, expiry, right, strike);
    }

    public static string Identifier(string underlying, DateOnly expiry, OptionRight right, decimal strike)
    {
        var builder = new StringBuilder(underlying.Trim().ToUpperInvariant().PadRight(6, ' '));
        builder.Append(expiry.ToString("yyMMdd", CultureInfo.InvariantCulture));
        builder.Append(right == OptionRight.Call ? 'C' : 'P');
        builder.Append(((long)(strike * StrikeScale)).ToString("D8", CultureInfo.InvariantCulture));

        return builder.ToString();
    }

    public static Error NotFound(string native) => new(
        ConnectorErrorCodes.InstrumentNotFound,
        $"Tiger returned no contract for '{native}'.",
        VendorCode: null,
        VendorMessage: null,
        Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["native"] = native });
}

/// <summary>
/// Every contract described so far, by Tiger symbol and by canonical key.
///
/// Shared across the process: a Tiger contract's listing, currency and lot are reference data, the same for every
/// account, and the host builds a connector per request.
/// </summary>
internal sealed class TigerInstrumentCache
{
    public static readonly TigerInstrumentCache Shared = new();

    private readonly ConcurrentDictionary<string, TigerInstrumentRecord> _byNative = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<InstrumentKey, TigerInstrumentRecord> _byKey = new();
    private readonly ConcurrentDictionary<string, byte> _outOfScope = new(StringComparer.OrdinalIgnoreCase);

    public void Add(TigerInstrumentRecord record)
    {
        _byNative[record.Native] = record;
        _byKey[record.Definition.Key] = record;
        _outOfScope.TryRemove(record.Native, out _);
    }

    public void MarkOutOfScope(string native) => _outOfScope[native] = 0;

    public bool IsOutOfScope(string native) => _outOfScope.ContainsKey(native);

    public bool TryGetByNative(string native, [NotNullWhen(true)] out TigerInstrumentRecord? record) =>
        _byNative.TryGetValue(native, out record);

    public bool TryGetByKey(InstrumentKey key, [NotNullWhen(true)] out TigerInstrumentRecord? record) =>
        _byKey.TryGetValue(key, out record);

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

/// <summary>
/// Contracts to canonical definitions.
///
/// Tiger repeats the contract on every row it returns — an order, a position, an execution all carry the symbol,
/// market, currency and, for an option, expiry, strike, right and multiplier. So describing a row usually costs
/// nothing. The exception is a US stock's LISTING VENUE, which rows do not carry: that is looked up once per symbol
/// through the contract method and cached for the process.
/// </summary>
internal sealed class TigerInstrumentResolver(TigerInstrumentCache cache, TigerOptions options)
{
    /// <summary>An Etf key matches the Equity definition on the same venue and symbol: Tiger has no ETF security type.</summary>
    public static bool Matches(InstrumentKey requested, InstrumentKey resolved) =>
        requested == resolved
        || (requested.AssetClass == AssetClass.Etf
            && resolved.AssetClass == AssetClass.Equity
            && requested.Venue == resolved.Venue
            && string.Equals(requested.Symbol, resolved.Symbol, StringComparison.OrdinalIgnoreCase));

    /// <summary>Describes the contract on a row, looking up only what the row does not carry.</summary>
    public async Task<Result<TigerInstrumentRecord>> ForContractAsync(
        TigerApi api,
        TigerContractFields fields,
        TigerCredentials credentials,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fields.Symbol))
        {
            return Result<TigerInstrumentRecord>.Failure(TigerErrors.MissingField("contract", "symbol"));
        }

        var market = TigerMaps.MarketForCode(fields.Market);
        if (market.IsFailure)
        {
            return Result<TigerInstrumentRecord>.Failure(market.Error);
        }

        var assetClass = TigerMaps.ToCanonicalAssetClass(fields.SecType);
        if (assetClass.IsFailure)
        {
            return Result<TigerInstrumentRecord>.Failure(assetClass.Error);
        }

        var symbol = fields.Symbol.Trim().ToUpperInvariant();

        if (assetClass.Value == AssetClass.Option)
        {
            return await OptionAsync(api, fields, market.Value, symbol, credentials, ct).ConfigureAwait(false);
        }

        var native = TigerNative.Qualify(market.Value, symbol);
        if (cache.TryGetByNative(native, out var known))
        {
            return known;
        }

        var venue = TigerMaps.ToCanonicalVenue(market.Value, fields.PrimaryExchange, fields.Exchange);
        if (venue.IsFailure)
        {
            // A US row carries no listing venue; the contract method knows it.
            var looked = await LookupAsync(api, symbol, market.Value, credentials, ct).ConfigureAwait(false);
            if (looked.IsFailure)
            {
                return looked;
            }

            return looked.Value;
        }

        var record = Build(fields, market.Value, symbol, venue.Value, assetClass.Value);
        cache.Add(record);
        return record;
    }

    /// <summary>The record for a canonical key, looking the contract up when it is not known.</summary>
    public async Task<Result<TigerInstrumentRecord>> ForKeyAsync(
        TigerApi api,
        InstrumentKey key,
        TigerCredentials credentials,
        CancellationToken ct)
    {
        if (cache.TryGetByKey(key, out var known))
        {
            return known;
        }

        if (key.AssetClass == AssetClass.Etf && cache.TryGetByKey(key with { AssetClass = AssetClass.Equity }, out var asStock))
        {
            return asStock;
        }

        if (key.AssetClass == AssetClass.Option)
        {
            if (key is not { Expiry: { } expiry, Strike: { } strike, Right: { } right })
            {
                return Result<TigerInstrumentRecord>.Failure(TigerErrors.InvalidRequest($"{key} is not a complete option key."));
            }

            var underlying = await ForKeyAsync(api, new InstrumentKey(key.Venue, key.Symbol, AssetClass.Equity), credentials, ct)
                .ConfigureAwait(false);

            if (underlying.IsFailure)
            {
                return underlying;
            }

            var contract = new TigerContractFields
            {
                Symbol = key.Symbol,
                SecType = TigerMaps.SecurityTypeOption,
                Market = underlying.Value.Market.Code,
                Currency = underlying.Value.Definition.Currency.ToString(),
                Expiry = TigerTime.ExpiryStamp(expiry),
                Strike = TigerNumber.Wire(strike),
                Right = right == OptionRight.Call ? "CALL" : "PUT",
                Identifier = TigerNative.Identifier(key.Symbol, expiry, right, strike),
            };

            return await OptionAsync(api, contract, underlying.Value.Market, key.Symbol.Trim().ToUpperInvariant(), credentials, ct)
                .ConfigureAwait(false);
        }

        var cash = TigerNative.ForCashKey(key);
        if (cash.IsFailure)
        {
            return Result<TigerInstrumentRecord>.Failure(cash.Error);
        }

        var record = await LookupAsync(api, cash.Value.Symbol, cash.Value.Market, credentials, ct).ConfigureAwait(false);
        if (record.IsFailure)
        {
            return record;
        }

        return Matches(key, record.Value.Definition.Key)
            ? record
            : Result<TigerInstrumentRecord>.Failure(ConnectorErrors.InstrumentNotFound(key));
    }

    /// <summary>Asks Tiger to describe one stock, which is the only way to learn a US listing venue.</summary>
    private async Task<Result<TigerInstrumentRecord>> LookupAsync(
        TigerApi api,
        string symbol,
        TigerMarket market,
        TigerCredentials credentials,
        CancellationToken ct)
    {
        var native = TigerNative.Qualify(market, symbol);
        if (cache.TryGetByNative(native, out var known))
        {
            return known;
        }

        if (cache.IsOutOfScope(native))
        {
            return Result<TigerInstrumentRecord>.Failure(TigerNative.NotFound(native));
        }

        var biz = TigerBiz.New()
            .Add("account", credentials.Account)
            .Add("symbol", symbol)
            .Add("sec_type", TigerMaps.SecurityTypeStock)
            .Add("lang", options.Language);

        var response = await api.CallAsync<TigerContract>(TigerMaps.MethodContract, biz, credentials, isTradeWrite: false, ct)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result<TigerInstrumentRecord>.Failure(response.Error);
        }

        var fields = response.Value.Fields();
        var contractMarket = TigerMaps.MarketForCode(fields.Market ?? market.Code);
        if (contractMarket.IsFailure)
        {
            cache.MarkOutOfScope(native);
            return Result<TigerInstrumentRecord>.Failure(contractMarket.Error);
        }

        var venue = TigerMaps.ToCanonicalVenue(contractMarket.Value, fields.PrimaryExchange, fields.Exchange);
        if (venue.IsFailure)
        {
            cache.MarkOutOfScope(native);
            return Result<TigerInstrumentRecord>.Failure(venue.Error);
        }

        var record = Build(fields, contractMarket.Value, symbol, venue.Value, AssetClass.Equity);
        cache.Add(record);
        return record;
    }

    /// <summary>
    /// An option contract. Everything but the venue is on the row; the venue is the underlying's, which is looked up
    /// once and then cached, exactly as every other connector here does it.
    /// </summary>
    private async Task<Result<TigerInstrumentRecord>> OptionAsync(
        TigerApi api,
        TigerContractFields fields,
        TigerMarket market,
        string symbol,
        TigerCredentials credentials,
        CancellationToken ct)
    {
        var expiry = TigerTime.ParseExpiry(fields.Expiry);
        var strike = TigerNumber.Decimal(fields.Strike);
        var right = ParseRight(fields.Right);

        if (expiry is null || strike is not > 0m || right is null)
        {
            return Result<TigerInstrumentRecord>.Failure(TigerErrors.MissingField("contract", "expiry/strike/right"));
        }

        var identifier = string.IsNullOrWhiteSpace(fields.Identifier)
            ? TigerNative.Identifier(symbol, expiry.Value, right.Value, strike.Value)
            : fields.Identifier.Trim();

        if (cache.TryGetByNative(identifier, out var known))
        {
            return known;
        }

        var underlying = await LookupAsync(api, symbol, market, credentials, ct).ConfigureAwait(false);
        if (underlying.IsFailure)
        {
            return underlying;
        }

        var currency = TigerMaps.ToCanonicalCurrency(fields.Currency) is { IsSuccess: true } listed
            ? listed.Value
            : underlying.Value.Definition.Currency;

        var multiplier = TigerNumber.Decimal(fields.Multiplier) is { } m && m > 0m ? m : TigerNative.StandardOptionMultiplier;

        var record = new TigerInstrumentRecord(
            new InstrumentDefinition
            {
                Key = new InstrumentKey(
                    underlying.Value.Definition.Key.Venue,
                    underlying.Value.Definition.Key.Symbol,
                    AssetClass.Option,
                    expiry.Value,
                    strike.Value,
                    right.Value),
                Name = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{underlying.Value.Definition.Key.Symbol} {expiry.Value:yyyy-MM-dd} {strike.Value} {right.Value}"),
                Currency = currency,
                LotSize = 1m,
                TickSize = 0.01m,
                Multiplier = multiplier,
                SettlementDays = 1,
            },
            market,
            symbol,
            identifier);

        cache.Add(record);
        return record;
    }

    private static TigerInstrumentRecord Build(
        TigerContractFields fields,
        TigerMarket market,
        string symbol,
        Venue venue,
        AssetClass assetClass)
    {
        var currency = TigerMaps.ToCanonicalCurrency(fields.Currency) is { IsSuccess: true } listed ? listed.Value : market.Currency;
        var lotSize = TigerNumber.Decimal(fields.LotSize) is { } lot && lot > 0m ? lot : 1m;
        var tickSize = TigerNumber.Decimal(fields.MinTick) is { } tick && tick > 0m ? tick : market.TickSize;

        return new TigerInstrumentRecord(
            new InstrumentDefinition
            {
                Key = new InstrumentKey(venue, symbol, assetClass),
                Name = string.IsNullOrWhiteSpace(fields.Name) ? symbol : fields.Name.Trim(),
                Currency = currency,
                LotSize = lotSize,
                TickSize = tickSize,
                Multiplier = 1m,
                SettlementDays = market.SettlementDays,
            },
            market,
            symbol,
            Identifier: null);
    }

    private static OptionRight? ParseRight(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "C" or "CALL" => OptionRight.Call,
        "P" or "PUT" => OptionRight.Put,
        _ => null,
    };
}

/// <summary>Canonical keys to Tiger symbols and identifiers, and back through the cache.</summary>
internal sealed class TigerSymbolTranslator(TigerInstrumentCache cache) : ISymbolTranslator
{
    public Result<string> ToNative(InstrumentKey key)
    {
        if (key.AssetClass == AssetClass.Option)
        {
            return TigerNative.ForOptionKey(key);
        }

        var cash = TigerNative.ForCashKey(key);
        return cash.IsFailure ? Result<string>.Failure(cash.Error) : cash.Value.Symbol;
    }

    public Result<InstrumentKey> ToCanonical(string nativeSymbol, string? nativeExchange = null)
    {
        var native = nativeSymbol?.Trim() ?? string.Empty;

        if (cache.TryGetByNative(native, out var byIdentifier))
        {
            return byIdentifier.Definition.Key;
        }

        foreach (var market in TigerMarket.All)
        {
            if ((nativeExchange is null || string.Equals(nativeExchange.Trim(), market.Code, StringComparison.OrdinalIgnoreCase))
                && cache.TryGetByNative(TigerNative.Qualify(market, native), out var record))
            {
                return record.Definition.Key;
            }
        }

        return Result<InstrumentKey>.Failure(new Error(
            ConnectorErrorCodes.InstrumentNotFound,
            $"'{native}' has not been described through Tiger reference data, so its listing venue is not known.",
            VendorCode: null,
            VendorMessage: null,
            Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["native"] = native }));
    }
}
