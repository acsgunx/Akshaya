using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akshaya.Connector.Moomoo;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  OpenD message shapes, in the proto3 JSON mapping of the published .proto files.
//
//  Every name below is the proto field name verbatim (they are already lowerCamelCase, so the JSON
//  mapping leaves them alone). Response members are deliberately NOT `required`: OpenD omits fields
//  it has no value for, and a DTO that refused to deserialise over one absent optional would blank a
//  whole order book. Request members that the proto marks required are `required` here, so a request
//  missing one fails to compile instead of failing at OpenD.
//
//  64-bit ids are ulong and cross the wire as strings; see MoomooJson.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

internal static class MoomooRetType
{
    public const int Succeed = 0;
    public const int Failed = -1;
    public const int TimeOut = -100;
    public const int Disconnect = -200;
    public const int Unknown = -400;
    public const int Invalid = -500;
}

internal sealed record MoomooRequest<TC2S>
{
    [JsonPropertyName("c2s")]
    public required TC2S C2S { get; init; }
}

internal sealed record MoomooResponse<TS2C>
{
    [JsonPropertyName("retType")]
    public int RetType { get; init; } = MoomooRetType.Unknown;

    [JsonPropertyName("retMsg")]
    public string? RetMsg { get; init; }

    [JsonPropertyName("errCode")]
    public int? ErrCode { get; init; }

    [JsonPropertyName("s2c")]
    public TS2C? S2C { get; init; }
}

/// <summary>The S2C of protocols that return nothing (Trd_UnlockTrade, Trd_SubAccPush, Qot_Sub).</summary>
internal sealed record OpenDEmpty
{
    public static readonly OpenDEmpty Instance = new();
}

/// <summary>Replay protection OpenD requires on every trade write.</summary>
internal sealed record OpenDPacketId
{
    [JsonPropertyName("connID")]
    public required ulong ConnId { get; init; }

    [JsonPropertyName("serialNo")]
    public required uint SerialNo { get; init; }
}

// ─────────────────────────────────── connection ───────────────────────────────────

internal sealed record OpenDInitConnectC2S
{
    [JsonPropertyName("clientVer")]
    public required int ClientVer { get; init; }

    [JsonPropertyName("clientID")]
    public required string ClientId { get; init; }

    [JsonPropertyName("recvNotify")]
    public bool? RecvNotify { get; init; }

    [JsonPropertyName("packetEncAlgo")]
    public int? PacketEncAlgo { get; init; }

    [JsonPropertyName("pushProtoFmt")]
    public int? PushProtoFmt { get; init; }

    [JsonPropertyName("programmingLanguage")]
    public string? ProgrammingLanguage { get; init; }
}

internal sealed record OpenDInitConnectS2C
{
    [JsonPropertyName("serverVer")]
    public int ServerVer { get; init; }

    [JsonPropertyName("loginUserID")]
    public ulong LoginUserId { get; init; }

    [JsonPropertyName("connID")]
    public ulong ConnId { get; init; }

    [JsonPropertyName("keepAliveInterval")]
    public int KeepAliveInterval { get; init; }

    [JsonPropertyName("userAttribution")]
    public int? UserAttribution { get; init; }
}

internal sealed record OpenDKeepAliveC2S
{
    [JsonPropertyName("time")]
    public required long Time { get; init; }
}

internal sealed record OpenDKeepAliveS2C
{
    [JsonPropertyName("time")]
    public long Time { get; init; }
}

internal sealed record OpenDGetGlobalStateC2S
{
    /// <summary>Deprecated by OpenD and ignored, but still a required field.</summary>
    [JsonPropertyName("userID")]
    public ulong UserId { get; init; }
}

internal sealed record OpenDGetGlobalStateS2C
{
    [JsonPropertyName("qotLogined")]
    public bool QotLogined { get; init; }

    [JsonPropertyName("trdLogined")]
    public bool TrdLogined { get; init; }

