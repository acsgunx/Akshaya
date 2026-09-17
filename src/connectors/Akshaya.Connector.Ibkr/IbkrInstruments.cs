using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Akshaya.Connectors.Abstractions;
using Akshaya.Connectors.Sdk;
using Akshaya.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Akshaya.Connector.Ibkr;

/// <summary>One contract this connector can describe: its canonical definition, market and IBKR conid.</summary>
internal sealed record IbkrInstrumentRecord(InstrumentDefinition Definition, IbkrMarket Market, long Conid);

/// <summary>
/// Every contract described so far, by conid and by canonical key.
///
/// SHARED ACROSS THE PROCESS. A conid is IBKR's global identifier for a contract — the same number for every user,
/// account and gateway — and a contract's listing, currency and multiplier are reference data, not account data.
/// The host builds a connector per request, so a per-connector cache would look the same contract up on every
/// request; one cache for the process looks it up once.
/// </summary>
internal sealed class IbkrInstrumentCache
{
    public static readonly IbkrInstrumentCache Shared = new();

    private readonly ConcurrentDictionary<long, IbkrInstrumentRecord> _byConid = new();
    private readonly ConcurrentDictionary<InstrumentKey, IbkrInstrumentRecord> _byKey = new();
    private readonly ConcurrentDictionary<long, byte> _outOfScope = new();

    public void Add(IbkrInstrumentRecord record)
    {
        _byConid[record.Conid] = record;
        _byKey[record.Definition.Key] = record;
        _outOfScope.TryRemove(record.Conid, out _);
    }

    /// <summary>IBKR knows the contract, and it is outside the declared venues or asset classes.</summary>
    public void MarkOutOfScope(long conid) => _outOfScope[conid] = 0;

    public bool IsOutOfScope(long conid) => _outOfScope.ContainsKey(conid);

    public bool Knows(long conid) => _byConid.ContainsKey(conid) || _outOfScope.ContainsKey(conid);

    public bool TryGetByConid(long conid, [NotNullWhen(true)] out IbkrInstrumentRecord? record) =>
        _byConid.TryGetValue(conid, out record);

    public bool TryGetByKey(InstrumentKey key, [NotNullWhen(true)] out IbkrInstrumentRecord? record) =>
        _byKey.TryGetValue(key, out record);

