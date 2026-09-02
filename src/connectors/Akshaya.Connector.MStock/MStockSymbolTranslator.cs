using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.MStock;

/// <summary>
/// Read side of the script master, as the symbol translator needs it.
///
/// It exists as an interface rather than a concrete cache so the translator can be unit
/// tested — and used for cash equities — without a hundred-megabyte CSV in the way.
/// </summary>
public interface IMStockInstrumentLookup
{
    /// <summary>Exact native symbol plus exchange segment to canonical identity.</summary>
    bool TryGetByNative(string tradingSymbol, string? exchange, out InstrumentKey key);

    /// <summary>Canonical identity to the exact native symbol mStock knows it by.</summary>
    bool TryGetNative(InstrumentKey key, out string tradingSymbol, out string exchange);

    /// <summary>Streaming subscriptions are by numeric token, not by symbol.</summary>
    bool TryGetToken(InstrumentKey key, out uint instrumentToken);

    /// <summary>Streaming ticks arrive carrying only a numeric token.</summary>
    bool TryGetByToken(uint instrumentToken, out InstrumentKey key);
}

/// <summary>
/// Canonical <see cref="InstrumentKey"/> to and from mStock's symbology.
///
/// mStock uses the NSE/BSE trading symbol: <c>INFY-EQ</c> for NSE cash, bare <c>INFY</c> for
/// BSE cash, <c>NIFTY25SEPFUT</c> for a monthly future, <c>NIFTY25SEP25000CE</c> for a monthly
/// option and <c>NIFTY2590925000CE</c> for the weekly expiring on 9 September 2025.
///
/// The asymmetry that shapes this class: encoding is total, decoding is not. Going out we
/// know the exact expiry date and can always produce the right symbol. Coming back, a monthly
/// symbol tells us the month but NOT the day — and NSE moved its expiry weekday twice in as
/// many years, so "last Thursday of the month" is no longer a safe reconstruction. Rather
/// than guess a date that would then be wrong on every greek, every calendar spread and every
/// settlement, derivative decoding requires the script master. Cash instruments, which carry
/// no expiry, decode structurally and need nothing.
///
/// <see cref="IMStockInstrumentLookup"/> is consulted FIRST in both directions when present:
/// the vendor's own master is authoritative and beats any rule we infer from the format.
/// </summary>
public sealed partial class MStockSymbolTranslator(IMStockInstrumentLookup? instruments = null)
    : ISymbolTranslator
{
    /// <summary>NSE cash instruments carry an <c>-EQ</c> series suffix; BSE ones do not.</summary>
    public const string NseCashSuffix = "-EQ";

    private const string FuturesSuffix = "FUT";

    private static readonly string[] MonthCodes =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    /// <summary>
    /// Weekly expiries encode the month as a single character: 1-9 for January to September,
    /// then O, N, D. A two-digit month would collide with the two-digit day that follows it.
    /// </summary>
    private static readonly char[] WeeklyMonthCodes =
        ['1', '2', '3', '4', '5', '6', '7', '8', '9', 'O', 'N', 'D'];

    /// <inheritdoc />
    public Result<string> ToNative(InstrumentKey key)
    {
        if (instruments is not null && instruments.TryGetNative(key, out var known, out _))
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

        return key.AssetClass switch
        {
            AssetClass.Equity or AssetClass.Etf => EncodeCash(key, symbol),
            AssetClass.Index => symbol,
            AssetClass.Future => EncodeFuture(key, symbol),
            AssetClass.Option => EncodeOption(key, symbol),
            _ => Result<string>.Failure(ConnectorErrors.InstrumentNotFound(key)),
        };
    }

    /// <inheritdoc />
    public Result<InstrumentKey> ToCanonical(string nativeSymbol, string? nativeExchange = null)
    {
        if (string.IsNullOrWhiteSpace(nativeSymbol))
        {
            return Result<InstrumentKey>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "An empty trading symbol cannot be translated."));
        }

        var symbol = nativeSymbol.Trim().ToUpperInvariant();

        // The vendor master wins whenever it knows the symbol: it carries the true expiry,
        // the true strike and the true segment, none of which we have to infer.
        if (instruments is not null && instruments.TryGetByNative(symbol, nativeExchange, out var known))
        {
            return known;
        }

        var venueResult = ResolveVenue(symbol, nativeExchange);
        if (venueResult.IsFailure)
        {
            return Result<InstrumentKey>.Failure(venueResult.Error);
        }

        var venue = venueResult.Value;
        var derivativeSegment = nativeExchange is not null && MStockMaps.IsDerivativeSegment(nativeExchange);

        if (symbol.EndsWith(NseCashSuffix, StringComparison.Ordinal))
        {
            return new InstrumentKey(venue, symbol[..^NseCashSuffix.Length], AssetClass.Equity);
        }

        if (derivativeSegment || IsStructurallyDerivative(symbol))
        {
            return DecodeDerivative(symbol, venue);
        }

        // Anything else on a cash segment is a plain BSE-style trading symbol.
        return new InstrumentKey(venue, symbol, AssetClass.Equity);
    }

    // --- encoding -------------------------------------------------------------------------

    private static Result<string> EncodeCash(InstrumentKey key, string symbol) => key.Venue.Mic switch
    {
        // NSE's capital-market segment appends the series. "-EQ" is the rolling-settlement
        // series and the only one a retail API can trade; BE/BZ instruments are illiquid
        // trade-for-trade names the platform does not route.
        "XNSE" => symbol.EndsWith(NseCashSuffix, StringComparison.Ordinal) ? symbol : symbol + NseCashSuffix,
        "XBOM" => symbol,
        _ => Result<string>.Failure(ConnectorErrors.InstrumentNotFound(key)),
    };

    private static Result<string> EncodeFuture(InstrumentKey key, string symbol)
    {
        if (key.Expiry is not { } expiry)
        {
            return Result<string>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"A future needs an expiry date; {key} has none."));
        }

        // Indian futures are monthly only, so they always use the monthly encoding regardless
        // of where in the month the expiry falls.
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
    /// rule for using the monthly (<c>25SEP</c>) rather than the weekly (<c>25909</c>) code.
    /// Expressed as "no further expiry of the same weekday falls inside this month" so that it
    /// keeps working after an exchange moves its expiry day — which NSE has now done twice.
    /// </summary>
    private static bool IsLastExpiryOfMonth(DateOnly expiry) => expiry.AddDays(7).Month != expiry.Month;

    private static string MonthlyCode(DateOnly expiry) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{expiry.Year % 100:D2}{MonthCodes[expiry.Month - 1]}");

    private static string WeeklyCode(DateOnly expiry) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{expiry.Year % 100:D2}{WeeklyMonthCodes[expiry.Month - 1]}{expiry.Day:D2}");

    /// <summary>
    /// Strikes are whole numbers on index and stock options but quarter-rupee on currency
    /// options, so trailing zeros must be trimmed rather than a fixed precision applied:
    /// <c>25000</c>, never <c>25000.00</c>, and <c>84.25</c>, never <c>84</c>.
    /// </summary>
    private static string FormatStrike(decimal strike) =>
        strike.ToString("0.####", CultureInfo.InvariantCulture);

    // --- decoding -------------------------------------------------------------------------

    private static Result<Venue> ResolveVenue(string symbol, string? nativeExchange)
    {
        if (!string.IsNullOrWhiteSpace(nativeExchange))
        {
            return MStockMaps.ToCanonicalVenue(nativeExchange);
        }

        // With no exchange hint, only the NSE series suffix is a reliable tell. Everything
        // else is genuinely ambiguous — INFY trades on both NSE and BSE — and picking one
        // would silently route an order to the wrong venue at a different price.
        if (symbol.EndsWith(NseCashSuffix, StringComparison.Ordinal))
        {
            return Venue.Nse;
        }

        return Result<Venue>.Failure(new Error(
            ConnectorErrorCodes.InstrumentNotFound,
            $"'{symbol}' does not identify a venue on its own and no exchange was supplied. "
            + "INFY trades on both NSE and BSE; guessing would route the order to the wrong book.",
            VendorCode: symbol,
            VendorMessage: null));
    }

    /// <summary>
    /// Whether the symbol's SHAPE is a derivative, not merely whether it ends in the right two
    /// letters. The distinction is load-bearing: "ACE" and "NUCLEUS" are cash scrips that end
    /// in CE and US respectively, and a suffix test alone would send them down the derivative
    /// path and fail an ordinary BSE equity lookup.
    /// </summary>
    private static bool IsStructurallyDerivative(string symbol)
    {
        if (symbol.EndsWith(FuturesSuffix, StringComparison.Ordinal))
        {
            return MonthlyExpiryPattern().IsMatch(symbol[..^FuturesSuffix.Length]);
        }

        if (symbol.EndsWith("CE", StringComparison.Ordinal) || symbol.EndsWith("PE", StringComparison.Ordinal))
        {
            var body = symbol[..^2];
            return WeeklyOptionPattern().IsMatch(body) || MonthlyOptionPattern().IsMatch(body);
        }

        return false;
    }

    private Result<InstrumentKey> DecodeDerivative(string symbol, Venue venue)
    {
        if (symbol.EndsWith(FuturesSuffix, StringComparison.Ordinal))
        {
            var body = symbol[..^FuturesSuffix.Length];
            var monthly = MonthlyExpiryPattern().Match(body);
            if (monthly.Success)
            {
                return NeedsMaster(symbol, venue, monthly.Groups["sym"].Value, AssetClass.Future);
            }

            return Result<InstrumentKey>.Failure(Undecodable(symbol));
        }

        var rightCode = symbol[^2..];
        var right = rightCode switch
        {
            "CE" => OptionRight.Call,
            "PE" => OptionRight.Put,
            _ => (OptionRight?)null,
        };

        if (right is null)
        {
            return Result<InstrumentKey>.Failure(Undecodable(symbol));
        }

        var optionBody = symbol[..^2];

        // Weekly first: its pattern is the more constrained of the two (a single month
        // character followed by exactly two day digits), so a monthly symbol cannot match it,
        // whereas a lazy monthly pattern could partially match a weekly one.
        var weekly = WeeklyOptionPattern().Match(optionBody);
        if (weekly.Success)
        {
            var year = 2000 + int.Parse(weekly.Groups["yy"].Value, CultureInfo.InvariantCulture);
            var monthIndex = Array.IndexOf(WeeklyMonthCodes, weekly.Groups["m"].Value[0]);
            var day = int.Parse(weekly.Groups["dd"].Value, CultureInfo.InvariantCulture);

            if (monthIndex < 0 || day is < 1 or > 31)
            {
                return Result<InstrumentKey>.Failure(Undecodable(symbol));
            }

            if (day > DateTime.DaysInMonth(year, monthIndex + 1))
            {
                return Result<InstrumentKey>.Failure(Undecodable(symbol));
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

        var monthlyOption = MonthlyOptionPattern().Match(optionBody);
        if (monthlyOption.Success)
        {
            return NeedsMaster(symbol, venue, monthlyOption.Groups["sym"].Value, AssetClass.Option);
        }

        return Result<InstrumentKey>.Failure(Undecodable(symbol));
    }

    /// <summary>
    /// A monthly derivative symbol names its month but not its day. Rather than invent one,
    /// say so — and say what would fix it. The nightly script-master ingest populates the
    /// lookup, after which this path is never taken.
    /// </summary>
    private Result<InstrumentKey> NeedsMaster(
        string symbol,
        Venue venue,
        string underlying,
        AssetClass assetClass)
    {
        if (instruments is not null && instruments.TryGetByNative(symbol, venue.Mic, out var key))
        {
            return key;
        }

        return Result<InstrumentKey>.Failure(new Error(
            ConnectorErrorCodes.InstrumentNotFound,
            $"'{symbol}' is a monthly {assetClass} on {underlying}. Its symbol encodes the expiry "
            + "month but not the expiry date, and the exchange has changed its expiry weekday, so "
            + "the date cannot be reconstructed from the symbol. Load the mStock script master "
            + "(IConnectorReference.GetInstrumentsAsync) so the exact expiry is known.",
            VendorCode: symbol,
            VendorMessage: null,
            Context: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tradingsymbol"] = symbol,
                ["venue"] = venue.Mic,
                ["assetClass"] = assetClass.ToString(),
            }));
    }

    private static decimal ParseStrike(string value) =>
        decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static Error Undecodable(string symbol) => new(
        ConnectorErrorCodes.InstrumentNotFound,
        $"'{symbol}' is not a trading symbol this connector recognises.",
        VendorCode: symbol,
        VendorMessage: null);

    // Source-generated so the patterns are compiled once at build time rather than
    // interpreted on every row of a two-hundred-thousand-row script master.

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
/// A translator that never consults a script master. Useful in tests and in the
/// authentication phase, where no instrument data has been loaded yet.
/// </summary>
public sealed class MStockStructuralSymbolTranslator : ISymbolTranslator
{
    private readonly MStockSymbolTranslator _inner = new();

    /// <inheritdoc />
    public Result<string> ToNative(InstrumentKey key) => _inner.ToNative(key);

    /// <inheritdoc />
    public Result<InstrumentKey> ToCanonical(string nativeSymbol, string? nativeExchange = null) =>
        _inner.ToCanonical(nativeSymbol, nativeExchange);
}

/// <summary>
/// The <c>EXCHANGE:TRADINGSYMBOL</c> key mStock's quote routes take and return. Kept next to
/// the translator because it is the same idea one layer up, and because getting the colon
/// wrong silently returns an empty quote map rather than an error.
/// </summary>
internal static class MStockQuoteKey
{
    public static string Build(string exchange, string tradingSymbol) => $"{exchange}:{tradingSymbol}";

    public static bool TrySplit(
        string key,
        [NotNullWhen(true)] out string? exchange,
        [NotNullWhen(true)] out string? tradingSymbol)
    {
        var separator = key.IndexOf(':');
        if (separator <= 0 || separator == key.Length - 1)
        {
            exchange = null;
            tradingSymbol = null;
            return false;
        }

        exchange = key[..separator];
        tradingSymbol = key[(separator + 1)..];
        return true;
    }
}