    [JsonPropertyName("serverVer")]
    public int ServerVer { get; init; }

    [JsonPropertyName("serverBuildNo")]
    public int ServerBuildNo { get; init; }

    [JsonPropertyName("time")]
    public long Time { get; init; }

    [JsonPropertyName("programStatus")]
    public OpenDProgramStatus? ProgramStatus { get; init; }
}

/// <summary>
/// OpenD's program status. <c>type</c> is the one field in these protocols declared with a real proto
/// ENUM type rather than an int32, so the JSON mapping may render it as its NAME
/// ("ProgramStatusType_Ready") or as its number. It is kept raw and interpreted by <see cref="IsReady"/>.
/// </summary>
internal sealed record OpenDProgramStatus
{
    [JsonPropertyName("type")]
    public JsonElement Type { get; init; }

    [JsonPropertyName("strExtDesc")]
    public string? Description { get; init; }

    public bool IsReady => Type.ValueKind switch
    {
        JsonValueKind.Number => Type.TryGetInt32(out var value) && value == MoomooMaps.ProgramStatusReady,
        JsonValueKind.String => Type.GetString() is { } name
                                && (name.EndsWith("_Ready", StringComparison.Ordinal)
                                    || name == MoomooMaps.ProgramStatusReady.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        _ => false,
    };

    public string TypeText => Type.ValueKind switch
    {
        JsonValueKind.Number or JsonValueKind.String => Type.ToString(),
        _ => "unknown",
    };
}

internal sealed record OpenDNotifyS2C
{
    [JsonPropertyName("type")]
    public int Type { get; init; }

    [JsonPropertyName("event")]
    public OpenDGatewayEvent? Event { get; init; }

    [JsonPropertyName("programStatus")]
    public OpenDNotifyProgramStatus? ProgramStatus { get; init; }

    [JsonPropertyName("connectStatus")]
    public OpenDConnectStatus? ConnectStatus { get; init; }
}

internal sealed record OpenDGatewayEvent
{
    [JsonPropertyName("eventType")]
    public int EventType { get; init; }

    [JsonPropertyName("desc")]
    public string? Description { get; init; }
}

internal sealed record OpenDNotifyProgramStatus
{
    [JsonPropertyName("programStatus")]
    public OpenDProgramStatus? ProgramStatus { get; init; }
}

internal sealed record OpenDConnectStatus
{
    [JsonPropertyName("qotLogined")]
    public bool QotLogined { get; init; }

    [JsonPropertyName("trdLogined")]
    public bool TrdLogined { get; init; }
}

// ─────────────────────────────────── trading: common ───────────────────────────────────

internal sealed record OpenDTrdHeader
{
    [JsonPropertyName("trdEnv")]
    public int TrdEnv { get; init; }

    [JsonPropertyName("accID")]
    public ulong AccId { get; init; }

    [JsonPropertyName("trdMarket")]
    public int TrdMarket { get; init; }
}

internal sealed record OpenDTrdAcc
{
    [JsonPropertyName("trdEnv")]
    public int TrdEnv { get; init; }

    [JsonPropertyName("accID")]
    public ulong AccId { get; init; }

    [JsonPropertyName("trdMarketAuthList")]
    public List<int>? TrdMarketAuthList { get; init; }

    [JsonPropertyName("accType")]
    public int? AccType { get; init; }

    [JsonPropertyName("cardNum")]
    public string? CardNum { get; init; }

    [JsonPropertyName("securityFirm")]
    public int? SecurityFirm { get; init; }

    [JsonPropertyName("simAccType")]
    public int? SimAccType { get; init; }

    [JsonPropertyName("uniCardNum")]
    public string? UniCardNum { get; init; }

    [JsonPropertyName("accStatus")]
    public int? AccStatus { get; init; }
}

internal sealed record OpenDFunds
{
    [JsonPropertyName("power")]
    public double? Power { get; init; }

    [JsonPropertyName("totalAssets")]
    public double? TotalAssets { get; init; }

