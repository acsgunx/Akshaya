using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Paper.Charges;

/// <summary>
/// US equity and listed-option transaction costs for NASDAQ and NYSE.
///
/// The US is the market where "zero commission" makes people believe trading is free. It is
/// not: the regulatory fees are asymmetric — charged on sells and not on buys — and they are
/// per SHARE rather than per dollar, so they scale with size rather than with price. A
/// strategy that trades a large number of low-priced shares pays materially more than a
/// percentage model predicts, and pays it only when it exits.
///
/// WORKED EXAMPLE — SELL 1,000 shares at $50.00 (proceeds $50,000):
/// <code>
///   Commission           0.00   zero-commission retail
///   SEC fee              1.39   $27.80 per $1,000,000 of proceeds, sells only
///   FINRA TAF            0.17   $0.000166 per share, sells only, capped at $8.30
///   ---------------------------------
///   Total                1.56
/// </code>
/// The same trade as a BUY: every line is zero, total 0.00. That asymmetry is why round-trip
/// cost in the US cannot be modelled as "twice the one-way cost".
///
/// WORKED EXAMPLE — SELL 10 equity option contracts at $2.00 premium:
/// <code>
///   Commission           6.50   $0.65 per contract
///   SEC fee              0.06   on $2,000 of premium proceeds
///   FINRA TAF            0.02   $0.00279 per contract, sells only
///   Options regulatory   0.27   $0.02685 per contract, both sides
///   ---------------------------------
///   Total                6.85
/// </code>
/// </summary>
public sealed class UsChargeSchedule : IChargeSchedule
{
    // ---------------------------------------------------------------------------------
    // COMMISSION. Broker-set. Zero is the honest default for US retail equity in 2026, but a
    // deployment routing through a per-share broker must override it or its backtest will be
    // optimistic by the whole commission line.
    // ---------------------------------------------------------------------------------

    /// <summary>Per-share equity commission. Zero for the zero-commission retail model.
    /// REVIEW: verify against the broker's published schedule, last checked 2026-09.</summary>
    public const decimal EquityCommissionPerShareUsd = 0m;

    /// <summary>Minimum equity commission per order, applied only when the per-share rate is
    /// non-zero. REVIEW: verify against the broker's published schedule, last checked 2026-09.</summary>
    public const decimal EquityCommissionMinimumUsd = 0m;

    /// <summary>Per-contract options commission. Even zero-commission equity brokers charge
    /// this on options, and it is the largest line on a small options trade.
    /// REVIEW: verify against the broker's published schedule, last checked 2026-09.
    /// $0.65 is the common US retail rate; some brokers are at $0.50 or tiered by volume.</summary>
    public const decimal OptionCommissionPerContractUsd = 0.65m;

    // ---------------------------------------------------------------------------------
    // REGULATORY FEES. Set by the SEC and FINRA, identical whoever routed the order, and
    // charged on SELLS ONLY. The SEC rate in particular is reset by the Commission — usually
    // at the start of the federal fiscal year, sometimes mid-year — and has swung by a factor
    // of five within a decade. Any figure here is a snapshot.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// SEC Section 31 fee, expressed as a fraction of sale proceeds. Charged on the SELL side
    /// only.
    /// REVIEW: verify against the SEC's current fee-rate advisory, last checked 2026-09. The
    /// value below corresponds to $27.80 per $1,000,000 of proceeds; I am NOT confident this
    /// is the rate currently in force, because the SEC re-sets it at least annually and has
    /// used rates from $5.10 to $27.80 per million in recent years. Do not treat this as
    /// authoritative.
    /// </summary>
    public const decimal SecFeeRate = 0.0000278m;

    /// <summary>FINRA Trading Activity Fee per share sold, equities.
    /// REVIEW: verify against FINRA's published TAF schedule, last checked 2026-09.</summary>
    public const decimal FinraTafPerShareUsd = 0.000166m;

    /// <summary>Cap on the equity TAF per trade, in US dollars.
    /// REVIEW: verify against FINRA's published TAF schedule, last checked 2026-09.</summary>
    public const decimal FinraTafCapUsd = 8.30m;

    /// <summary>FINRA TAF per options contract sold.
    /// REVIEW: verify against FINRA's published TAF schedule, last checked 2026-09.</summary>
    public const decimal FinraTafPerOptionContractUsd = 0.00279m;

    /// <summary>Options Regulatory Fee per contract, charged on BOTH sides unlike the others.
    /// REVIEW: verify against the exchanges' published ORF, last checked 2026-09. The ORF
    /// differs per exchange and changes frequently; this is a representative figure, not a
    /// quoted one.</summary>
    public const decimal OptionsRegulatoryFeePerContractUsd = 0.02685m;

    private static readonly Currency Usd = Currency.Usd;

    /// <inheritdoc />
    public string Name => "United States (NYSE/NASDAQ)";

    /// <inheritdoc />
    public Currency Currency => Usd;

    /// <inheritdoc />
    public bool Handles(Venue venue) => venue == Venue.Nasdaq || venue == Venue.Nyse;

    /// <inheritdoc />
    public Result<ChargesEstimate> Estimate(ChargeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Price.Currency != Usd)
        {
            return Result<ChargesEstimate>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"The US charge schedule levies in USD; this trade is priced in {context.Price.Currency}."));
        }

        var proceeds = Math.Abs(context.Turnover.Amount);
        var units = Math.Abs(context.Quantity.Value);
        var isSell = context.Side == Side.Sell;
        var isOption = context.Instrument.AssetClass == AssetClass.Option;
        var b = new ChargeBuilder(Usd);

        // --- commission ------------------------------------------------------------
        if (isOption)
        {
            b.Add(
                "Commission",
                units * OptionCommissionPerContractUsd,
                "Per contract. Charged even by zero-commission equity brokers.");
        }
        else
        {
            var perShare = units * EquityCommissionPerShareUsd;
            var commission = EquityCommissionPerShareUsd == 0m
                ? 0m
                : Math.Max(perShare, EquityCommissionMinimumUsd * context.OrderCount);

            b.Add(
                "Commission",
                commission,
                commission == 0m
                    ? "Zero-commission retail equity. The regulatory fees below are not zero."
                    : "Per share, subject to a per-order minimum.");
        }

        // --- SEC Section 31 fee, sells only ----------------------------------------
        b.Add(
            "SEC fee",
            isSell ? proceeds * SecFeeRate : 0m,
            isSell
                ? "Section 31 fee on sale proceeds. Rate is re-set by the SEC periodically."
                : "Charged on sales only.");

        // --- FINRA Trading Activity Fee, sells only --------------------------------
        var taf = !isSell
            ? 0m
            : isOption
                ? units * FinraTafPerOptionContractUsd
                : Math.Min(units * FinraTafPerShareUsd, FinraTafCapUsd);

        b.Add(
            "FINRA TAF",
            taf,
            isSell
                ? (isOption ? "Per contract sold." : "Per share sold, capped per trade.")
                : "Charged on sales only.");

        // --- Options regulatory fee, both sides ------------------------------------
        // Added only for options so an equity breakdown does not carry a permanently zero
        // line that reads as an unimplemented calculation.
        if (isOption)
        {
            b.Add(
                "Options regulatory fee",
                units * OptionsRegulatoryFeePerContractUsd,
                "Per contract, charged on both buys and sells. Varies by exchange.");
        }

        return b.Build();
    }
}