    /// <summary>Exact symbol first, then symbol prefix, then name; options are never search results.</summary>
    public IReadOnlyList<InstrumentDefinition> Search(string query, int limit)
    {
        var text = query.Trim();

        return
        [
            .. _byConid.Values
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
/// Conids to canonical definitions and canonical keys to conids, through the gateway.
///
/// Decoding a conid reads <c>/trsrv/secdef</c>, which carries the listing exchange, currency and — for an option —
/// expiry, strike, right, multiplier and underlying conid. An option's venue is its underlying's listing venue, the
/// convention every connector here follows. Hong Kong and Singapore trade in board lots, which only the contract
/// rules route reports.
///
/// Encoding a stock key reads <c>/trsrv/stocks</c>, which lists a symbol's contracts by exchange, and takes the one
/// on the key's listing exchange; an option key is found through <c>/iserver/secdef/info</c> by month, strike and
/// right. A key whose contract IBKR lists on a different venue is not found, never substituted.
/// </summary>
internal sealed class IbkrInstrumentResolver(IbkrInstrumentCache cache, IbkrOptions options, ILogger logger)
{
    /// <summary>
    /// Whether a resolved contract is the one a key asked for. IBKR has no ETF security type — an ETF is a stock — so
    /// an Etf key matches the Equity definition on the same venue and symbol.
    /// </summary>
    public static bool Matches(InstrumentKey requested, InstrumentKey resolved) =>
        requested == resolved
        || (requested.AssetClass == AssetClass.Etf
            && resolved.AssetClass == AssetClass.Equity
            && requested.Venue == resolved.Venue
            && string.Equals(requested.Symbol, resolved.Symbol, StringComparison.OrdinalIgnoreCase));

    /// <summary>Describes every conid not already known. A conid IBKR returns nothing for stays unknown, so callers report it.</summary>
    public async Task<Result> EnsureConidsAsync(IbkrChannel channel, IEnumerable<long> conids, CancellationToken ct)
    {
        var wanted = conids.Where(c => c > 0 && !cache.Knows(c)).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return Result.Success();
        }

        var api = channel.Api();
        if (api.IsFailure)
        {
            return Result.Failure(api.Error);
        }

        var rows = await SecdefAsync(api.Value, wanted, ct).ConfigureAwait(false);
        if (rows.IsFailure)
        {
            return Result.Failure(rows.Error);
        }

        var described = rows.Value;
        var returned = described.Select(r => IbkrNumber.Integer(r.Conid) ?? 0).ToHashSet();

        var underlyings = described
            .Where(IsOption)
            .Select(r => IbkrNumber.Integer(r.UnderlyingConid) ?? 0)
            .Where(c => c > 0 && !cache.Knows(c) && !returned.Contains(c))
            .Distinct()
            .ToList();

        if (underlyings.Count > 0)
        {
            var more = await SecdefAsync(api.Value, underlyings, ct).ConfigureAwait(false);
            if (more.IsFailure)
            {
                return Result.Failure(more.Error);
            }

            described.AddRange(more.Value);
        }

        // Stocks first, so every option finds its underlying.
        foreach (var row in described.Where(r => !IsOption(r)))
        {
            await AddStockAsync(channel, row, ct).ConfigureAwait(false);
        }

        foreach (var row in described.Where(IsOption))
        {
            AddOption(row);
        }

        return Result.Success();
    }

    public async Task<Result<IbkrInstrumentRecord>> ForKeyAsync(IbkrChannel channel, InstrumentKey key, CancellationToken ct)
    {
        if (cache.TryGetByKey(key, out var known))
        {
            return known;
        }

        if (key.AssetClass == AssetClass.Etf && cache.TryGetByKey(key with { AssetClass = AssetClass.Equity }, out var asStock))
        {
            return asStock;
        }

        return key.AssetClass == AssetClass.Option
            ? await OptionAsync(channel, key, ct).ConfigureAwait(false)
            : await StockAsync(channel, key, ct).ConfigureAwait(false);
    }

    /// <summary>Option contracts from <c>/iserver/secdef/info</c>, which answers an array for options and an object otherwise.</summary>
    internal static List<IbkrSecdefInfo> ReadContracts(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.Deserialize<List<IbkrSecdefInfo>>(IbkrJson.Options) ?? [],
        JsonValueKind.Object when element.TryGetProperty("conid", out _) =>
            element.Deserialize<IbkrSecdefInfo>(IbkrJson.Options) is { } single ? [single] : [],
        _ => [],
    };

    private async Task<Result<IbkrInstrumentRecord>> StockAsync(IbkrChannel channel, InstrumentKey key, CancellationToken ct)
    {
        if (key.AssetClass is not (AssetClass.Equity or AssetClass.Etf))
        {
            return Result<IbkrInstrumentRecord>.Failure(ConnectorErrors.NotSupported($"{key.AssetClass} contracts through this connector"));
        }

        var market = IbkrMaps.MarketForVenue(key.Venue);
        if (market.IsFailure)
        {
            return Result<IbkrInstrumentRecord>.Failure(market.Error);
        }

        var exchange = IbkrMaps.ListingExchangeFor(key.Venue);
        if (exchange.IsFailure)
        {
            return Result<IbkrInstrumentRecord>.Failure(exchange.Error);
        }

        var api = channel.Api();
        if (api.IsFailure)
        {
            return Result<IbkrInstrumentRecord>.Failure(api.Error);
        }

        var symbol = IbkrMaps.ToNativeSymbol(market.Value, key.Symbol);
        var listings = await api.Value
            .GetAsync<Dictionary<string, List<IbkrStockEntry>>>(HttpConnectorPath.WithQuery("trsrv/stocks", ("symbols", symbol)), ct)
            .ConfigureAwait(false);

        if (listings.IsFailure)
        {
            return Result<IbkrInstrumentRecord>.Failure(listings.Error);
        }

        var conid = listings.Value
            .Where(pair => string.Equals(pair.Key, symbol, StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value ?? [])
            .Where(entry => string.Equals(entry.AssetClass, IbkrMaps.SecTypeStock, StringComparison.OrdinalIgnoreCase))
            .SelectMany(entry => entry.Contracts ?? [])
            .Where(contract => string.Equals(contract.Exchange?.Trim(), exchange.Value, StringComparison.OrdinalIgnoreCase))
            .Select(contract => IbkrNumber.Integer(contract.Conid))
            .FirstOrDefault(found => found is > 0);

        if (conid is not { } resolved)
        {
            return Result<IbkrInstrumentRecord>.Failure(ConnectorErrors.InstrumentNotFound(key));
        }

        var ensured = await EnsureConidsAsync(channel, [resolved], ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<IbkrInstrumentRecord>.Failure(ensured.Error);
        }

        return cache.TryGetByConid(resolved, out var record) && Matches(key, record.Definition.Key)
            ? record
            : Result<IbkrInstrumentRecord>.Failure(ConnectorErrors.InstrumentNotFound(key));
    }

    private async Task<Result<IbkrInstrumentRecord>> OptionAsync(IbkrChannel channel, InstrumentKey key, CancellationToken ct)
    {
        if (key is not { Expiry: { } expiry, Strike: { } strike, Right: { } right })
        {
            return Result<IbkrInstrumentRecord>.Failure(IbkrErrors.InvalidRequest(
                $"{key} is not a complete option key: an expiry, a strike and a right are all required."));
        }

        var owner = await StockAsync(channel, new InstrumentKey(key.Venue, key.Symbol, AssetClass.Equity), ct).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return owner;
        }

        if (!owner.Value.Market.Is(IbkrMarket.Us))
        {
            return Result<IbkrInstrumentRecord>.Failure(ConnectorErrors.NotSupported("options on non-US underlyings through this connector"));
        }

        var api = await channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsFailure)
        {
            return Result<IbkrInstrumentRecord>.Failure(api.Error);
        }

        var info = await api.Value.SendElementAsync(
            HttpMethod.Get,
            HttpConnectorPath.WithQuery(
                "iserver/secdef/info",
                ("conid", owner.Value.Conid.ToString(CultureInfo.InvariantCulture)),
                ("sectype", IbkrMaps.SecTypeOption),
                ("month", IbkrTime.ContractMonth(expiry)),
                ("exchange", IbkrMaps.SmartRouting),
                ("strike", strike.ToString(CultureInfo.InvariantCulture)),
                ("right", right == OptionRight.Call ? "C" : "P")),
            body: null,
            ct).ConfigureAwait(false);

        if (info.IsFailure)
        {
            return Result<IbkrInstrumentRecord>.Failure(info.Error);
        }

        var conid = ReadContracts(info.Value)
            .Where(c => IbkrTime.ParseDate(c.MaturityDate) == expiry)
            .Select(c => IbkrNumber.Integer(c.Conid))
            .FirstOrDefault(found => found is > 0);

        if (conid is not { } resolved)
        {
            return Result<IbkrInstrumentRecord>.Failure(ConnectorErrors.InstrumentNotFound(key));
        }

        var ensured = await EnsureConidsAsync(channel, [resolved], ct).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return Result<IbkrInstrumentRecord>.Failure(ensured.Error);
        }

        return cache.TryGetByConid(resolved, out var record) && Matches(key, record.Definition.Key)
            ? record
            : Result<IbkrInstrumentRecord>.Failure(ConnectorErrors.InstrumentNotFound(key));
    }