    [JsonPropertyName("cash")]
    public double? Cash { get; init; }

    [JsonPropertyName("marketVal")]
    public double? MarketVal { get; init; }

    [JsonPropertyName("frozenCash")]
    public double? FrozenCash { get; init; }

    [JsonPropertyName("avlWithdrawalCash")]
    public double? AvailableWithdrawalCash { get; init; }

    [JsonPropertyName("currency")]
    public int? Currency { get; init; }

    [JsonPropertyName("unrealizedPL")]
    public double? UnrealizedPl { get; init; }

    [JsonPropertyName("realizedPL")]
    public double? RealizedPl { get; init; }

    [JsonPropertyName("initialMargin")]
    public double? InitialMargin { get; init; }

    [JsonPropertyName("maintenanceMargin")]
    public double? MaintenanceMargin { get; init; }

    [JsonPropertyName("netCashPower")]
    public double? NetCashPower { get; init; }
}

internal sealed record OpenDPosition
{
    [JsonPropertyName("positionID")]
    public ulong PositionId { get; init; }

    [JsonPropertyName("positionSide")]
    public int PositionSide { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("qty")]
    public double? Qty { get; init; }

    [JsonPropertyName("canSellQty")]
    public double? CanSellQty { get; init; }

    [JsonPropertyName("price")]
    public double? Price { get; init; }

    [JsonPropertyName("costPrice")]
    public double? CostPrice { get; init; }

    [JsonPropertyName("plVal")]
    public double? PlVal { get; init; }

    [JsonPropertyName("secMarket")]
    public int? SecMarket { get; init; }

    [JsonPropertyName("unrealizedPL")]
    public double? UnrealizedPl { get; init; }

    [JsonPropertyName("realizedPL")]
    public double? RealizedPl { get; init; }

    [JsonPropertyName("currency")]
    public int? Currency { get; init; }

    [JsonPropertyName("trdMarket")]
    public int? TrdMarket { get; init; }

    [JsonPropertyName("dilutedCostPrice")]
    public double? DilutedCostPrice { get; init; }

    [JsonPropertyName("averageCostPrice")]
    public double? AverageCostPrice { get; init; }
}

internal sealed record OpenDOrder
{
    [JsonPropertyName("trdSide")]
    public int TrdSide { get; init; }

    [JsonPropertyName("orderType")]
    public int OrderType { get; init; }

    [JsonPropertyName("orderStatus")]
    public int OrderStatus { get; init; }

    [JsonPropertyName("orderID")]
    public ulong OrderId { get; init; }

    [JsonPropertyName("orderIDEx")]
    public string? OrderIdEx { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("qty")]
    public double? Qty { get; init; }

    [JsonPropertyName("price")]
    public double? Price { get; init; }

    [JsonPropertyName("createTime")]
    public string? CreateTime { get; init; }

    [JsonPropertyName("updateTime")]
    public string? UpdateTime { get; init; }

    [JsonPropertyName("fillQty")]
    public double? FillQty { get; init; }

    [JsonPropertyName("fillAvgPrice")]
    public double? FillAvgPrice { get; init; }

    [JsonPropertyName("lastErrMsg")]
    public string? LastErrMsg { get; init; }

    [JsonPropertyName("secMarket")]
    public int? SecMarket { get; init; }

    [JsonPropertyName("createTimestamp")]
    public double? CreateTimestamp { get; init; }

    [JsonPropertyName("updateTimestamp")]
    public double? UpdateTimestamp { get; init; }

    [JsonPropertyName("remark")]
    public string? Remark { get; init; }

    [JsonPropertyName("timeInForce")]
    public int? TimeInForce { get; init; }

    [JsonPropertyName("auxPrice")]
    public double? AuxPrice { get; init; }

    [JsonPropertyName("currency")]
    public int? Currency { get; init; }

