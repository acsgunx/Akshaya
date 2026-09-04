using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akshaya.Connector.MStock;

/// <summary>
/// Wire shapes for the mStock Type A REST API.
///
/// These are all <c>internal</c> and all deliberately dumb: every field is nullable and no
/// field is validated here. Vendor payloads drift — a field that has been an integer for two
/// years turns into a string, an object turns into an array of one — and a DTO that throws on
/// deserialisation takes the whole order book down with it. Validation happens in the facets,
/// where a missing field can be turned into a proper <c>Result</c> failure that names it.
///
/// Timestamps are carried as strings and parsed by <see cref="MStockTime"/>, because mStock
/// sends naive local datetimes ("2025-09-01 09:15:04") with no offset, and letting
/// System.Text.Json bind those to a DateTimeOffset silently stamps them with the SERVER's
/// offset — which turns every timestamp on a UTC container into a 5.5-hour lie.
/// </summary>
internal static class MStockJson
{
    /// <summary>
    /// Shared serialiser settings. <see cref="JsonNumberHandling.AllowReadingFromString"/> is
    /// the important one: mStock quotes numbers inconsistently across endpoints (prices come
    /// back as <c>1560.5</c> from the quote route and <c>"1560.50"</c> from the order book).
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}

/// <summary>
/// The envelope every Type A route wraps its payload in. A 200 response with
/// <c>status: "error"</c> is mStock's normal way of reporting a business failure, so the
/// envelope must be inspected even on HTTP success.
/// </summary>
/// <typeparam name="T">The payload shape.</typeparam>
internal sealed class MStockEnvelope<T>
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>mStock's error taxonomy: TokenException, InputException, NetworkException, ...</summary>
    [JsonPropertyName("error_type")]
    public string? ErrorType { get; init; }

    /// <summary>Some routes use <c>errorcode</c> instead of <c>error_type</c>.</summary>
    [JsonPropertyName("errorcode")]
    public string? ErrorCode { get; init; }

    [JsonIgnore]
    public bool IsSuccess =>
        Status is null || string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase);
}

// --- authentication ---------------------------------------------------------------------

