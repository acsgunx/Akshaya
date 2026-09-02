namespace Akshaya.SharedKernel;

/// <summary>
/// Time is injected, never ambient. A backtester needs to control the clock, a test needs
/// to freeze it, and "is the market open" must be answerable at an arbitrary instant.
/// tests/Akshaya.Architecture.Tests bans DateTime.Now / DateTimeOffset.Now / DateTime.UtcNow
/// outside this file.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Test and backtest clock. Advance it explicitly; it never moves on its own.</summary>
public sealed class ManualClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    public void Set(DateTimeOffset to) => _now = to;
}