    [JsonPropertyName("trdMarket")]
    public int? TrdMarket { get; init; }
}

internal sealed record OpenDOrderFill
{
    [JsonPropertyName("trdSide")]
    public int TrdSide { get; init; }

    [JsonPropertyName("fillID")]
    public ulong FillId { get; init; }

    [JsonPropertyName("fillIDEx")]
    public string? FillIdEx { get; init; }

    [JsonPropertyName("orderID")]
    public ulong? OrderId { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("qty")]
    public double? Qty { get; init; }

    [JsonPropertyName("price")]
    public double? Price { get; init; }

    [JsonPropertyName("createTime")]
    public string? CreateTime { get; init; }

    [JsonPropertyName("secMarket")]
    public int? SecMarket { get; init; }

    [JsonPropertyName("createTimestamp")]
    public double? CreateTimestamp { get; init; }

    [JsonPropertyName("status")]
    public int? Status { get; init; }

    [JsonPropertyName("trdMarket")]
    public int? TrdMarket { get; init; }
}

internal sealed record OpenDTrdFilterConditions
{
    [JsonPropertyName("codeList")]
    public List<string>? CodeList { get; init; }

    [JsonPropertyName("idList")]
    public List<ulong>? IdList { get; init; }

    /// <summary>Market-local <c>yyyy-MM-dd HH:mm:ss</c>. Required by the history protocols.</summary>
    [JsonPropertyName("beginTime")]
    public string? BeginTime { get; init; }

    [JsonPropertyName("endTime")]
    public string? EndTime { get; init; }
}

// ─────────────────────────────────── trading: requests ───────────────────────────────────

internal sealed record OpenDGetAccListC2S
{
    [JsonPropertyName("userID")]
    public ulong UserId { get; init; }

    [JsonPropertyName("trdCategory")]
    public int? TrdCategory { get; init; }

    [JsonPropertyName("needGeneralSecAccount")]
    public bool? NeedGeneralSecAccount { get; init; }
}

internal sealed record OpenDGetAccListS2C
{
    [JsonPropertyName("accList")]
    public List<OpenDTrdAcc>? AccList { get; init; }
}

internal sealed record OpenDUnlockTradeC2S
{
    [JsonPropertyName("unlock")]
    public required bool Unlock { get; init; }

    [JsonPropertyName("pwdMD5")]
    public string? PwdMd5 { get; init; }

    [JsonPropertyName("securityFirm")]
    public int? SecurityFirm { get; init; }
}

internal sealed record OpenDSubAccPushC2S
{
    [JsonPropertyName("accIDList")]
    public required List<ulong> AccIdList { get; init; }
}

internal sealed record OpenDGetFundsC2S
{
    [JsonPropertyName("header")]
    public required OpenDTrdHeader Header { get; init; }

    [JsonPropertyName("refreshCache")]
    public bool? RefreshCache { get; init; }

    [JsonPropertyName("currency")]
    public int? Currency { get; init; }
}

internal sealed record OpenDGetFundsS2C
{
    [JsonPropertyName("header")]
    public OpenDTrdHeader? Header { get; init; }

    [JsonPropertyName("funds")]
    public OpenDFunds? Funds { get; init; }
}

internal sealed record OpenDGetPositionListC2S
{
    [JsonPropertyName("header")]
    public required OpenDTrdHeader Header { get; init; }

    [JsonPropertyName("filterConditions")]
    public OpenDTrdFilterConditions? FilterConditions { get; init; }

    [JsonPropertyName("refreshCache")]
    public bool? RefreshCache { get; init; }
}

internal sealed record OpenDGetPositionListS2C
{
    [JsonPropertyName("header")]
    public OpenDTrdHeader? Header { get; init; }

    [JsonPropertyName("positionList")]
    public List<OpenDPosition>? PositionList { get; init; }
}

internal sealed record OpenDGetOrderListC2S
{
    [JsonPropertyName("header")]
    public required OpenDTrdHeader Header { get; init; }

    [JsonPropertyName("filterConditions")]
    public OpenDTrdFilterConditions? FilterConditions { get; init; }

