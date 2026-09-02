using System.Collections.Concurrent;
using Akshaya.Connectors.Abstractions;
using Akshaya.Modules.Trading.Ports;
using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Infrastructure.InMemory;

/// <summary>
/// DEVELOPMENT ONLY. FX rates from a table someone typed in.
///
/// PHASE 5 replaces this with a real rate feed with timestamps, staleness detection and an
/// audit of which rate was applied to which valuation. Until then, be clear about what this is:
/// A HARD-CODED NUMBER THAT WAS WRONG THE DAY IT WAS WRITTEN. It exists so the risk gate's
/// normalisation path can be exercised end to end without a market data subscription.
///
/// Two behaviours are deliberate and must be preserved by the real implementation:
///
///  * AN UNKNOWN PAIR IS AN ERROR, never a silent 1.0. Defaulting a missing rate to one is how
///    a 500,000 INR limit gets compared against a USD notional and passes an order eighty times
///    over the limit. The risk rules are written to fail closed on this error.
///  * INVERSES ARE DERIVED, not stored twice. A table holding both directions eventually holds
///    two numbers that disagree.
/// </summary>
public sealed class StaticFxConverter(IClock clock) : IFxConverter
{
    private readonly ConcurrentDictionary<string, decimal> _rates = new(StringComparer.Ordinal);

    /// <summary>Sets a rate and, implicitly, its inverse. Multiply an amount in <paramref name="from"/> by <paramref name="rate"/> to get <paramref name="to"/>.</summary>
    public StaticFxConverter Set(Currency from, Currency to, decimal rate)
    {
        if (rate <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "An FX rate must be positive.");
        }

        _rates[Key(from, to)] = rate;
        return this;
    }

    public Task<Result<FxQuote>> GetRateAsync(Currency from, Currency to, CancellationToken ct = default)
    {
        if (from == to)
        {
            return Task.FromResult(Result<FxQuote>.Success(new FxQuote(from, to, 1m, clock.UtcNow)));
        }

        if (_rates.TryGetValue(Key(from, to), out var direct))
        {
            return Task.FromResult(Result<FxQuote>.Success(new FxQuote(from, to, direct, clock.UtcNow)));
        }

        if (_rates.TryGetValue(Key(to, from), out var inverse) && inverse != 0m)
        {
            return Task.FromResult(Result<FxQuote>.Success(new FxQuote(from, to, 1m / inverse, clock.UtcNow)));
        }

        return Task.FromResult(Result<FxQuote>.Failure(new Error(
            ConnectorErrorCodes.InvalidRequest,
            $"No {from}/{to} rate is configured. Amounts in different currencies cannot be combined without one.")));
    }

    public async Task<Result<Money>> ConvertAsync(Money amount, Currency target, CancellationToken ct = default)
    {
        if (amount.Currency == target)
        {
            return amount;
        }

        var rate = await GetRateAsync(amount.Currency, target, ct);
        return rate.IsFailure
            ? Result<Money>.Failure(rate.Error)
            : Result<Money>.Success(amount.ConvertTo(target, rate.Value.Rate));
    }

    private static string Key(Currency from, Currency to) => $"{from.Code}/{to.Code}";
}