/// <summary>
/// The first login leg's payload, as mStock actually sends it:
///
/// <code>
/// {"status":"success","data":{
///    "ugid":"5544454f-…","is_kyc":true,"is_activate":false,"is_password_reset":true,
///    "is_error":false,"cid":"1111","nm":"","flag":0}}
/// </code>
///
/// EVERY FIELD IS OPTIONAL AND LENIENTLY TYPED, on purpose. This connector needs exactly one
/// value out of the eight above — <see cref="Ugid"/> — and an earlier version of this class
/// declared the three <c>is_*</c> flags as strings, which mStock sends as bare booleans. The
/// result was a login that failed with "the broker's response could not be understood" while
/// the broker had in fact said "success": a field nothing reads killed the whole document,
/// because System.Text.Json treats one type mismatch as fatal for all of it.
///
/// The rule this class now follows: a field the connector does not act on must never be able
/// to fail a login. See docs/connectors/mstock-login-response.md.
/// </summary>
internal sealed class MStockLoginData
{
    /// <summary>Opaque login identifier some builds require to be echoed on the token call.</summary>
    [JsonPropertyName("ugid")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Ugid { get; init; }

    /// <summary>Client code. Quoted on some builds ("1111"), bare on others.</summary>
    [JsonPropertyName("cid")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? ClientId { get; init; }

    /// <summary>Account nickname. Sent as <c>nm</c>; <see cref="NickNameLong"/> covers the older key.</summary>
    [JsonPropertyName("nm")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? NickName { get; init; }

    /// <summary>
    /// The <c>nick_name</c> spelling. Kept because this connector was originally written
    /// against it and there is no way to tell which builds still send it.
    /// </summary>
    [JsonPropertyName("nick_name")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? NickNameLong { get; init; }

    /// <summary>
    /// Masked mobile the OTP went to, when the build sends one.
    ///
    /// OFTEN ABSENT — the observed payload has no such field at all. The wizard therefore has
    /// to treat "we cannot tell you where the code went" as normal and simply not show a
    /// destination, rather than failing or inventing one.
    /// </summary>
    [JsonPropertyName("mobile")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? MaskedMobile { get; init; }

    [JsonPropertyName("is_kyc")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool? IsKyc { get; init; }

    [JsonPropertyName("is_activate")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool? IsActivated { get; init; }

    [JsonPropertyName("is_password_reset")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool? IsPasswordReset { get; init; }

    /// <summary>
    /// mStock's own in-payload error flag, alongside the envelope's <c>status</c>.
    ///
    /// Not currently branched on: the envelope's status is the authority, and trusting a second
    /// disagreeing signal would make "did this succeed" ambiguous. Mapped so it is visible in a
    /// debugger and in logs when a support question needs answering.
    /// </summary>
    [JsonPropertyName("is_error")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool? IsError { get; init; }

    /// <summary>Undocumented status/sequence value. Mapped only so it cannot break the parse.</summary>
    [JsonPropertyName("flag")]
    [JsonConverter(typeof(LenientIntConverter))]
    public int? Flag { get; init; }

    /// <summary>The best available label for the account, whichever key the build used.</summary>
    [JsonIgnore]
    public string? DisplayName =>
        !string.IsNullOrWhiteSpace(NickName) ? NickName
        : !string.IsNullOrWhiteSpace(NickNameLong) ? NickNameLong
        : null;
}

internal sealed class MStockSessionData
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; init; }

    [JsonPropertyName("user_name")]
    public string? UserName { get; init; }

    [JsonPropertyName("user_shortname")]
    public string? UserShortName { get; init; }

    [JsonPropertyName("user_type")]
    public string? UserType { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("broker")]
    public string? Broker { get; init; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("public_token")]
    public string? PublicToken { get; init; }

    /// <summary>Token the streaming socket authenticates with. Not interchangeable with the access token.</summary>
    [JsonPropertyName("enctoken")]
    public string? EncToken { get; init; }

    [JsonPropertyName("login_time")]
    public string? LoginTime { get; init; }

    /// <summary>Exchanges this login is entitled to. Enforced before we send an order there.</summary>
    [JsonPropertyName("exchanges")]
    public IReadOnlyList<string>? Exchanges { get; init; }

    [JsonPropertyName("products")]
    public IReadOnlyList<string>? Products { get; init; }

    [JsonPropertyName("order_types")]
    public IReadOnlyList<string>? OrderTypes { get; init; }
}

// --- orders -----------------------------------------------------------------------------

/// <summary>Order placement body. Field names are mStock's, not ours.</summary>
internal sealed class MStockPlaceOrderRequest
{
    [JsonPropertyName("tradingsymbol")]
    public required string TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public required string Exchange { get; init; }

    [JsonPropertyName("transaction_type")]
    public required string TransactionType { get; init; }

    [JsonPropertyName("order_type")]
    public required string OrderType { get; init; }

    [JsonPropertyName("quantity")]
    public required string Quantity { get; init; }

    [JsonPropertyName("product")]
    public required string Product { get; init; }

    [JsonPropertyName("validity")]
    public required string Validity { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("trigger_price")]
    public string? TriggerPrice { get; init; }

    [JsonPropertyName("disclosed_quantity")]
    public string? DisclosedQuantity { get; init; }

    /// <summary>
    /// mStock's only free-text correlation field, and it is short. See
    /// <see cref="MStockOrders"/> for how the platform's ClientOrderId is folded into it.
    /// </summary>
    /// <summary>
    /// Client tag. A string in the placement request, <c>[]</c> in the order book and
    /// <c>null</c> in /order/details — see LenientStringOrArrayConverter.
    /// </summary>
    [JsonPropertyName("tag")]
    [JsonConverter(typeof(LenientStringOrArrayConverter))]
    public string? Tag { get; init; }

    /// <summary>
    /// Whether the order has been modified. <c>"false"</c> (a string) from the order book and
    /// <c>0</c> (a number) from /order/details, for the same field. Nothing branches on it;
    /// mapped leniently so neither shape can fail the parse.
    /// </summary>
    [JsonPropertyName("modified")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool? Modified { get; init; }

    /// <summary>
    /// The wire form. Placement is documented as <c>application/x-www-form-urlencoded</c>, so
    /// this — not the JSON attributes above — is what actually goes to mStock. The attributes
    /// stay because the same type is bound back from recorded fixtures in the tests, and
    /// because keeping one field-name declaration is the whole point of this file.
    ///
    /// Null members are OMITTED rather than sent empty. A <c>price=</c> on a MARKET order is
    /// not the same as no price at all: some exchange gateways read an empty protection price
    /// as zero and reject the order.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToForm()
    {
        var form = new List<KeyValuePair<string, string>>(11)
        {
            new("tradingsymbol", TradingSymbol),
            new("exchange", Exchange),
            new("transaction_type", TransactionType),
            new("order_type", OrderType),
            new("quantity", Quantity),
            new("product", Product),
            new("validity", Validity),
        };

        MStockForm.AddIfPresent(form, "price", Price);
        MStockForm.AddIfPresent(form, "trigger_price", TriggerPrice);
        MStockForm.AddIfPresent(form, "disclosed_quantity", DisclosedQuantity);
        MStockForm.AddIfPresent(form, "tag", Tag);

        return form;
    }
}

/// <summary>
/// Order amendment body.
///
/// WIDER THAN THE FIELDS BEING CHANGED, ON PURPOSE. mStock's modify route documents the full
/// order context — variety, tradingsymbol, exchange, transaction_type, product — alongside the
/// mutable fields, and it is a replace rather than a patch. Sending only the changed fields
/// invites the broker to fill the rest from its own defaults, which is how a CNC order becomes
/// an MIS order on a price amendment. <see cref="MStockOrders.ModifyAsync"/> therefore reads
/// the live order first and carries the unchanged values through verbatim.
/// </summary>
internal sealed class MStockModifyOrderRequest
{
    [JsonPropertyName("order_id")]
    public required string OrderId { get; init; }

    [JsonPropertyName("variety")]
    public string? Variety { get; init; }

    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("transaction_type")]
    public string? TransactionType { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    [JsonPropertyName("quantity")]
    public string? Quantity { get; init; }

    [JsonPropertyName("order_type")]
    public string? OrderType { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("trigger_price")]
    public string? TriggerPrice { get; init; }

    [JsonPropertyName("disclosed_quantity")]
    public string? DisclosedQuantity { get; init; }

    [JsonPropertyName("validity")]
    public string? Validity { get; init; }

    /// <summary>
    /// Remaining (unfilled) quantity, which mStock wants alongside the new total on a
    /// part-filled order. Derived from the order book rather than from the caller.
    /// </summary>
    [JsonPropertyName("modqty_remng")]
    public string? RemainingQuantity { get; init; }

    /// <summary>Form-encoded wire shape; see <see cref="MStockPlaceOrderRequest.ToForm"/>.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToForm()
    {
        var form = new List<KeyValuePair<string, string>>(12);

        MStockForm.AddIfPresent(form, "variety", Variety);
        MStockForm.AddIfPresent(form, "tradingsymbol", TradingSymbol);
        MStockForm.AddIfPresent(form, "exchange", Exchange);
        MStockForm.AddIfPresent(form, "transaction_type", TransactionType);
        MStockForm.AddIfPresent(form, "product", Product);
        MStockForm.AddIfPresent(form, "order_type", OrderType);
        MStockForm.AddIfPresent(form, "quantity", Quantity);
        MStockForm.AddIfPresent(form, "price", Price);
        MStockForm.AddIfPresent(form, "trigger_price", TriggerPrice);
        MStockForm.AddIfPresent(form, "disclosed_quantity", DisclosedQuantity);
        MStockForm.AddIfPresent(form, "validity", Validity);
        MStockForm.AddIfPresent(form, "modqty_remng", RemainingQuantity);

        return form;
    }
}

/// <summary>Shared helper so no DTO invents its own "skip the nulls" rule.</summary>
internal static class MStockForm
{
    public static void AddIfPresent(List<KeyValuePair<string, string>> form, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            form.Add(new KeyValuePair<string, string>(key, value));
        }
    }
}

internal sealed class MStockOrderIdData
{
    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    /// <summary>Some builds spell it <c>order_no</c>; both are accepted.</summary>
    [JsonPropertyName("order_no")]
    public string? OrderNo { get; init; }

    [JsonIgnore]
    public string? Any => OrderId ?? OrderNo;
}

/// <summary>
/// The cancel-all response.
///
/// mStock DOES NOT REPORT A COUNT. The documented success payload is a single
/// <c>{"order_id": "..."}</c> — the same shape as an ordinary cancel — and says nothing about
/// how many orders the sweep actually reached. <c>cancelled_orders</c> and <c>count</c> are
/// kept because some builds have been seen to send them, but neither may be relied on.
///
/// This matters more than it looks. <see cref="MStockOrders.CancelAllAsync"/> previously read
/// only those two fields, so a *successful* sweep of nine orders deserialised to zero and was
/// reported to the trader as "0 cancelled" — indistinguishable from a sweep that did nothing.
/// The connector now returns <see cref="UnknownCount"/> when the broker declines to say, and
/// the caller counts the working orders that actually disappeared from the book instead.
/// </summary>
internal sealed class MStockCancelAllData
{
    /// <summary>Sentinel for "the broker acknowledged the sweep but did not say how many".</summary>
    public const int UnknownCount = -1;

    [JsonPropertyName("cancelled_orders")]
    public IReadOnlyList<string>? CancelledOrders { get; init; }

    [JsonPropertyName("count")]
    public int? Count { get; init; }

    /// <summary>The documented shape: one order id, no count.</summary>
    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    /// <summary>
    /// How many orders the broker claims to have cancelled, or <see cref="UnknownCount"/>
    /// when it did not say. Never silently zero — see the type's remarks.
    /// </summary>
    [JsonIgnore]
    public int ReportedCount => Count
                                ?? CancelledOrders?.Count
                                ?? UnknownCount;
}

// --- margin and charges -----------------------------------------------------------------

/// <summary>
/// Body for <c>POST /openapi/typea/margins/orders</c>.
///
/// JSON, not form-encoded — this is the one documented write route on the Type A surface that
/// takes a JSON body, and mStock's own cURL sample sets <c>Content-Type: application/json</c>
/// even though the surrounding prose says otherwise.
/// </summary>
internal sealed class MStockMarginRequest
{
    [JsonPropertyName("exchange")]
    public required string Exchange { get; init; }

    [JsonPropertyName("tradingsymbol")]
    public required string TradingSymbol { get; init; }

    [JsonPropertyName("transaction_type")]
    public required string TransactionType { get; init; }

    [JsonPropertyName("variety")]
    public required string Variety { get; init; }

    [JsonPropertyName("product")]
    public required string Product { get; init; }

    [JsonPropertyName("order_type")]
    public required string OrderType { get; init; }

    [JsonPropertyName("quantity")]
    public required decimal Quantity { get; init; }

    /// <summary>Zero on a market order — this route wants the field present, unlike placement.</summary>
    [JsonPropertyName("price")]
    public decimal Price { get; init; }

    [JsonPropertyName("trigger_price")]
    public decimal TriggerPrice { get; init; }
}

/// <summary>
/// The margin calculator's reply: a margin breakdown plus a full itemised charge schedule.
///
/// <c>total</c> is the capital the broker will block. It is NOT the sum of the charges — the
/// charge lines are a separate, additional estimate, and adding the two together would
/// overstate the cost of every trade.
/// </summary>
internal sealed class MStockMarginData
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("span")]
    public decimal? Span { get; init; }

    [JsonPropertyName("exposure")]
    public decimal? Exposure { get; init; }

    [JsonPropertyName("option_premium")]
    public decimal? OptionPremium { get; init; }

    [JsonPropertyName("additional")]
    public decimal? Additional { get; init; }

    [JsonPropertyName("bo")]
    public decimal? BracketOrder { get; init; }

    [JsonPropertyName("cash")]
    public decimal? Cash { get; init; }

    [JsonPropertyName("var")]
    public decimal? Var { get; init; }

    [JsonPropertyName("leverage")]
    public decimal? Leverage { get; init; }

    [JsonPropertyName("charges")]
    public MStockChargesDto? Charges { get; init; }

    /// <summary>Total margin blocked.</summary>
    [JsonPropertyName("total")]
    public decimal? Total { get; init; }
}

internal sealed class MStockChargesDto
{
    [JsonPropertyName("transaction_tax")]
    public decimal? TransactionTax { get; init; }

    /// <summary>"stt" on equity, "ctt" on commodities — the label for the line above.</summary>
    [JsonPropertyName("transaction_tax_type")]
    public string? TransactionTaxType { get; init; }

    [JsonPropertyName("exchange_turnover_charge")]
    public decimal? ExchangeTurnoverCharge { get; init; }

    [JsonPropertyName("sebi_turnover_charge")]
    public decimal? SebiTurnoverCharge { get; init; }

    [JsonPropertyName("brokerage")]
    public decimal? Brokerage { get; init; }

    [JsonPropertyName("stamp_duty")]
    public decimal? StampDuty { get; init; }

    [JsonPropertyName("gst")]
    public MStockGstDto? Gst { get; init; }

    [JsonPropertyName("total")]
    public decimal? Total { get; init; }
}

internal sealed class MStockGstDto
{
    [JsonPropertyName("igst")]
    public decimal? Igst { get; init; }

    [JsonPropertyName("cgst")]
    public decimal? Cgst { get; init; }

    [JsonPropertyName("sgst")]
    public decimal? Sgst { get; init; }

    [JsonPropertyName("total")]
    public decimal? Total { get; init; }
}

// --- position conversion ------------------------------------------------------------------

/// <summary>
/// Body for <c>POST /openapi/typea/portfolio/convertposition</c>: moves an open position from
/// one margin product to another (the intraday-to-delivery rescue, most often).
/// </summary>
internal sealed class MStockConvertPositionRequest
{
    [JsonPropertyName("tradingsymbol")]
    public required string TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public required string Exchange { get; init; }

    [JsonPropertyName("transaction_type")]
    public required string TransactionType { get; init; }

    /// <summary>mStock documents exactly one value here: <c>DAY</c>.</summary>
    [JsonPropertyName("position_type")]
    public required string PositionType { get; init; }

    [JsonPropertyName("quantity")]
    public required string Quantity { get; init; }

    [JsonPropertyName("old_product")]
    public required string OldProduct { get; init; }

    [JsonPropertyName("new_product")]
    public required string NewProduct { get; init; }

    /// <summary>Form-encoded wire shape; see <see cref="MStockPlaceOrderRequest.ToForm"/>.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToForm() =>
    [
        new("tradingsymbol", TradingSymbol),
        new("exchange", Exchange),
        new("transaction_type", TransactionType),
        new("position_type", PositionType),
        new("quantity", Quantity),
        new("old_product", OldProduct),
        new("new_product", NewProduct),
    ];
}

internal sealed class MStockOrderDto
{
    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    [JsonPropertyName("exchange_order_id")]
    public string? ExchangeOrderId { get; init; }

    [JsonPropertyName("parent_order_id")]
    public string? ParentOrderId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("status_message")]
    public string? StatusMessage { get; init; }

    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("instrument_token")]
    public long? InstrumentToken { get; init; }

    [JsonPropertyName("transaction_type")]
    public string? TransactionType { get; init; }

    [JsonPropertyName("order_type")]
    public string? OrderType { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    [JsonPropertyName("validity")]
    public string? Validity { get; init; }

    [JsonPropertyName("variety")]
    public string? Variety { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("filled_quantity")]
    public decimal? FilledQuantity { get; init; }

    [JsonPropertyName("pending_quantity")]
    public decimal? PendingQuantity { get; init; }

    [JsonPropertyName("cancelled_quantity")]
    public decimal? CancelledQuantity { get; init; }

    [JsonPropertyName("disclosed_quantity")]
    public decimal? DisclosedQuantity { get; init; }

    [JsonPropertyName("price")]
    public decimal? Price { get; init; }

    [JsonPropertyName("trigger_price")]
    public decimal? TriggerPrice { get; init; }

    [JsonPropertyName("average_price")]
    public decimal? AveragePrice { get; init; }

    [JsonPropertyName("order_timestamp")]
    public string? OrderTimestamp { get; init; }

    [JsonPropertyName("exchange_timestamp")]
    public string? ExchangeTimestamp { get; init; }

    [JsonPropertyName("exchange_update_timestamp")]
    public string? ExchangeUpdateTimestamp { get; init; }

    /// <summary>
    /// Client tag. A string in the placement request, <c>[]</c> in the order book and
    /// <c>null</c> in /order/details — see LenientStringOrArrayConverter.
    /// </summary>
    [JsonPropertyName("tag")]
    [JsonConverter(typeof(LenientStringOrArrayConverter))]
    public string? Tag { get; init; }

    /// <summary>
    /// Whether the order has been modified. <c>"false"</c> (a string) from the order book and
    /// <c>0</c> (a number) from /order/details, for the same field. Nothing branches on it;
    /// mapped leniently so neither shape can fail the parse.
    /// </summary>
    [JsonPropertyName("modified")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool? Modified { get; init; }
}

/// <summary>
/// One row of <c>/openapi/typea/tradebook</c>.
///
/// A SECOND, COMPLETELY DIFFERENT TRADE SHAPE. <c>/trades</c> returns snake_case
/// (<c>trade_id</c>, <c>tradingsymbol</c>, <c>average_price</c>) and is modelled by
/// <see cref="MStockTradeDto"/>; <c>/tradebook</c> returns SCREAMING_SNAKE_CASE with entirely
/// different names (<c>TRADE_NUMBER</c>, <c>SYMBOL</c>, <c>PRICE</c>) for the same concepts.
///
/// Reading the tradebook into the snake_case DTO did not throw — every member simply bound to
/// null — so the call SUCCEEDED with a list of empty rows, mapping then failed on the first
/// one with "trade_id is missing", and the fallback to <c>/trades</c> that would have worked
/// never ran because the first call had not reported failure. Two wrong shapes cancelling out
/// into a plausible-looking error is exactly why this now has its own type.
/// </summary>
internal sealed class MStockTradeBookRow
{
    [JsonPropertyName("TRADE_NUMBER")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? TradeNumber { get; init; }

    [JsonPropertyName("ORDER_NUMBER")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? OrderNumber { get; init; }

    [JsonPropertyName("EXCH_ORDER_NUMBER")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? ExchangeOrderNumber { get; init; }

    /// <summary>Ticker, e.g. "IDEA". <c>FULL_SYMBOL</c> carries the company name instead.</summary>
    [JsonPropertyName("SYMBOL")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Symbol { get; init; }

    [JsonPropertyName("FULL_SYMBOL")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? FullSymbol { get; init; }

    [JsonPropertyName("EXCHANGE")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Exchange { get; init; }

    /// <summary>"Buy" / "Sell" — title case here, "BUY" / "SELL" everywhere else.</summary>
    [JsonPropertyName("BUY_SELL")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? BuySell { get; init; }

    [JsonPropertyName("PRODUCT")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Product { get; init; }

    [JsonPropertyName("ORDER_TYPE")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? OrderType { get; init; }

    [JsonPropertyName("QUANTITY")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("PRICE")]
    public decimal? Price { get; init; }

    [JsonPropertyName("TRADE_VALUE")]
    public decimal? TradeValue { get; init; }

    /// <summary>"10-06-2025 13:08:42" — day-first, 24-hour.</summary>
    [JsonPropertyName("ORDER_DATE_TIME")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? OrderDateTime { get; init; }

    /// <summary>Numeric instrument id, as a quoted string.</summary>
    [JsonPropertyName("SEC_ID")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? SecurityId { get; init; }

    /// <summary>"E" equity / "D" derivative.</summary>
    [JsonPropertyName("SEGMENT")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Segment { get; init; }

    /// <summary>Projects onto the shape the rest of the connector already maps.</summary>
    public MStockTradeDto ToTrade() => new()
    {
        TradeId = TradeNumber,
        OrderId = OrderNumber,
        ExchangeOrderId = ExchangeOrderNumber,
        TradingSymbol = Symbol,
        Exchange = Exchange,
        TransactionType = BuySell,
        Product = Product,
        Quantity = Quantity,
        AveragePrice = Price,
        Price = Price,
        FillTimestamp = OrderDateTime,
        TradeTimestamp = OrderDateTime,
    };
}

internal sealed class MStockTradeDto
{
    [JsonPropertyName("trade_id")]
    public string? TradeId { get; init; }

    [JsonPropertyName("order_id")]
    public string? OrderId { get; init; }

    [JsonPropertyName("exchange_order_id")]
    public string? ExchangeOrderId { get; init; }

    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("instrument_token")]
    public long? InstrumentToken { get; init; }

    [JsonPropertyName("transaction_type")]
    public string? TransactionType { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("average_price")]
    public decimal? AveragePrice { get; init; }

    [JsonPropertyName("price")]
    public decimal? Price { get; init; }

    [JsonPropertyName("fill_timestamp")]
    public string? FillTimestamp { get; init; }

    [JsonPropertyName("trade_timestamp")]
    public string? TradeTimestamp { get; init; }

    [JsonPropertyName("exchange_timestamp")]
    public string? ExchangeTimestamp { get; init; }
}

// --- portfolio ---------------------------------------------------------------------------

/// <summary>
/// mStock returns positions split into a <c>net</c> and a <c>day</c> bucket. We surface
/// <c>net</c>: the day bucket double-counts anything carried in from a previous session.
/// </summary>
internal sealed class MStockPositionsData
{
    [JsonPropertyName("net")]
    public IReadOnlyList<MStockPositionDto>? Net { get; init; }

    [JsonPropertyName("day")]
    public IReadOnlyList<MStockPositionDto>? Day { get; init; }
}

internal sealed class MStockPositionDto
{
    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("instrument_token")]
    public long? InstrumentToken { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("buy_quantity")]
    public decimal? BuyQuantity { get; init; }

    [JsonPropertyName("sell_quantity")]
    public decimal? SellQuantity { get; init; }

    [JsonPropertyName("average_price")]
    public decimal? AveragePrice { get; init; }

    [JsonPropertyName("last_price")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("pnl")]
    public decimal? Pnl { get; init; }

    [JsonPropertyName("realised")]
    public decimal? Realised { get; init; }

    [JsonPropertyName("unrealised")]
    public decimal? Unrealised { get; init; }

    [JsonPropertyName("multiplier")]
    public decimal? Multiplier { get; init; }
}

internal sealed class MStockHoldingDto
{
    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("exchange")]
    public string? Exchange { get; init; }

    [JsonPropertyName("instrument_token")]
    public long? InstrumentToken { get; init; }

    [JsonPropertyName("isin")]
    public string? Isin { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("t1_quantity")]
    public decimal? T1Quantity { get; init; }

    [JsonPropertyName("realised_quantity")]
    public decimal? RealisedQuantity { get; init; }

    [JsonPropertyName("collateral_quantity")]
    public decimal? CollateralQuantity { get; init; }

    [JsonPropertyName("average_price")]
    public decimal? AveragePrice { get; init; }

    [JsonPropertyName("last_price")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("pnl")]
    public decimal? Pnl { get; init; }
}

/// <summary>
/// One row of <c>/openapi/typea/user/fundsummary</c>, as mStock actually documents it:
///
/// <code>
/// {"status":"success","data":[{
///    "AVAILABLE_BALANCE":"299972678840.29","AMOUNT_UTILIZED":"27395824.71",
///    "CLEAR_BALANCE":"199999949998","COLLATERALS":"74668","SEG":"A", … }]}
/// </code>
///
/// TWO THINGS THIS CLASS EXISTS TO CORRECT. The previous version expected
/// <c>{"equity":{"available":{…}}}</c> — Zerodha Kite's margins shape, not mStock's. mStock
/// returns an ARRAY of flat rows with SCREAMING_SNAKE_CASE keys, so deserialising it into an
/// object threw and the fund summary could never have worked. And every monetary value is a
/// QUOTED string ("0.0", "299972678840.29"); they bind to <c>decimal?</c> only because
/// <see cref="MStockJson.Options"/> sets <c>AllowReadingFromString</c>.
///
/// Field names are the vendor's own, verbatim, including the misspelled
/// <c>OPT_BUY_PRIMIUM_UTILIZE</c>. Renaming them to read nicely would mean this class no longer
/// matches the payload it is documenting, which is exactly how the last mismatch survived.
/// </summary>
internal sealed class MStockFundRow
{
    /// <summary>Segment code. "A" in the documented sample; the only row for an equity account.</summary>
    [JsonPropertyName("SEG")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Segment { get; init; }

    [JsonPropertyName("LIMIT_TYPE")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? LimitType { get; init; }

    /// <summary>What the account can actually trade with right now. The headline number.</summary>
    [JsonPropertyName("AVAILABLE_BALANCE")]
    public decimal? AvailableBalance { get; init; }

    /// <summary>Settled cash, excluding collateral and unsettled payins.</summary>
    [JsonPropertyName("CLEAR_BALANCE")]
    public decimal? ClearBalance { get; init; }

    [JsonPropertyName("UNCLEAR_BALANCE")]
    public decimal? UnclearBalance { get; init; }

    /// <summary>Margin consumed by open positions and working orders.</summary>
    [JsonPropertyName("AMOUNT_UTILIZED")]
    public decimal? AmountUtilized { get; init; }

    [JsonPropertyName("COLLATERALS")]
    public decimal? Collaterals { get; init; }

    [JsonPropertyName("MF_COLLATERAL")]
    public decimal? MutualFundCollateral { get; init; }

    [JsonPropertyName("REALISED_PROFITS")]
    public decimal? RealisedProfits { get; init; }

    [JsonPropertyName("MTM_COMBINED")]
    public decimal? MarkToMarketCombined { get; init; }

    [JsonPropertyName("ADDITIONAL_MARGIN")]
    public decimal? AdditionalMargin { get; init; }

    [JsonPropertyName("PEAK_MARGIN")]
    public decimal? PeakMargin { get; init; }

    [JsonPropertyName("PHYSICAL_MARGIN")]
    public decimal? PhysicalMargin { get; init; }

    [JsonPropertyName("ADHOC_LIMIT")]
    public decimal? AdhocLimit { get; init; }

    [JsonPropertyName("BANK_HOLDING")]
    public decimal? BankHolding { get; init; }

    [JsonPropertyName("LIMIT_SOD")]
    public decimal? LimitStartOfDay { get; init; }

    [JsonPropertyName("SUM_OF_ALL")]
    public decimal? SumOfAll { get; init; }

    [JsonPropertyName("RECEIVABLES")]
    public decimal? Receivables { get; init; }

    [JsonPropertyName("PAY_OUT_AMT")]
    public decimal? PayOutAmount { get; init; }

    [JsonPropertyName("OFS_UTILIZED")]
    public decimal? OfsUtilized { get; init; }

    /// <summary>Vendor's spelling of "premium". Kept verbatim so this matches the wire.</summary>
    [JsonPropertyName("OPT_BUY_PRIMIUM_UTILIZE")]
    public decimal? OptionBuyPremiumUtilized { get; init; }

    [JsonPropertyName("MTF_AVAILABLE_BALANCE")]
    public decimal? MtfAvailableBalance { get; init; }

    [JsonPropertyName("MTF_COLLATERAL")]
    public decimal? MtfCollateral { get; init; }

    [JsonPropertyName("MTF_UTILIZE")]
    public decimal? MtfUtilized { get; init; }
}

// --- market data --------------------------------------------------------------------------

internal sealed class MStockQuoteDto
{
    [JsonPropertyName("instrument_token")]
    public long? InstrumentToken { get; init; }

    [JsonPropertyName("last_price")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("last_quantity")]
    public decimal? LastQuantity { get; init; }

    [JsonPropertyName("volume")]
    public long? Volume { get; init; }

    [JsonPropertyName("oi")]
    public long? OpenInterest { get; init; }

    [JsonPropertyName("net_change")]
    public decimal? NetChange { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    [JsonPropertyName("ohlc")]
    public MStockOhlcDto? Ohlc { get; init; }

    [JsonPropertyName("depth")]
    public MStockDepthDto? Depth { get; init; }
}

internal sealed class MStockOhlcDto
{
    [JsonPropertyName("open")]
    public decimal? Open { get; init; }

    [JsonPropertyName("high")]
    public decimal? High { get; init; }

    [JsonPropertyName("low")]
    public decimal? Low { get; init; }

    [JsonPropertyName("close")]
    public decimal? Close { get; init; }
}

internal sealed class MStockDepthDto
{
    [JsonPropertyName("buy")]
    public IReadOnlyList<MStockDepthLevelDto>? Buy { get; init; }

    [JsonPropertyName("sell")]
    public IReadOnlyList<MStockDepthLevelDto>? Sell { get; init; }
}

internal sealed class MStockDepthLevelDto
{
    [JsonPropertyName("price")]
    public decimal? Price { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("orders")]
    public int? Orders { get; init; }
}

/// <summary>
/// Candle payload. mStock sends candles as positional arrays
/// (<c>[timestamp, open, high, low, close, volume, oi?]</c>) rather than objects, so the
/// element type has to stay <see cref="JsonElement"/> and be read by index.
/// </summary>
internal sealed class MStockCandlesData
{
    [JsonPropertyName("candles")]
    public IReadOnlyList<IReadOnlyList<JsonElement>>? Candles { get; init; }
}

internal sealed class MStockOptionChainRowDto
{
    [JsonPropertyName("strikePrice")]
    public decimal? StrikePrice { get; init; }

    [JsonPropertyName("strike_price")]
    public decimal? StrikePriceSnake { get; init; }

    [JsonPropertyName("expiryDate")]
    public string? ExpiryDate { get; init; }

    [JsonPropertyName("CE")]
    public MStockOptionChainLegDto? Call { get; init; }

    [JsonPropertyName("PE")]
    public MStockOptionChainLegDto? Put { get; init; }

    [JsonIgnore]
    public decimal? Strike => StrikePrice ?? StrikePriceSnake;
}

internal sealed class MStockOptionChainLegDto
{
    [JsonPropertyName("tradingsymbol")]
    public string? TradingSymbol { get; init; }

    [JsonPropertyName("instrument_token")]
    public long? InstrumentToken { get; init; }

    [JsonPropertyName("lastPrice")]
    public decimal? LastPrice { get; init; }

    [JsonPropertyName("last_price")]
    public decimal? LastPriceSnake { get; init; }

    [JsonPropertyName("openInterest")]
    public long? OpenInterest { get; init; }

    [JsonPropertyName("oi")]
    public long? OpenInterestShort { get; init; }

    [JsonPropertyName("bidprice")]
    public decimal? BidPrice { get; init; }

    [JsonPropertyName("askPrice")]
    public decimal? AskPrice { get; init; }

    [JsonPropertyName("totalTradedVolume")]
    public long? Volume { get; init; }

    [JsonIgnore]
    public decimal? Ltp => LastPrice ?? LastPriceSnake;

    [JsonIgnore]
    public long? Oi => OpenInterest ?? OpenInterestShort;
}

internal sealed class MStockOptionChainData
{
    [JsonPropertyName("underlyingValue")]
    public decimal? UnderlyingValue { get; init; }

    [JsonPropertyName("data")]
    public IReadOnlyList<MStockOptionChainRowDto>? Rows { get; init; }

    [JsonPropertyName("records")]
    public IReadOnlyList<MStockOptionChainRowDto>? Records { get; init; }

    [JsonIgnore]
    public IReadOnlyList<MStockOptionChainRowDto> AllRows =>
        Rows ?? Records ?? [];
}