    [JsonPropertyName("filterStatusList")]
    public List<int>? FilterStatusList { get; init; }

    [JsonPropertyName("refreshCache")]
    public bool? RefreshCache { get; init; }
}

internal sealed record OpenDOrderListS2C
{
    [JsonPropertyName("header")]
    public OpenDTrdHeader? Header { get; init; }

    [JsonPropertyName("orderList")]
    public List<OpenDOrder>? OrderList { get; init; }
}

internal sealed record OpenDGetHistoryOrderListC2S
{
    [JsonPropertyName("header")]
    public required OpenDTrdHeader Header { get; init; }

    [JsonPropertyName("filterConditions")]
    public required OpenDTrdFilterConditions FilterConditions { get; init; }
}

internal sealed record OpenDGetOrderFillListC2S
{
    [JsonPropertyName("header")]
    public required OpenDTrdHeader Header { get; init; }

    [JsonPropertyName("filterConditions")]
    public OpenDTrdFilterConditions? FilterConditions { get; init; }

    [JsonPropertyName("refreshCache")]
    public bool? RefreshCache { get; init; }
}

internal sealed record OpenDGetHistoryOrderFillListC2S
{
    [JsonPropertyName("header")]
    public required OpenDTrdHeader Header { get; init; }

    [JsonPropertyName("filterConditions")]
    public required OpenDTrdFilterConditions FilterConditions { get; init; }
}

internal sealed record OpenDOrderFillListS2C
{
    [JsonPropertyName("header")]
    public OpenDTrdHeader? Header { get; init; }

    [JsonPropertyName("orderFillList")]
    public List<OpenDOrderFill>? OrderFillList { get; init; }
}

internal sealed record OpenDPlaceOrderC2S
{
    [JsonPropertyName("packetID")]
    public required OpenDPacketId PacketId { get; init; }

    [JsonPropertyName("header")]
    public required OpenDTrdHeader Header { get; init; }

    [JsonPropertyName("trdSide")]
    public required int TrdSide { get; init; }

    [JsonPropertyName("orderType")]
    public required int OrderType { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("qty")]
    public required double Qty { get; init; }

    [JsonPropertyName("price")]
    public double? Price { get; init; }

    [JsonPropertyName("secMarket")]
    public int? SecMarket { get; init; }

    /// <summary>Free text, at most 64 bytes, echoed back on every order row. Carries the ClientOrderId.</summary>
    [JsonPropertyName("remark")]
    public string? Remark { get; init; }

    [JsonPropertyName("timeInForce")]
    public int? TimeInForce { get; init; }

    [JsonPropertyName("auxPrice")]
    public double? AuxPrice { get; init; }
}

internal sealed record OpenDPlaceOrderS2C
{
    [JsonPropertyName("header")]
    public OpenDTrdHeader? Header { get; init; }

    [JsonPropertyName("orderID")]
    public ulong? OrderId { get; init; }

    [JsonPropertyName("orderIDEx")]
    public string? OrderIdEx { get; init; }
}

internal sealed record OpenDModifyOrderC2S
{
    [JsonPropertyName("packetID")]
    public required OpenDPacketId PacketId { get; init; }

    [JsonPropertyName("header")]
    public required OpenDTrdHeader Header { get; init; }

    /// <summary>Zero when <see cref="ForAll"/> is true.</summary>
    [JsonPropertyName("orderID")]
    public required ulong OrderId { get; init; }

    [JsonPropertyName("modifyOrderOp")]
    public required int ModifyOrderOp { get; init; }

    [JsonPropertyName("forAll")]
    public bool? ForAll { get; init; }

    /// <summary>Only meaningful, and only needed, for a cancel-all.</summary>
    [JsonPropertyName("trdMarket")]
    public int? TrdMarket { get; init; }

    [JsonPropertyName("qty")]
    public double? Qty { get; init; }

    [JsonPropertyName("price")]
    public double? Price { get; init; }

