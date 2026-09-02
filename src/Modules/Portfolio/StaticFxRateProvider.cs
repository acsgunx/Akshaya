using System.Collections.Concurrent;
using Akshaya.Connectors.Abstractions;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Portfolio;

/// <summary>
/// DEVELOPMENT ONLY. FX rates from a table someone typed in.
///
/// PHASE 5 replaces this with a real rate feed: refreshed on a schedule, with staleness
/// detection, a fallback chain and an audit of which rate valued which snapshot. Until then be
/// honest about what this is — HARD-CODED NUMBERS THAT WERE WRONG THE DAY THEY WERE WRITTEN.
/// It exists so the conversion path can be exercised end to end without a market data
/// subscription, and every figure it produces carries its <see cref="FxRate.AsOf"/> so a stale
/// rate is visible rather than assumed.
///
/// Two behaviours the real implementation must keep:
///  * An unknown pair is an ERROR, never an implied 1.0.
///  * Inverses are DERIVED from one stored direction, never stored twice — a table holding both
///    directions eventually holds two numbers that disagree.
/// </summary>
public sealed class StaticFxRateProvider(IClock clock) : IFxRateProvider
{
    private readonly ConcurrentDictionary<string, decimal> _rates = new(StringComparer.Ordinal);

    /// <summary>Sets a rate and, implicitly, its inverse.</summary>
    public StaticFxRateProvider Set(Currency from, Currency to, decimal rate)
    {
        if (rate <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "An FX rate must be positive.");
        }

        _rates[Key(from, to)] = rate;
        return this;
    }

    public Task<Result<FxRate>> GetRateAsync(Currency from, Currency to, CancellationToken ct = default)
    {
        var now = clock.UtcNow;

        if (from == to)
        {
            return Task.FromResult(Result<FxRate>.Success(new FxRate(from, to, 1m, now)));
        }

        if (_rates.TryGetValue(Key(from, to), out var direct))
        {
            return Task.FromResult(Result<FxRate>.Success(new FxRate(from, to, direct, now)));
        }

        if (_rates.TryGetValue(Key(to, from), out var inverse) && inverse != 0m)
        {
            return Task.FromResult(Result<FxRate>.Success(new FxRate(from, to, 1m / inverse, now)));
        }

        return Task.FromResult(Result<FxRate>.Failure(new Error(
            ConnectorErrorCodes.InvalidRequest,
            $"No {from}/{to} rate is configured, so {from} amounts cannot be shown in {to}.")));
    }

    private static string Key(Currency from, Currency to) => $"{from.Code}/{to.Code}";
}
