using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Connector.Paper.Charges;

/// <summary>
/// Indian equity and F&amp;O transaction costs for NSE and BSE.
///
/// India has the most layered cost stack of the three markets modelled here — six statutory
/// components on top of brokerage, three of them asymmetric between buy and sell — and it is
/// the one where ignoring costs distorts a backtest most. An intraday equity strategy in
/// India pays securities transaction tax on every sell; a strategy that round-trips a hundred
/// times a day and is backtested without it is not a strategy, it is an arithmetic error.
///
/// WORKED EXAMPLE — intraday sell, 100 shares of an NSE equity at ₹1,500 (turnover ₹150,000):
/// <code>
///   Brokerage                     20.00   flat per executed order, capped at 0.03% (=45.00)
///   STT                           37.50   0.025% of 150,000, sell side only
///   Exchange transaction charges   4.46   0.00297% of 150,000
///   SEBI turnover fee              0.15   ₹10 per crore
///   Stamp duty                     0.00   buy side only; this is a sell
///   GST                            4.40   18% of (20.00 + 4.46)
///   DP charges                     0.00   delivery sells only; this is intraday
///   ------------------------------------
///   Total                         66.51
/// </code>
/// The same trade as a DELIVERY sell instead: brokerage 0.00 (delivery is free on the plan
/// modelled), STT 150.00 (0.1%), transaction charges 4.46, SEBI 0.15, stamp 0.00, GST 0.80,
/// DP 15.93 — total 171.34. The four-fold STT difference between the two is why
/// <see cref="ChargeContext.PositionEffect"/> is a required field and not a hint.
/// </summary>
public sealed class IndiaChargeSchedule : IChargeSchedule
{
    // ---------------------------------------------------------------------------------
    // BROKERAGE. Broker-set, not statutory, so this is the one block a deployment is
    // expected to override. The defaults model the common Indian discount-broker plan:
    // free delivery, flat ₹20 per executed intraday or F&O order subject to a percentage cap.
    // ---------------------------------------------------------------------------------

    /// <summary>Flat brokerage per executed order, in rupees, for intraday and F&amp;O.
    /// REVIEW: verify against the published schedule, last checked 2026-09. Discount brokers
    /// cluster at ₹20 but several charge ₹15 or ₹0 on selected segments.</summary>
    public const decimal FlatBrokeragePerOrderInr = 20m;

    /// <summary>Percentage cap on brokerage; the charge is the lesser of the flat fee and this.
    /// REVIEW: verify against the published schedule, last checked 2026-09.</summary>
    public const decimal BrokerageCapRate = 0.0003m; // 0.03% of turnover

    /// <summary>Brokerage on delivery equity. Zero on the plan modelled here.
    /// REVIEW: verify against the published schedule, last checked 2026-09 — several brokers
    /// that were free on delivery have since introduced a flat charge.</summary>
    public const decimal DeliveryBrokerageInr = 0m;

    // ---------------------------------------------------------------------------------
    // SECURITIES TRANSACTION TAX (STT) / COMMODITIES TRANSACTION TAX (CTT). Statutory,
    // set by the Finance Act. The rates below reflect the changes effective 1 October 2024;
    // several of them had been at half these levels for years before that, so any figure
    // copied from an older article will be wrong.
    // ---------------------------------------------------------------------------------

    /// <summary>STT on delivery equity, charged on BOTH buy and sell.
    /// REVIEW: verify against the published schedule, last checked 2026-09.</summary>
    public const decimal SttDeliveryRate = 0.001m; // 0.1% each side

    /// <summary>STT on intraday equity, charged on the SELL side only.
    /// REVIEW: verify against the published schedule, last checked 2026-09.</summary>
    public const decimal SttIntradaySellRate = 0.00025m; // 0.025%

    /// <summary>STT on equity futures, sell side only. Raised from 0.0125% on 2024-10-01.
    /// REVIEW: verify against the published schedule, last checked 2026-09.</summary>
    public const decimal SttFuturesSellRate = 0.0002m; // 0.02%

    /// <summary>STT on equity options, charged on the PREMIUM, sell side only. Raised from
    /// 0.0625% on 2024-10-01.
    /// REVIEW: verify against the published schedule, last checked 2026-09.</summary>
    public const decimal SttOptionsSellPremiumRate = 0.001m; // 0.1% of premium

    /// <summary>CTT on non-agricultural commodity futures, sell side only.
    /// REVIEW: verify against the published schedule, last checked 2026-09. Agricultural
    /// commodities are exempt and this model does not distinguish them — a commodity
    /// backtest on agri contracts will be over-charged here.</summary>
    public const decimal CttCommodityFuturesSellRate = 0.0001m; // 0.01%

    // ---------------------------------------------------------------------------------
    // EXCHANGE TRANSACTION CHARGES. Set by the exchange, differ by exchange AND segment,
    // and were revised in 2024 when SEBI required uniform (non-slab) charges.
    // ---------------------------------------------------------------------------------