    [JsonPropertyName("auxPrice")]
    public double? AuxPrice { get; init; }
}

internal sealed record OpenDModifyOrderS2C
{
    [JsonPropertyName("header")]
    public OpenDTrdHeader? Header { get; init; }

    [JsonPropertyName("orderID")]
    public ulong OrderId { get; init; }
}

internal sealed record OpenDUpdateOrderS2C
{
    [JsonPropertyName("header")]
    public OpenDTrdHeader? Header { get; init; }

    [JsonPropertyName("order")]
    public OpenDOrder? Order { get; init; }
}

internal sealed record OpenDUpdateOrderFillS2C
{
    [JsonPropertyName("header")]
    public OpenDTrdHeader? Header { get; init; }

    [JsonPropertyName("orderFill")]
    public OpenDOrderFill? OrderFill { get; init; }
}

// ─────────────────────────────────── quotes ───────────────────────────────────

internal sealed record OpenDSecurity
{
    [JsonPropertyName("market")]
    public int Market { get; init; }

    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;
}

internal sealed record OpenDSubC2S
{
    [JsonPropertyName("securityList")]
    public required List<OpenDSecurity> SecurityList { get; init; }

    [JsonPropertyName("subTypeList")]
    public required List<int> SubTypeList { get; init; }

    [JsonPropertyName("isSubOrUnSub")]
    public required bool IsSubOrUnSub { get; init; }

    [JsonPropertyName("isRegOrUnRegPush")]
    public bool? IsRegOrUnRegPush { get; init; }

    [JsonPropertyName("isFirstPush")]
    public bool? IsFirstPush { get; init; }
}

internal sealed record OpenDOptionBasicQotExData
{
    [JsonPropertyName("openInterest")]
    public int? OpenInterest { get; init; }
}

internal sealed record OpenDBasicQot
{
    [JsonPropertyName("security")]
    public OpenDSecurity? Security { get; init; }

    [JsonPropertyName("isSuspended")]
    public bool? IsSuspended { get; init; }

    [JsonPropertyName("updateTime")]
    public string? UpdateTime { get; init; }

    [JsonPropertyName("highPrice")]
    public double? HighPrice { get; init; }

    [JsonPropertyName("openPrice")]
    public double? OpenPrice { get; init; }

    [JsonPropertyName("lowPrice")]
    public double? LowPrice { get; init; }

    [JsonPropertyName("curPrice")]
    public double? CurPrice { get; init; }

    [JsonPropertyName("lastClosePrice")]
    public double? LastClosePrice { get; init; }

    [JsonPropertyName("volume")]
    public long? Volume { get; init; }

    [JsonPropertyName("updateTimestamp")]
    public double? UpdateTimestamp { get; init; }

    [JsonPropertyName("optionExData")]
    public OpenDOptionBasicQotExData? OptionExData { get; init; }
}

internal sealed record OpenDUpdateBasicQotS2C
{
    [JsonPropertyName("basicQotList")]
    public List<OpenDBasicQot>? BasicQotList { get; init; }
}

internal sealed record OpenDOrderBookLevel
{
    [JsonPropertyName("price")]
    public double? Price { get; init; }

    [JsonPropertyName("volume")]
    public long? Volume { get; init; }

    /// <summary>Spelled "oreder" in the proto. Correcting it here would silently drop every order count.</summary>
    [JsonPropertyName("orederCount")]
    public int? OrderCount { get; init; }
}

internal sealed record OpenDGetOrderBookC2S
{
    [JsonPropertyName("security")]
    public required OpenDSecurity Security { get; init; }

    [JsonPropertyName("num")]
    public required int Num { get; init; }
}

internal sealed record OpenDOrderBookS2C
{
    [JsonPropertyName("security")]
    public OpenDSecurity? Security { get; init; }

    [JsonPropertyName("orderBookAskList")]
    public List<OpenDOrderBookLevel>? OrderBookAskList { get; init; }