    private async Task<Result<List<IbkrSecdef>>> SecdefAsync(IbkrApi api, IReadOnlyCollection<long> conids, CancellationToken ct)
    {
        var rows = new List<IbkrSecdef>();

        foreach (var chunk in conids.Chunk(Math.Max(1, options.MaxSecdefConids)))
        {
            var response = await api
                .GetAsync<IbkrSecdefResponse>(
                    HttpConnectorPath.WithQuery(
                        "trsrv/secdef",
                        ("conids", string.Join(",", chunk.Select(c => c.ToString(CultureInfo.InvariantCulture))))),
                    ct)
                .ConfigureAwait(false);

            if (response.IsFailure)
            {
                return Result<List<IbkrSecdef>>.Failure(response.Error);
            }

            rows.AddRange(response.Value.Secdef ?? []);
        }

        return rows;
    }

    private async Task AddStockAsync(IbkrChannel channel, IbkrSecdef row, CancellationToken ct)
    {
        if (IbkrNumber.Integer(row.Conid) is not { } conid || conid <= 0)
        {
            return;
        }

        var assetClass = IbkrMaps.ToCanonicalAssetClass(row.AssetClass);
        var venue = IbkrMaps.ToCanonicalVenue(row.ListingExchange);
        var currency = IbkrMaps.ToCanonicalCurrency(row.Currency);

        if (assetClass is not { IsSuccess: true, Value: AssetClass.Equity }
            || venue.IsFailure
            || currency.IsFailure
            || string.IsNullOrWhiteSpace(row.Ticker))
        {
            cache.MarkOutOfScope(conid);
            return;
        }

        var market = IbkrMaps.MarketForVenue(venue.Value);
        if (market.IsFailure)
        {
            cache.MarkOutOfScope(conid);
            return;
        }

        var lotSize = market.Value.Is(IbkrMarket.Us) ? 1m : await BoardLotAsync(channel, conid, ct).ConfigureAwait(false);

        cache.Add(new IbkrInstrumentRecord(
            new InstrumentDefinition
            {
                Key = new InstrumentKey(venue.Value, IbkrMaps.ToCanonicalSymbol(market.Value, row.Ticker), AssetClass.Equity),
                Name = string.IsNullOrWhiteSpace(row.Name) ? row.Ticker.Trim() : row.Name.Trim(),
                Currency = currency.Value,
                LotSize = lotSize,
                TickSize = TickSize(row, market.Value),
                Multiplier = 1m,
                SettlementDays = market.Value.SettlementDays,
            },
            market.Value,
            conid));
    }