    /// <summary>NSE cash-segment transaction charge.
    /// REVIEW: verify against the published circular, last checked 2026-09.</summary>
    public const decimal NseEquityTransactionRate = 0.0000297m; // 0.00297%

    /// <summary>BSE cash-segment transaction charge. BSE's equity rate is close to NSE's but
    /// not identical, and its derivative rates differ substantially.
    /// REVIEW: verify against the published circular, last checked 2026-09 — this is the rate
    /// I am least confident of in this file.</summary>
    public const decimal BseEquityTransactionRate = 0.0000375m; // 0.00375%

    /// <summary>Equity futures transaction charge.
    /// REVIEW: verify against the published circular, last checked 2026-09.</summary>
    public const decimal FuturesTransactionRate = 0.0000173m; // 0.00173%

    /// <summary>Options transaction charge, levied on PREMIUM turnover, not notional.
    /// REVIEW: verify against the published circular, last checked 2026-09.</summary>
    public const decimal OptionsTransactionPremiumRate = 0.0003503m; // 0.03503% of premium

    /// <summary>Commodity futures transaction charge. Varies by commodity group in reality;
    /// this models the common non-agri rate.
    /// REVIEW: verify against the published circular, last checked 2026-09. Treat as
    /// indicative only.</summary>
    public const decimal CommodityTransactionRate = 0.000026m; // 0.0026%

    // ---------------------------------------------------------------------------------
    // REGULATOR AND STATE LEVIES.
    // ---------------------------------------------------------------------------------

    /// <summary>SEBI turnover fee: ₹10 per crore of turnover, both sides, every segment.
    /// REVIEW: verify against the published schedule, last checked 2026-09.</summary>
    public const decimal SebiTurnoverRate = 0.000001m; // ₹10 / 1,00,00,000

    /// <summary>Stamp duty on delivery equity, BUY side only. Uniform across states since 2020.
    /// REVIEW: verify against the published schedule, last checked 2026-09.</summary>
    public const decimal StampDutyDeliveryBuyRate = 0.00015m; // 0.015%

    /// <summary>Stamp duty on intraday equity, buy side only.
    /// REVIEW: verify against the published schedule, last checked 2026-09.</summary>
    public const decimal StampDutyIntradayBuyRate = 0.00003m; // 0.003%

    /// <summary>Stamp duty on futures, buy side only.
    /// REVIEW: verify against the published schedule, last checked 2026-09.</summary>
    public const decimal StampDutyFuturesBuyRate = 0.00002m; // 0.002%

    /// <summary>Stamp duty on options premium, buy side only.
    /// REVIEW: verify against the published schedule, last checked 2026-09.</summary>
    public const decimal StampDutyOptionsBuyRate = 0.00003m; // 0.003%

    /// <summary>
    /// GST rate applied to brokerage and exchange transaction charges.
    ///
    /// NOTE ON THE BASE: this schedule applies GST to (brokerage + transaction charges), which
    /// is what the platform contract specifies. In practice most Indian brokers ALSO include
    /// the SEBI turnover fee in the GST base. The difference is a fraction of a rupee on a
    /// retail-sized trade, but it is a known, deliberate simplification rather than an
    /// oversight — do not "fix" it without changing the contract note too.
    /// REVIEW: verify the rate and the base, last checked 2026-09.
    /// </summary>
    public const decimal GstRate = 0.18m; // 18%

    /// <summary>
    /// Depository participant charge on a DELIVERY SELL, per scrip per day, flat regardless of
    /// quantity. Broker-set on top of a depository floor, so it varies more than anything else
    /// in this file.
    ///
    /// Modelled per EXECUTION here, not per scrip per day, because the engine charges each
    /// fill independently and has no concept of a trading day's netting. A strategy that sells
    /// the same scrip in five clips will be over-charged by roughly ₹64 against a real
    /// statement. That is the conservative direction and it is documented rather than hidden.
    /// REVIEW: verify against the published schedule, last checked 2026-09.
    /// </summary>
    public const decimal DpChargePerDeliverySellInr = 15.93m;

    private static readonly Currency Inr = Currency.Inr;

    /// <inheritdoc />
    public string Name => "India (NSE/BSE)";

    /// <inheritdoc />
    public Currency Currency => Inr;

    /// <inheritdoc />
    public bool Handles(Venue venue) => venue == Venue.Nse || venue == Venue.Bse;

    /// <inheritdoc />
    public Result<ChargesEstimate> Estimate(ChargeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Price.Currency != Inr)
        {
            // A rupee fee on a non-rupee trade would be silently wrong in the total. Refuse.
            return Result<ChargesEstimate>.Failure(new Error(
                ConnectorErrorCodes.InvalidRequest,
                $"The India charge schedule levies in INR; this trade is priced in {context.Price.Currency}."));
        }