    [JsonPropertyName("orderBookBidList")]
    public List<OpenDOrderBookLevel>? OrderBookBidList { get; init; }

    [JsonPropertyName("svrRecvTimeBidTimestamp")]
    public double? ServerReceivedBidTimestamp { get; init; }

    [JsonPropertyName("svrRecvTimeAskTimestamp")]
    public double? ServerReceivedAskTimestamp { get; init; }
}

internal sealed record OpenDKLine
{
    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("isBlank")]
    public bool? IsBlank { get; init; }

    [JsonPropertyName("highPrice")]
    public double? HighPrice { get; init; }

    [JsonPropertyName("openPrice")]
    public double? OpenPrice { get; init; }

    [JsonPropertyName("lowPrice")]
    public double? LowPrice { get; init; }

    [JsonPropertyName("closePrice")]
    public double? ClosePrice { get; init; }

    [JsonPropertyName("volume")]
    public long? Volume { get; init; }

    [JsonPropertyName("timestamp")]
    public double? Timestamp { get; init; }
}

internal sealed record OpenDRequestHistoryKLC2S
{
    [JsonPropertyName("rehabType")]
    public required int RehabType { get; init; }

    [JsonPropertyName("klType")]
    public required int KlType { get; init; }

    [JsonPropertyName("security")]
    public required OpenDSecurity Security { get; init; }

    [JsonPropertyName("beginTime")]
    public required string BeginTime { get; init; }

    [JsonPropertyName("endTime")]
    public required string EndTime { get; init; }

    [JsonPropertyName("maxAckKLNum")]
    public int? MaxAckKlNum { get; init; }

    /// <summary>A protobuf <c>bytes</c> field: base64 in JSON, passed back exactly as received.</summary>
    [JsonPropertyName("nextReqKey")]
    public string? NextReqKey { get; init; }
}

internal sealed record OpenDRequestHistoryKLS2C
{
    [JsonPropertyName("security")]
    public OpenDSecurity? Security { get; init; }

    [JsonPropertyName("klList")]
    public List<OpenDKLine>? KlList { get; init; }

    [JsonPropertyName("nextReqKey")]
    public string? NextReqKey { get; init; }
}

internal sealed record OpenDSecurityStaticBasic
{
    [JsonPropertyName("security")]
    public OpenDSecurity? Security { get; init; }

    [JsonPropertyName("lotSize")]
    public int? LotSize { get; init; }

    [JsonPropertyName("secType")]
    public int? SecType { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("listTime")]
    public string? ListTime { get; init; }

    [JsonPropertyName("delisting")]
    public bool? Delisting { get; init; }

    [JsonPropertyName("exchType")]
    public int? ExchType { get; init; }
}

internal sealed record OpenDOptionStaticExData
{
    [JsonPropertyName("type")]
    public int? Type { get; init; }

    [JsonPropertyName("owner")]
    public OpenDSecurity? Owner { get; init; }

    [JsonPropertyName("strikeTime")]
    public string? StrikeTime { get; init; }

    [JsonPropertyName("strikePrice")]
    public double? StrikePrice { get; init; }

    [JsonPropertyName("suspend")]
    public bool? Suspend { get; init; }
}

internal sealed record OpenDSecurityStaticInfo
{
    [JsonPropertyName("basic")]
    public OpenDSecurityStaticBasic? Basic { get; init; }

    [JsonPropertyName("optionExData")]
    public OpenDOptionStaticExData? OptionExData { get; init; }
}

internal sealed record OpenDGetStaticInfoC2S
{
    [JsonPropertyName("market")]
    public int? Market { get; init; }

    [JsonPropertyName("secType")]
    public int? SecType { get; init; }

    /// <summary>When present OpenD ignores market and secType and answers for these securities only.</summary>
    [JsonPropertyName("securityList")]
    public List<OpenDSecurity>? SecurityList { get; init; }
}

