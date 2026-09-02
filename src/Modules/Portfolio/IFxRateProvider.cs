using Akshaya.SharedKernel;

namespace Akshaya.Modules.Portfolio;

/// <summary>
/// One rate, and when it was true.
///
/// The timestamp is not decoration. A converted P&amp;L figure without the rate and the instant
/// it was taken cannot be reproduced, cannot be audited, and cannot be explained to a user who
/// screenshotted a different number an hour ago. Every converted figure this module produces
/// carries the rates that produced it.
/// </summary>
/// <param name="From">Source currency.</param>
/// <param name="To">Target currency.</param>
/// <param name="Rate">Multiply an amount in <paramref name="From"/> by this to get <paramref name="To"/>.</param>
/// <param name="AsOf">When the rate was observed.</param>
public readonly record struct FxRate(Currency From, Currency To, decimal Rate, DateTimeOffset AsOf);

/// <summary>
/// Supplies FX rates for DISPLAY conversion.
///
/// A missing rate is a <see cref="Result{T}"/> failure and never a silent 1.0. The whole point
/// of this module is that a portfolio spanning INR, SGD, USD and HKD stays honest; defaulting a
/// missing rate to one would produce a total that looks plausible and is wrong by an order of
/// magnitude. When a rate is missing, the native figures are still shown and the converted
/// total says why it is incomplete.
/// </summary>
public interface IFxRateProvider
{
    Task<Result<FxRate>> GetRateAsync(Currency from, Currency to, CancellationToken ct = default);
}
