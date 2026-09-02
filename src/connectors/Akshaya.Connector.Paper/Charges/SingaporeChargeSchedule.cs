using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Paper.Charges;

/// <summary>
/// SGX transaction costs.
///
/// Singapore's stack is short but has a feature India's does not: a MINIMUM brokerage. On
/// small tickets the minimum dominates everything else, which makes small-size strategies
/// structurally unprofitable in a way a percentage-only model completely hides. A S$1,000
/// trade below pays 1% in costs; the same model with brokerage at a flat 0.08% would say
/// 0.12% and be wrong by an order of magnitude.
///
/// WORKED EXAMPLE — buy 1,000 shares at S$2.50 (contract value S$2,500):
/// <code>
///   Brokerage           25.00   0.08% = 2.00, raised to the S$25.00 minimum
///   SGX clearing fee     0.81   0.0325% of 2,500
///   SGX trading fee      0.19   0.0075% of 2,500
///   Settlement fee       0.35   flat per contract
///   GST                  2.37   9% of (25.00 + 0.81 + 0.19 + 0.35)
///   ---------------------------------
///   Total               28.72   (1.15% of contract value)
/// </code>
/// The same trade at 100,000 shares (S$250,000): brokerage 200.00, clearing 81.25 (below the
/// cap), trading 18.75, settlement 0.35, GST 27.03 — total 327.38, or 0.13%. The order of
/// magnitude between the two is the whole reason the minimum is modelled explicitly.
/// </summary>
public sealed class SingaporeChargeSchedule : IChargeSchedule
{
    // ---------------------------------------------------------------------------------
    // BROKERAGE. Broker-set. Singapore retail brokerage is tiered by contract value with a
    // hard minimum; the single-rate-plus-minimum below models the common online tier and is
    // the block a deployment is expected to override.
    // ---------------------------------------------------------------------------------

    /// <summary>Commission rate on contract value for the online tier.
    /// REVIEW: verify against the published schedule, last checked 2026-09. Singapore
    /// brokerage ranges from roughly 0.08% at the discount end to 0.28% at full-service
    /// brokers, and is tiered by contract value at most of them — this single rate is an
    /// approximation of one tier, not a schedule.</summary>
    public const decimal BrokerageRate = 0.0008m; // 0.08%

    /// <summary>Minimum commission per executed order, in Singapore dollars.
    /// REVIEW: verify against the published schedule, last checked 2026-09. Commonly S$10 at
    /// discount brokers and S$25 at bank-owned ones; S$25 is the conservative choice.</summary>
    public const decimal MinimumBrokerageSgd = 25m;

    // ---------------------------------------------------------------------------------
    // EXCHANGE AND DEPOSITORY FEES. Set by SGX / CDP, identical whoever routed the order.
    // ---------------------------------------------------------------------------------

    /// <summary>SGX clearing fee on contract value.
    /// REVIEW: verify against the published SGX fee schedule, last checked 2026-09.</summary>
    public const decimal ClearingFeeRate = 0.000325m; // 0.0325%

    /// <summary>Cap on the clearing fee per contract, in Singapore dollars. Only bites on very
    /// large tickets, but a block-trading strategy backtested without it is over-charged.
    /// REVIEW: verify against the published SGX fee schedule, last checked 2026-09.</summary>
    public const decimal ClearingFeeCapSgd = 600m;

    /// <summary>SGX trading (access) fee on contract value.
    /// REVIEW: verify against the published SGX fee schedule, last checked 2026-09.</summary>
    public const decimal TradingFeeRate = 0.000075m; // 0.0075%

    /// <summary>CDP settlement instruction fee, flat per contract.
    /// REVIEW: verify against the published CDP schedule, last checked 2026-09. This one I am
    /// least confident of: it is charged per settlement instruction rather than per execution,
    /// and brokers differ on whether they pass it through at all. Treat as indicative.</summary>
    public const decimal SettlementFeeSgd = 0.35m;

    /// <summary>Goods and services tax on brokerage and exchange fees. Raised to 9% on
    /// 2024-01-01; anything quoting 7% or 8% predates that.
    /// REVIEW: verify the prevailing rate, last checked 2026-09.</summary>
    public const decimal GstRate = 0.09m;

    private static readonly Currency Sgd = Currency.Sgd;

    /// <inheritdoc />
    public string Name => "Singapore (SGX)";

    /// <inheritdoc />
    public Currency Currency => Sgd;

    /// <inheritdoc />
    public bool Handles(Venue venue) => venue == Venue.Sgx;

    /// <inheritdoc />
    public Result<ChargesEstimate> Estimate(ChargeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Price.Currency != Sgd)
        {
            // SGX lists in several currencies; this schedule only models the SGD board lot.
            return Result<ChargesEstimate>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"The Singapore charge schedule levies in SGD; this trade is priced in {context.Price.Currency}. "
                + "SGX's non-SGD boards are not modelled."));
        }

        var value = Math.Abs(context.Turnover.Amount);
        var b = new ChargeBuilder(Sgd);

        // --- brokerage, with the minimum that dominates small tickets ---------------
        var computed = value * BrokerageRate;
        var minimum = MinimumBrokerageSgd * context.OrderCount;
        var brokerage = Math.Max(computed, minimum);

        b.Add(
            BrokerageLine,
            brokerage,
            brokerage > computed
                ? "0.08% of contract value, raised to the S$25.00 per-order minimum."
                : "0.08% of contract value.");

        // --- exchange fees, symmetric between buy and sell -------------------------
        b.Add(
            ClearingLine,
            Math.Min(value * ClearingFeeRate, ClearingFeeCapSgd),
            "0.0325% of contract value, capped at S$600 per contract.");

        b.Add(TradingLine, value * TradingFeeRate, "0.0075% of contract value.");

        b.Add(SettlementLine, SettlementFeeSgd * context.OrderCount, "Flat CDP settlement instruction fee.");

        // --- GST on the whole fee stack --------------------------------------------
        // Unlike India, Singapore's GST base is brokerage AND the exchange fees, so all four
        // named lines go in. Named explicitly for the same reason as India: so a line added
        // later cannot widen the tax base by accident.
        b.Add(
            "GST",
            b.SumOf(BrokerageLine, ClearingLine, TradingLine, SettlementLine) * GstRate,
            "9% on brokerage and exchange fees.");

        return b.Build();
    }

    private const string BrokerageLine = "Brokerage";
    private const string ClearingLine = "SGX clearing fee";
    private const string TradingLine = "SGX trading fee";
    private const string SettlementLine = "Settlement fee";
}
