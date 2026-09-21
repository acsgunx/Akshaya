using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Zerodha;

/// <summary>
/// Read side of the instrument master, as the rest of the connector needs it.
///
/// It exists as an interface rather than a concrete cache so the translator can be unit tested —
/// and used for cash equities — without several megabytes of CSV in the way.
/// </summary>
public interface IZerodhaInstrumentLookup
{
    /// <summary>Exchange-qualified native symbol ("NSE:INFY") to canonical identity.</summary>
    bool TryGetByNative(string qualifiedSymbol, out InstrumentKey key);

    /// <summary>Canonical identity to the exchange-qualified symbol Kite knows it by.</summary>
    bool TryGetNative(InstrumentKey key, [NotNullWhen(true)] out string? qualifiedSymbol);

    /// <summary>The socket and the history route both address instruments by numeric token.</summary>
    bool TryGetToken(InstrumentKey key, out uint instrumentToken);

    /// <summary>Ticks arrive carrying only a numeric token.</summary>
    bool TryGetByToken(uint instrumentToken, out InstrumentKey key);
}

/// <summary>
/// Canonical <see cref="InstrumentKey"/> to and from Kite's symbology.
///
/// Kite keeps the exchange and the trading symbol in separate fields on its order routes, but
/// joins them with a colon for quotes — <c>NSE:INFY</c>. This translator emits that JOINED form
/// as the native symbol, for two reasons: it is the only form that decodes unambiguously on its
/// own (INFY trades on both NSE and BSE), and it is exactly what the quote routes already want.
/// The orders facet splits it back into its two fields with
/// <see cref="ZerodhaInstrument.TrySplit"/>.
///
/// Cash symbols are the pleasant surprise here. Unlike most Indian APIs, Kite's plain rolling-
/// settlement series carries NO suffix — <c>INFY</c>, not <c>INFY-EQ</c> — and the non-plain
/// series carry theirs as part of the symbol (<c>IDEA-BE</c>, <c>GOLDSTAR-SM</c>). So the
/// canonical symbol is the trading symbol verbatim, and cash translation is total and lossless in
/// both directions with no master needed. Nothing is stripped, so nothing has to be guessed back.
///
/// The one asymmetry is derivatives: a MONTHLY contract names its expiry month but not the day,
/// and NSE has moved its expiry weekday more than once, so decoding one requires the master.
/// Weekly symbols carry a full date and decode structurally.
///
/// <see cref="IZerodhaInstrumentLookup"/> is consulted FIRST in both directions when present: the
/// vendor's own master is authoritative and beats any rule we infer from the format.
/// </summary>
public sealed partial class ZerodhaSymbolTranslator(IZerodhaInstrumentLookup? instruments = null)
    : ISymbolTranslator
{
    private const string FuturesSuffix = "FUT";

    private static readonly string[] MonthCodes =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    /// <summary>
    /// Weekly expiries encode the month as a single character: 1-9 for January to September, then
    /// O, N, D. A two-digit month would collide with the two-digit day that follows it.
    /// </summary>
    private static readonly char[] WeeklyMonthCodes =
        ['1', '2', '3', '4', '5', '6', '7', '8', '9', 'O', 'N', 'D'];

    /// <inheritdoc />
    public Result<string> ToNative(InstrumentKey key)
    {
        if (instruments is not null && instruments.TryGetNative(key, out var known))
        {
            return known;
        }

        var symbol = key.Symbol.Trim().ToUpperInvariant();
        if (symbol.Length == 0)
        {
            return Result<string>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "An instrument key with an empty symbol cannot be translated."));
        }

        var exchange = ZerodhaMaps.ToNativeExchange(key.Venue, key.AssetClass);
        if (exchange.IsFailure)
        {
            return Result<string>.Failure(ConnectorErrors.InstrumentNotFound(key));
        }

        var body = key.AssetClass switch
        {
            // Cash and indices are carried verbatim. Kite's own symbol is the canonical one.
            AssetClass.Equity or AssetClass.Etf or AssetClass.Index => Result<string>.Success(symbol),
            AssetClass.Future => EncodeFuture(key, symbol),
            AssetClass.Option => EncodeOption(key, symbol),
            _ => Result<string>.Failure(ConnectorErrors.InstrumentNotFound(key)),
        };

        return body.Map(value => ZerodhaInstrument.Qualify(exchange.Value, value));
    }

    /// <inheritdoc />
    /// <param name="nativeSymbol">
    /// Either an exchange-qualified symbol ("NSE:INFY") or a bare trading symbol, in which case
    /// <paramref name="nativeExchange"/> must say which exchange it belongs to.
    /// </param>
    /// <param name="nativeExchange">The Kite exchange segment (NSE, BSE, NFO, BFO).</param>
    public Result<InstrumentKey> ToCanonical(string nativeSymbol, string? nativeExchange = null)
    {
        if (string.IsNullOrWhiteSpace(nativeSymbol))
        {
            return Result<InstrumentKey>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "An empty trading symbol cannot be translated."));
        }

        var raw = nativeSymbol.Trim().ToUpperInvariant();

        string exchange;
        string symbol;

        if (ZerodhaInstrument.TrySplit(raw, out var splitExchange, out var splitSymbol))
        {
            exchange = splitExchange;
            symbol = splitSymbol;
        }
        else if (!string.IsNullOrWhiteSpace(nativeExchange))
        {
            exchange = nativeExchange.Trim().ToUpperInvariant();
            symbol = raw;
        }
        else
        {
            // Refuse rather than guess: INFY trades on both NSE and BSE at different prices, and
            // picking one silently routes the order to the wrong book.
            return Result<InstrumentKey>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                $"'{raw}' carries no exchange and none was supplied. Kite identifies an instrument by "
                + "exchange AND trading symbol; a bare symbol does not name a venue, and INFY trades "
                + "on both NSE and BSE.",
                VendorCode: raw,
                VendorMessage: null));
        }

        // The vendor master wins whenever it knows the symbol: it carries the true expiry, the
        // true strike and the true asset class, none of which we have to infer.
        var qualified = ZerodhaInstrument.Qualify(exchange, symbol);
        if (instruments is not null && instruments.TryGetByNative(qualified, out var known))
        {
            return known;
        }

        var venue = ZerodhaMaps.ToCanonicalVenue(exchange);
        if (venue.IsFailure)
        {
            // A venue this connector does not serve is "not tradable here", not a vocabulary bug.
            return Result<InstrumentKey>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                venue.Error.Message,
                VendorCode: qualified,
                VendorMessage: venue.Error.VendorMessage));
        }

        // The EXCHANGE decides whether this is a derivative, not the shape of the symbol. Kite
        // segregates them — NFO and BFO carry nothing but derivatives, NSE and BSE nothing but
        // cash — which makes this a fact rather than the inference it has to be at brokers that
        // put everything on one segment. It also means a cash scrip whose name happens to end in
        // "CE" can never be mistaken for a call option.
        return ZerodhaMaps.IsDerivativeSegment(exchange)
            ? DecodeDerivative(symbol, venue.Value, qualified)
            : new InstrumentKey(venue.Value, symbol, AssetClass.Equity);
    }

    // --- encoding -------------------------------------------------------------------------

    private static Result<string> EncodeFuture(InstrumentKey key, string symbol)
    {
        if (key.Expiry is not { } expiry)
        {
            return Result<string>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"A future needs an expiry date; {key} has none."));
        }

        // Indian futures are monthly only, so they always use the monthly encoding regardless of
        // where in the month the expiry falls.
        return $"{symbol}{MonthlyCode(expiry)}{FuturesSuffix}";
    }

    private static Result<string> EncodeOption(InstrumentKey key, string symbol)
    {
        if (key.Expiry is not { } expiry)
        {
            return Result<string>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"An option needs an expiry date; {key} has none."));
        }

        if (key.Strike is not { } strike)
        {
            return Result<string>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"An option needs a strike; {key} has none."));
        }

        if (key.Right is not { } right)
        {
            return Result<string>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"An option needs a call/put right; {key} has none."));
        }

        var expiryCode = IsLastExpiryOfMonth(expiry) ? MonthlyCode(expiry) : WeeklyCode(expiry);
        var rightCode = right == OptionRight.Call ? "CE" : "PE";

        return $"{symbol}{expiryCode}{FormatStrike(strike)}{rightCode}";
    }

    /// <summary>
    /// True when this expiry is the last one in its calendar month, which is the exchange's own
    /// rule for using the monthly (<c>26SEP</c>) rather than the weekly (<c>26908</c>) code.
    /// Expressed as "no further expiry of the same weekday falls inside this month" so that it
    /// keeps working after an exchange moves its expiry day — which NSE has now done twice.
    /// </summary>
    private static bool IsLastExpiryOfMonth(DateOnly expiry) => expiry.AddDays(7).Month != expiry.Month;

    private static string MonthlyCode(DateOnly expiry) =>
        string.Create(CultureInfo.InvariantCulture, $"{expiry.Year % 100:D2}{MonthCodes[expiry.Month - 1]}");

    private static string WeeklyCode(DateOnly expiry) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{expiry.Year % 100:D2}{WeeklyMonthCodes[expiry.Month - 1]}{expiry.Day:D2}");

    /// <summary>
    /// Strikes are whole numbers on index and stock options but fractional on currency options,
    /// so trailing zeros are trimmed rather than a fixed precision applied: <c>23900</c>, never
    /// <c>23900.00</c>.
    /// </summary>
    private static string FormatStrike(decimal strike) =>
        strike.ToString("0.####", CultureInfo.InvariantCulture);

    // --- decoding -------------------------------------------------------------------------

    private Result<InstrumentKey> DecodeDerivative(string symbol, Venue venue, string qualified)
    {
        if (symbol.EndsWith(FuturesSuffix, StringComparison.Ordinal))
        {
            var monthly = MonthlyExpiryPattern().Match(symbol[..^FuturesSuffix.Length]);
            return monthly.Success
                ? NeedsMaster(qualified, monthly.Groups["sym"].Value, AssetClass.Future)
                : Result<InstrumentKey>.Failure(Undecodable(qualified));
        }

        var right = symbol.Length >= 2
            ? symbol[^2..] switch
            {
                "CE" => OptionRight.Call,
                "PE" => OptionRight.Put,
                _ => (OptionRight?)null,
            }
            : null;

        if (right is null)
        {
            return Result<InstrumentKey>.Failure(Undecodable(qualified));
        }

        var stem = symbol[..^2];

        // Weekly first: its pattern is the more constrained of the two (a single month character
        // followed by exactly two day digits), so a monthly symbol cannot match it, whereas a lazy
        // monthly pattern could partially match a weekly one.
        var weekly = WeeklyOptionPattern().Match(stem);
        if (weekly.Success)
        {
            var year = 2000 + int.Parse(weekly.Groups["yy"].Value, CultureInfo.InvariantCulture);
            var monthIndex = Array.IndexOf(WeeklyMonthCodes, weekly.Groups["m"].Value[0]);
            var day = int.Parse(weekly.Groups["dd"].Value, CultureInfo.InvariantCulture);

            if (monthIndex < 0 || day < 1 || day > DateTime.DaysInMonth(year, monthIndex + 1))
            {
                return Result<InstrumentKey>.Failure(Undecodable(qualified));
            }

            // A weekly symbol carries a full date, so no master lookup is needed.
            return new InstrumentKey(
                venue,
                weekly.Groups["sym"].Value,
                AssetClass.Option,
                new DateOnly(year, monthIndex + 1, day),
                ParseStrike(weekly.Groups["strike"].Value),
                right);
        }

        var monthlyOption = MonthlyOptionPattern().Match(stem);
        return monthlyOption.Success
            ? NeedsMaster(qualified, monthlyOption.Groups["sym"].Value, AssetClass.Option)
            : Result<InstrumentKey>.Failure(Undecodable(qualified));
    }

    /// <summary>
    /// A monthly derivative symbol names its month but not its day. Rather than invent one, say
    /// so — and say what would fix it. The daily instrument-master ingest populates the lookup,
    /// after which this path is never taken.
    /// </summary>
    private Result<InstrumentKey> NeedsMaster(string qualified, string underlying, AssetClass assetClass)
    {
        if (instruments is not null && instruments.TryGetByNative(qualified, out var key))
        {
            return key;
        }

        return Result<InstrumentKey>.Failure(new Error(
            ConnectorErrorCodes.InstrumentNotFound,
            $"'{qualified}' is a monthly {assetClass.ToString().ToLowerInvariant()} on {underlying}: its symbol gives the "
            + "expiry month but not the day, and the exact date comes from Zerodha's instrument list, which this "
            + "server has not downloaded yet. It downloads the first time a chart, a live price or the "
            + "instrument search is opened; try again after that.",
            VendorCode: qualified,
            VendorMessage: null,
            Context: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["symbol"] = qualified,
                ["assetClass"] = assetClass.ToString(),
            }));
    }

    private static decimal ParseStrike(string value) =>
        decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static Error Undecodable(string qualified) => new(
        ConnectorErrorCodes.InstrumentNotFound,
        $"'{qualified}' is not a trading symbol this connector recognises.",
        VendorCode: qualified,
        VendorMessage: null);

    // Source-generated so the patterns are compiled once at build time rather than interpreted on
    // every row of a hundred-thousand-row instrument master.

    [GeneratedRegex(
        @"^(?<sym>[A-Z][A-Z0-9&\-]*?)(?<yy>\d{2})(?<mon>JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex MonthlyExpiryPattern();

    [GeneratedRegex(
        @"^(?<sym>[A-Z][A-Z0-9&\-]*?)(?<yy>\d{2})(?<mon>JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)(?<strike>\d+(?:\.\d+)?)$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex MonthlyOptionPattern();

    [GeneratedRegex(
        @"^(?<sym>[A-Z][A-Z0-9&\-]*?)(?<yy>\d{2})(?<m>[1-9OND])(?<dd>\d{2})(?<strike>\d+(?:\.\d+)?)$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex WeeklyOptionPattern();
}

/// <summary>
/// The <c>EXCHANGE:TRADINGSYMBOL</c> pairing Kite's quote routes take and return, and which this
/// connector uses as its native symbol throughout.
///
/// Kept in one place because the colon is load-bearing in two directions: the quote routes return
/// an empty map rather than an error when the key is malformed, and the ORDER routes want the two
/// halves as separate form fields, so every placement splits one of these apart.
/// </summary>
internal static class ZerodhaInstrument
{
    public static string Qualify(string exchange, string tradingSymbol) => $"{exchange}:{tradingSymbol}";

    public static bool TrySplit(
        string qualified,
        [NotNullWhen(true)] out string? exchange,
        [NotNullWhen(true)] out string? tradingSymbol)
    {
        var separator = qualified.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == qualified.Length - 1)
        {
            exchange = null;
            tradingSymbol = null;
            return false;
        }

        exchange = qualified[..separator];
        tradingSymbol = qualified[(separator + 1)..];
        return true;
    }
}