        var turnover = Math.Abs(context.Turnover.Amount);
        var isSell = context.Side == Side.Sell;
        var isBuy = !isSell;
        var b = new ChargeBuilder(Inr);

        var assetClass = context.Instrument.AssetClass;
        var isOption = assetClass == AssetClass.Option;
        var isFuture = assetClass == AssetClass.Future;
        var isCommodity = assetClass == AssetClass.Commodity;

        // Only cash equity and ETFs settle into a demat account, so only they get the
        // free-delivery brokerage and the DP charge. AssetClass.Commodity is its own class
        // here rather than a Future, which is why InstrumentKey.IsDerivative is not enough.
        var isCashEquity = assetClass is AssetClass.Equity or AssetClass.Etf;

        // For options every statutory rate is levied on PREMIUM turnover, which is what
        // price x quantity x multiplier already is. Notional (strike x lot) never enters.

        // --- brokerage -------------------------------------------------------------
        var freeDelivery = context.IsDelivery && isCashEquity;

        var brokerage = freeDelivery
            ? DeliveryBrokerageInr * context.OrderCount
            : Math.Min(FlatBrokeragePerOrderInr, turnover * BrokerageCapRate) * context.OrderCount;

        b.Add(BrokerageLine, brokerage, freeDelivery
            ? "Delivery equity is free on the modelled plan."
            : "Flat per executed order, capped at 0.03% of turnover.");

        // --- STT / CTT -------------------------------------------------------------
        var (sttRate, sttNote) = (isOption, isFuture, isCommodity, context.IsDelivery, isSell) switch
        {
            (true, _, _, _, true) => (SttOptionsSellPremiumRate, "0.1% of option premium, sell side."),
            (true, _, _, _, false) => (0m, "Options STT is charged on the sell side only."),
            (_, true, _, _, true) when !isCommodity => (SttFuturesSellRate, "0.02% of futures turnover, sell side."),
            (_, true, _, _, false) when !isCommodity => (0m, "Futures STT is charged on the sell side only."),
            (_, _, true, _, true) => (CttCommodityFuturesSellRate, "CTT 0.01%, sell side. Agri contracts are exempt and are not distinguished here."),
            (_, _, true, _, false) => (0m, "CTT is charged on the sell side only."),
            (_, _, _, true, _) => (SttDeliveryRate, "0.1% on delivery, charged on both buy and sell."),
            (_, _, _, false, true) => (SttIntradaySellRate, "0.025% on intraday, sell side only."),
            _ => (0m, "Intraday STT is charged on the sell side only."),
        };

        b.Add(isCommodity ? "CTT" : SttLine, turnover * sttRate, sttNote);

        // --- exchange transaction charges ------------------------------------------
        var exchangeRate = (isOption, isFuture, isCommodity) switch
        {
            (true, _, _) => OptionsTransactionPremiumRate,
            (_, true, _) when !isCommodity => FuturesTransactionRate,
            (_, _, true) => CommodityTransactionRate,
            _ => context.Instrument.Venue == Venue.Bse
                ? BseEquityTransactionRate
                : NseEquityTransactionRate,
        };

        b.Add(TransactionLine, turnover * exchangeRate, "Set by the exchange; segment-specific.");

        // --- SEBI turnover fee -----------------------------------------------------
        b.Add("SEBI turnover fee", turnover * SebiTurnoverRate, "₹10 per crore of turnover.");

        // --- stamp duty (buy side only, everywhere) --------------------------------
        var stampRate = !isBuy
            ? 0m
            : (isOption, isFuture || isCommodity, context.IsDelivery) switch
            {
                (true, _, _) => StampDutyOptionsBuyRate,
                (_, true, _) => StampDutyFuturesBuyRate,
                (_, _, true) => StampDutyDeliveryBuyRate,
                _ => StampDutyIntradayBuyRate,
            };

        b.Add(
            "Stamp duty",
            turnover * stampRate,
            isBuy ? "Levied on the buy side, uniform across states since 2020." : "Buy side only.");

        // --- GST on brokerage + transaction charges --------------------------------
        // Computed from the named lines rather than the running total so that adding a line
        // later cannot silently widen the tax base.
        b.Add(
            "GST",
            b.SumOf(BrokerageLine, TransactionLine) * GstRate,
            "18% on brokerage and exchange transaction charges.");

        // --- DP charges (delivery sells only) --------------------------------------
        var dp = isSell && context.IsDelivery && isCashEquity
            ? DpChargePerDeliverySellInr
            : 0m;

        b.Add(
            "DP charges",
            dp,
            dp > 0m
                ? "Flat per delivery sell, independent of quantity. Charged per execution here, not netted per scrip per day."
                : "Charged on delivery sells only.");

        return b.Build();
    }

    private const string BrokerageLine = "Brokerage";
    private const string SttLine = "STT";
    private const string TransactionLine = "Exchange transaction charges";
}