internal sealed record OpenDGetStaticInfoS2C
{
    [JsonPropertyName("staticInfoList")]
    public List<OpenDSecurityStaticInfo>? StaticInfoList { get; init; }
}

internal sealed record OpenDSnapshotBasicData
{
    [JsonPropertyName("security")]
    public OpenDSecurity? Security { get; init; }

    [JsonPropertyName("type")]
    public int? Type { get; init; }

    [JsonPropertyName("isSuspend")]
    public bool? IsSuspend { get; init; }

    [JsonPropertyName("lotSize")]
    public int? LotSize { get; init; }

    [JsonPropertyName("updateTime")]
    public string? UpdateTime { get; init; }

    [JsonPropertyName("highPrice")]
    public double? HighPrice { get; init; }

    [JsonPropertyName("openPrice")]
    public double? OpenPrice { get; init; }

    [JsonPropertyName("lowPrice")]
    public double? LowPrice { get; init; }

    [JsonPropertyName("lastClosePrice")]
    public double? LastClosePrice { get; init; }

    [JsonPropertyName("curPrice")]
    public double? CurPrice { get; init; }

    [JsonPropertyName("volume")]
    public long? Volume { get; init; }

    [JsonPropertyName("updateTimestamp")]
    public double? UpdateTimestamp { get; init; }

    [JsonPropertyName("askPrice")]
    public double? AskPrice { get; init; }

    [JsonPropertyName("bidPrice")]
    public double? BidPrice { get; init; }

    [JsonPropertyName("askVol")]
    public long? AskVol { get; init; }

    [JsonPropertyName("bidVol")]
    public long? BidVol { get; init; }
}

internal sealed record OpenDOptionSnapshotExData
{
    [JsonPropertyName("type")]
    public int? Type { get; init; }

    [JsonPropertyName("owner")]
    public OpenDSecurity? Owner { get; init; }

    [JsonPropertyName("strikeTime")]
    public string? StrikeTime { get; init; }

    [JsonPropertyName("strikePrice")]
    public double? StrikePrice { get; init; }

    [JsonPropertyName("contractSize")]
    public int? ContractSize { get; init; }

    [JsonPropertyName("openInterest")]
    public int? OpenInterest { get; init; }
}

internal sealed record OpenDSnapshot
{
    [JsonPropertyName("basic")]
    public OpenDSnapshotBasicData? Basic { get; init; }

    [JsonPropertyName("optionExData")]
    public OpenDOptionSnapshotExData? OptionExData { get; init; }
}

internal sealed record OpenDGetSecuritySnapshotC2S
{
    [JsonPropertyName("securityList")]
    public required List<OpenDSecurity> SecurityList { get; init; }
}

internal sealed record OpenDGetSecuritySnapshotS2C
{
    [JsonPropertyName("snapshotList")]
    public List<OpenDSnapshot>? SnapshotList { get; init; }
}

internal sealed record OpenDGetOptionChainC2S
{
    [JsonPropertyName("owner")]
    public required OpenDSecurity Owner { get; init; }

    /// <summary><c>yyyy-MM-dd</c>. The span may be at most thirty days.</summary>
    [JsonPropertyName("beginTime")]
    public required string BeginTime { get; init; }

    [JsonPropertyName("endTime")]
    public required string EndTime { get; init; }
}

internal sealed record OpenDOptionItem
{
    [JsonPropertyName("call")]
    public OpenDSecurityStaticInfo? Call { get; init; }

    [JsonPropertyName("put")]
    public OpenDSecurityStaticInfo? Put { get; init; }
}

internal sealed record OpenDOptionChainEntry
{
    [JsonPropertyName("strikeTime")]
    public string? StrikeTime { get; init; }

    [JsonPropertyName("option")]
    public List<OpenDOptionItem>? Option { get; init; }
}

internal sealed record OpenDGetOptionChainS2C
{
    [JsonPropertyName("optionChain")]
    public List<OpenDOptionChainEntry>? OptionChain { get; init; }
}
