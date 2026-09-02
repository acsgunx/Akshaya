namespace Akshaya.SharedKernel;

public enum Side
{
    Buy,
    Sell,
}

public enum OrderType
{
    Market,
    Limit,
    Stop,
    StopLimit,
    MarketIfTouched,
    TrailingStop,
}

public enum TimeInForce
{
    Day,
    Gtc,
    Ioc,
    Fok,
    Gtd,
    AtTheOpen,
    AtTheClose,
}

/// <summary>
/// What the order does to a position, expressed as flags rather than one enum because the
/// concept fragments by market. India splits CNC / MIS / MTF / NRML; the US has cash vs
/// margin and a short-sell flag; Singapore has its own. A connector declares the
/// combinations it supports in its manifest and maps them in one place.
/// </summary>
[Flags]
public enum PositionEffect
{
    None = 0,
    Intraday = 1 << 0,
    Delivery = 1 << 1,
    Margin = 1 << 2,
    CarryForward = 1 << 3,
    ShortSell = 1 << 4,
}

public enum OrderVariety
{
    Regular,
    AfterMarket,
    Cover,
    Bracket,
    Iceberg,
    GoodTillTriggered,
}

/// <summary>
/// Canonical order lifecycle. Every broker has its own status vocabulary and its own set of
/// intermediate states; connectors collapse those onto this. <see cref="Unknown"/> exists
/// because pretending a status is known when it is not is how phantom orders happen.
/// </summary>
public enum OrderStatus
{
    /// <summary>Persisted locally, not yet sent. Exists so a crash mid-send is recoverable.</summary>
    PendingSubmit,

    /// <summary>Sent to the broker, no acknowledgement yet.</summary>
    Submitted,

    /// <summary>Acknowledged and live at the venue.</summary>
    Open,

    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected,
    Expired,
    Unknown,
}

public static class OrderStatusExtensions
{
    public static bool IsTerminal(this OrderStatus status) => status is
        OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired;

    public static bool IsWorking(this OrderStatus status) => status is
        OrderStatus.Submitted or OrderStatus.Open or OrderStatus.PartiallyFilled;
}

public enum StreamMode
{
    /// <summary>Last traded price only. Cheapest; use for watchlists.</summary>
    Ltp,

    /// <summary>LTP plus OHLC, volume and best bid/ask.</summary>
    Quote,

    /// <summary>Everything the broker sends, including market depth.</summary>
    Full,
}

public enum TimeFrame
{
    OneMinute,
    ThreeMinutes,
    FiveMinutes,
    FifteenMinutes,
    ThirtyMinutes,
    OneHour,
    OneDay,
    OneWeek,
    OneMonth,
}

public static class TimeFrameExtensions
{
    public static TimeSpan ToTimeSpan(this TimeFrame frame) => frame switch
    {
        TimeFrame.OneMinute => TimeSpan.FromMinutes(1),
        TimeFrame.ThreeMinutes => TimeSpan.FromMinutes(3),
        TimeFrame.FiveMinutes => TimeSpan.FromMinutes(5),
        TimeFrame.FifteenMinutes => TimeSpan.FromMinutes(15),
        TimeFrame.ThirtyMinutes => TimeSpan.FromMinutes(30),
        TimeFrame.OneHour => TimeSpan.FromHours(1),
        TimeFrame.OneDay => TimeSpan.FromDays(1),
        TimeFrame.OneWeek => TimeSpan.FromDays(7),
        TimeFrame.OneMonth => TimeSpan.FromDays(30),
        _ => throw new ArgumentOutOfRangeException(nameof(frame), frame, null),
    };
}