    private void AddOption(IbkrSecdef row)
    {
        if (IbkrNumber.Integer(row.Conid) is not { } conid || conid <= 0)
        {
            return;
        }

        var expiry = IbkrTime.ParseDate(row.Expiry);
        var right = ParseRight(row.PutOrCall);
        var strike = IbkrNumber.Decimal(row.Strike);

        if (IbkrNumber.Integer(row.UnderlyingConid) is not { } underlying
            || !cache.TryGetByConid(underlying, out var owner)
            || !owner.Market.Is(IbkrMarket.Us)
            || expiry is null
            || right is null
            || strike is not > 0m)
        {
            cache.MarkOutOfScope(conid);
            return;
        }

        var multiplier = IbkrNumber.Decimal(row.Multiplier) is > 0m and var m ? m : 100m;
        var currency = IbkrMaps.ToCanonicalCurrency(row.Currency) is { IsSuccess: true } listed ? listed.Value : owner.Definition.Currency;

        cache.Add(new IbkrInstrumentRecord(
            new InstrumentDefinition
            {
                Key = new InstrumentKey(
                    owner.Definition.Key.Venue,
                    owner.Definition.Key.Symbol,
                    AssetClass.Option,
                    expiry.Value,
                    strike.Value,
                    right.Value),
                Name = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{owner.Definition.Key.Symbol} {expiry.Value:yyyy-MM-dd} {strike.Value} {right.Value}"),
                Currency = currency,
                LotSize = 1m,
                TickSize = TickSize(row, IbkrMarket.Us),
                Multiplier = multiplier,
                SettlementDays = 1,
            },
            IbkrMarket.Us,
            conid));
    }

    private async Task<decimal> BoardLotAsync(IbkrChannel channel, long conid, CancellationToken ct)
    {
        var api = await channel.IServerAsync(ct).ConfigureAwait(false);
        if (api.IsSuccess)
        {
            var rules = await api.Value
                .GetAsync<IbkrContractRules>($"iserver/contract/{conid.ToString(CultureInfo.InvariantCulture)}/info-and-rules", ct)
                .ConfigureAwait(false);

            if (rules.IsSuccess && IbkrNumber.Decimal(rules.Value.Rules?.SizeIncrement) is { } increment && increment > 0m)
            {
                return increment;
            }
        }

        logger.LogWarning(
            "{ConnectorId}: could not read the board lot for conid {Conid}; its definition carries a lot size of 1, and IBKR will refuse an odd lot.",
            IbkrAuth.ConnectorId,
            conid);

        return 1m;
    }

    private static decimal TickSize(IbkrSecdef row, IbkrMarket market)
    {
        foreach (var rule in row.IncrementRules ?? [])
        {
            if ((IbkrNumber.Decimal(rule.LowerEdge) ?? 0m) == 0m && IbkrNumber.Decimal(rule.Increment) is { } step && step > 0m)
            {
                return step;
            }
        }

        return market.TickSize;
    }

    private static bool IsOption(IbkrSecdef row) =>
        string.Equals(row.AssetClass?.Trim(), IbkrMaps.SecTypeOption, StringComparison.OrdinalIgnoreCase);

    private static OptionRight? ParseRight(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "C" or "CALL" => OptionRight.Call,
        "P" or "PUT" => OptionRight.Put,
        _ => null,
    };
}

/// <summary>Canonical keys to conids and back, from the cache only: a conid is found through reference data, never guessed.</summary>
internal sealed class IbkrSymbolTranslator(IbkrInstrumentCache cache) : ISymbolTranslator
{
    public Result<string> ToNative(InstrumentKey key) =>
        cache.TryGetByKey(key, out var record)
            ? record.Conid.ToString(CultureInfo.InvariantCulture)
            : Result<string>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                $"{key} has not been resolved through IBKR reference data, so its conid is not known.",
                VendorCode: null,
                VendorMessage: null,
                Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["instrument"] = key.ToString() }));

    public Result<InstrumentKey> ToCanonical(string nativeSymbol, string? nativeExchange = null) =>
        long.TryParse(nativeSymbol?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var conid)
        && cache.TryGetByConid(conid, out var record)
            ? record.Definition.Key
            : Result<InstrumentKey>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                $"Conid '{nativeSymbol}' has not been described through IBKR reference data.",
                VendorCode: null,
                VendorMessage: null,
                Context: new Dictionary<string, string>(StringComparer.Ordinal) { ["native"] = nativeSymbol ?? string.Empty }));
}
