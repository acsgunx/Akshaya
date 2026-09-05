using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Fyers;

/// <summary>
/// Read side of the symbol master, as the symbol translator needs it.
///
/// It exists as an interface rather than a concrete cache so the translator can be unit tested —
/// and used for NSE cash equities — without several megabytes of CSV in the way.
/// </summary>
public interface IFyersInstrumentLookup
{
    /// <summary>Exact native symbol ("NSE:SBIN-EQ") to canonical identity.</summary>
    bool TryGetByNative(string symbolTicker, out InstrumentKey key);

    /// <summary>Canonical identity to the exact native symbol FYERS knows it by.</summary>
    bool TryGetNative(InstrumentKey key, [NotNullWhen(true)] out string? symbolTicker);

    /// <summary>Payloads that identify an instrument only by its fyToken.</summary>
    bool TryGetByToken(string fyToken, out InstrumentKey key);
}

/// <summary>
/// Canonical <see cref="InstrumentKey"/> to and from the FYERS symbology.
///
/// FYERS symbols carry their own exchange prefix — <c>NSE:SBIN-EQ</c>, <c>BSE:360ONE-A</c>,
/// <c>NSE:NIFTY50-INDEX</c>, <c>NSE:BANKNIFTY26SEPFUT</c>, <c>NSE:NIFTY26SEP25000CE</c> for a
/// monthly option and <c>NSE:NIFTY2632423050PE</c> for the weekly expiring on 24 March 2026.
/// That prefix is a genuine convenience: unlike most Indian APIs there is never a second
/// "exchange" argument to get wrong, and a symbol is unambiguous on its own.
///
/// Two asymmetries shape this class, and both are the same underlying point — the symbol does
/// not always carry everything the canonical key needs:
///
/// * MONTHLY DERIVATIVES name their expiry MONTH but not the DAY, and NSE has moved its expiry
///   weekday more than once, so "last Thursday" is no longer a safe reconstruction. Decoding one
///   requires the master. Weekly symbols carry a full date and decode structurally.
/// * BSE CASH instruments carry a settlement series that cannot be derived — <c>360ONE</c> is
///   <c>-A</c> and <c>3IINFOLTD</c> is <c>-T</c>, and the difference is the exchange's own
///   classification, not a rule. Encoding one requires the master. NSE cash does not: <c>-EQ</c>
///   is the rolling-settlement series and the only one the platform routes.
///
/// <see cref="IFyersInstrumentLookup"/> is consulted FIRST in both directions when present: the
/// vendor's own master is authoritative and beats any rule we infer from the format.
/// </summary>
public sealed partial class FyersSymbolTranslator(IFyersInstrumentLookup? instruments = null)
    : ISymbolTranslator
{
    /// <summary>NSE's rolling-settlement series, appended to every ordinary NSE cash symbol.</summary>
    public const string NseCashSeries = "-EQ";

    /// <summary>The suffix that marks an index rather than a tradable scrip.</summary>
    public const string IndexSuffix = "-INDEX";

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

        var exchange = FyersMaps.ToNativeExchange(key.Venue);
        if (exchange.IsFailure)
        {
            return Result<string>.Failure(ConnectorErrors.InstrumentNotFound(key));
        }

        var body = key.AssetClass switch
        {
            AssetClass.Equity or AssetClass.Etf => EncodeCash(key, symbol),
            AssetClass.Index => symbol + IndexSuffix,
            AssetClass.Future => EncodeFuture(key, symbol),
            AssetClass.Option => EncodeOption(key, symbol),
            _ => Result<string>.Failure(ConnectorErrors.InstrumentNotFound(key)),
        };

        return body.Map(value => $"{exchange.Value}:{value}");
    }

    /// <inheritdoc />
    /// <param name="nativeSymbol">A full FYERS ticker, exchange prefix included.</param>
    /// <param name="nativeExchange">
    /// Ignored when the symbol already carries a prefix, which it always does on the routes this
    /// connector reads. Accepted only because the contract passes it, and used as a fallback for
    /// the rare payload that reports a bare scrip name.
    /// </param>
    public Result<InstrumentKey> ToCanonical(string nativeSymbol, string? nativeExchange = null)
    {
        if (string.IsNullOrWhiteSpace(nativeSymbol))
        {
            return Result<InstrumentKey>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                "An empty trading symbol cannot be translated."));
        }

        var ticker = nativeSymbol.Trim().ToUpperInvariant();

        // The vendor master wins whenever it knows the symbol: it carries the true expiry, the
        // true strike, the true series and the true asset class, none of which we have to infer.
        if (instruments is not null && instruments.TryGetByNative(ticker, out var known))
        {
            return known;
        }

        if (!TrySplit(ticker, out var exchangeCode, out var body))
        {
            // No prefix. Fall back to the supplied exchange, and refuse rather than guess when
            // there is none: SBIN trades on both NSE and BSE at different prices, and picking
            // one silently routes the order to the wrong book.
            if (string.IsNullOrWhiteSpace(nativeExchange))
            {
                return Result<InstrumentKey>.Failure(new Error(
                    ConnectorErrorCodes.InstrumentNotFound,
                    $"'{ticker}' carries no exchange prefix and none was supplied. FYERS symbols are "
                    + "written 'NSE:SBIN-EQ'; a bare scrip name does not identify a venue, and SBIN "
                    + "trades on both NSE and BSE.",
                    VendorCode: ticker,
                    VendorMessage: null));
            }

            exchangeCode = nativeExchange.Trim().ToUpperInvariant();
            body = ticker;
        }

        var venue = FyersMaps.ToCanonicalVenue(exchangeCode);
        if (venue.IsFailure)
        {
            // A venue this connector does not serve is "not tradable here", not a vocabulary bug.
            return Result<InstrumentKey>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                venue.Error.Message,
                VendorCode: ticker,
                VendorMessage: venue.Error.VendorMessage));
        }

        return DecodeBody(body, venue.Value, ticker);
    }

    // --- encoding -------------------------------------------------------------------------

    private static Result<string> EncodeCash(InstrumentKey key, string symbol)
    {
        // Already carries a series (a caller round-tripping a master-sourced symbol): leave it.
        if (CashSeriesPattern().IsMatch(symbol))
        {
            return symbol;
        }

        return key.Venue.Mic switch
        {
            "XNSE" => symbol + NseCashSeries,

            // BSE has no default series. -A, -B, -T, -XT and friends are the exchange's own
            // groupings and there is no rule that derives one from the scrip name, so guessing
            // would mean sending an order for an instrument that may not exist — or, worse, one
            // that does and settles differently.
            "XBOM" => Result<string>.Failure(new Error(
                ConnectorErrorCodes.InstrumentNotFound,
                $"'{symbol}' needs its BSE settlement series (-A, -B, -T and so on) and the symbol "
                + "master has not been ingested. Load it via IConnectorReference.GetInstrumentsAsync; "
                + "the series cannot be derived from the scrip name.",
                VendorCode: symbol,
                VendorMessage: null,
                Context: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["symbol"] = symbol,
                    ["venue"] = key.Venue.Mic,
                })),

            _ => Result<string>.Failure(ConnectorErrors.InstrumentNotFound(key)),
        };
    }

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
    /// rule for using the monthly (<c>26SEP</c>) rather than the weekly (<c>26924</c>) code.
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
    /// so trailing zeros are trimmed rather than a fixed precision applied: <c>25000</c>, never
    /// <c>25000.00</c>, and <c>84.25</c>, never <c>84</c>.
    /// </summary>
    private static string FormatStrike(decimal strike) =>
        strike.ToString("0.####", CultureInfo.InvariantCulture);

    // --- decoding -------------------------------------------------------------------------

    private static bool TrySplit(
        string ticker,
        [NotNullWhen(true)] out string? exchange,
        [NotNullWhen(true)] out string? body)
    {
        var separator = ticker.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == ticker.Length - 1)
        {
            exchange = null;
            body = null;
            return false;
        }

        exchange = ticker[..separator];
        body = ticker[(separator + 1)..];
        return true;
    }

    private Result<InstrumentKey> DecodeBody(string body, Venue venue, string ticker)
    {
        if (body.EndsWith(IndexSuffix, StringComparison.Ordinal))
        {
            return new InstrumentKey(venue, body[..^IndexSuffix.Length], AssetClass.Index);
        }

        if (IsStructurallyDerivative(body))
        {
            return DecodeDerivative(body, venue, ticker);
        }

        // A cash instrument. The settlement series is stripped so that the same scrip has the
        // same canonical symbol wherever it is held: NSE's -EQ and BSE's -A are the venue's own
        // classification of one instrument, not two different instruments, and keeping them in
        // the key would stop a blended portfolio recognising the position.
        //
        // Equity rather than Etf is the structural answer, because the two are indistinguishable
        // from the ticker alone. The master tells them apart and wins above.
        var series = CashSeriesPattern().Match(body);
        return new InstrumentKey(
            venue,
            series.Success ? body[..series.Index] : body,
            AssetClass.Equity);
    }

    /// <summary>
    /// Whether the symbol's SHAPE is a derivative, not merely whether it ends in the right two
    /// letters. The distinction is load-bearing: "ACE" and "NUCLEUS" are cash scrips that end in
    /// CE and US respectively, and a suffix test alone would send them down the derivative path
    /// and fail an ordinary equity lookup.
    /// </summary>
    private static bool IsStructurallyDerivative(string body)
    {
        if (body.EndsWith(FuturesSuffix, StringComparison.Ordinal))
        {
            return MonthlyExpiryPattern().IsMatch(body[..^FuturesSuffix.Length]);
        }

        if (body.EndsWith("CE", StringComparison.Ordinal) || body.EndsWith("PE", StringComparison.Ordinal))
        {
            var stem = body[..^2];
            return WeeklyOptionPattern().IsMatch(stem) || MonthlyOptionPattern().IsMatch(stem);
        }

        return false;
    }

    private Result<InstrumentKey> DecodeDerivative(string body, Venue venue, string ticker)
    {
        if (body.EndsWith(FuturesSuffix, StringComparison.Ordinal))
        {
            var monthly = MonthlyExpiryPattern().Match(body[..^FuturesSuffix.Length]);
            return monthly.Success
                ? NeedsMaster(ticker, monthly.Groups["sym"].Value, AssetClass.Future)
                : Result<InstrumentKey>.Failure(Undecodable(ticker));
        }

        var right = body[^2..] switch
        {
            "CE" => OptionRight.Call,
            "PE" => OptionRight.Put,
            _ => (OptionRight?)null,
        };

        if (right is null)
        {
            return Result<InstrumentKey>.Failure(Undecodable(ticker));
        }

        var stem = body[..^2];

        // Weekly first: its pattern is the more constrained of the two (a single month character
        // followed by exactly two day digits), so a monthly symbol cannot match it, whereas a
        // lazy monthly pattern could partially match a weekly one.
        var weekly = WeeklyOptionPattern().Match(stem);
        if (weekly.Success)
        {
            var year = 2000 + int.Parse(weekly.Groups["yy"].Value, CultureInfo.InvariantCulture);
            var monthIndex = Array.IndexOf(WeeklyMonthCodes, weekly.Groups["m"].Value[0]);
            var day = int.Parse(weekly.Groups["dd"].Value, CultureInfo.InvariantCulture);

            if (monthIndex < 0 || day < 1 || day > DateTime.DaysInMonth(year, monthIndex + 1))
            {
                return Result<InstrumentKey>.Failure(Undecodable(ticker));
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
            ? NeedsMaster(ticker, monthlyOption.Groups["sym"].Value, AssetClass.Option)
            : Result<InstrumentKey>.Failure(Undecodable(ticker));
    }

    /// <summary>
    /// A monthly derivative symbol names its month but not its day. Rather than invent one, say
    /// so — and say what would fix it. The nightly symbol-master ingest populates the lookup,
    /// after which this path is never taken.
    /// </summary>
    private Result<InstrumentKey> NeedsMaster(string ticker, string underlying, AssetClass assetClass)
    {
        if (instruments is not null && instruments.TryGetByNative(ticker, out var key))
        {
            return key;
        }

        return Result<InstrumentKey>.Failure(new Error(
            ConnectorErrorCodes.InstrumentNotFound,
            $"'{ticker}' is a monthly {assetClass} on {underlying}. Its symbol encodes the expiry "
            + "month but not the expiry date, and the exchange has changed its expiry weekday, so the "
            + "date cannot be reconstructed from the symbol. Load the FYERS symbol master "
            + "(IConnectorReference.GetInstrumentsAsync) so the exact expiry is known.",
            VendorCode: ticker,
            VendorMessage: null,
            Context: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["symbol"] = ticker,
                ["assetClass"] = assetClass.ToString(),
            }));
    }

    private static decimal ParseStrike(string value) =>
        decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static Error Undecodable(string ticker) => new(
        ConnectorErrorCodes.InstrumentNotFound,
        $"'{ticker}' is not a trading symbol this connector recognises.",
        VendorCode: ticker,
        VendorMessage: null);

    // Source-generated so the patterns are compiled once at build time rather than interpreted on
    // every row of a hundred-thousand-row symbol master.

    /// <summary>
    /// A settlement series at the very end of a cash symbol: NSE's <c>-EQ</c> and <c>-BE</c>,
    /// BSE's <c>-A</c>, <c>-T</c>, <c>-XT</c>. One or two letters only, which is what keeps it
    /// off <c>-INDEX</c> and off a hyphenated scrip name like BAJAJ-AUTO.
    /// </summary>
    [GeneratedRegex(@"-[A-Z]{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CashSeriesPattern();

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
