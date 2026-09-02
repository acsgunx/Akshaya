using Akshaya.SharedKernel;

namespace Akshaya.Modules.Trading.Ports;

/// <summary>One FX rate, and — just as importantly — when it was true.</summary>
/// <param name="From">Source currency.</param>
/// <param name="To">Target currency.</param>
/// <param name="Rate">Multiply an amount in <paramref name="From"/> by this to get <paramref name="To"/>.</param>
/// <param name="AsOf">
/// When the rate was observed. Carried everywhere a converted number is shown, because a
/// converted P&amp;L without a rate timestamp is a number nobody can reproduce or audit.
/// </param>
public readonly record struct FxQuote(Currency From, Currency To, decimal Rate, DateTimeOffset AsOf);

/// <summary>
/// Converts <see cref="Money"/> between currencies for the risk gate.
///
/// The risk gate needs this because limits are expressed in ONE normalised currency: a tenant
/// whose max order value is 500,000 INR must have that limit enforced identically on an order
/// priced in USD, and comparing 500,000 to 6,000 without a rate would silently pass an order
/// eighty times over the limit.
///
/// Conversion returns <see cref="Result{T}"/> rather than throwing or defaulting to 1.0. A
/// missing rate is a real operational condition and the caller must decide what to do about
/// it; the rules in this module fail CLOSED, because a risk limit that cannot be evaluated is
/// a risk limit that is not being enforced.
/// </summary>
public interface IFxConverter
{
    Task<Result<FxQuote>> GetRateAsync(Currency from, Currency to, CancellationToken ct = default);

    Task<Result<Money>> ConvertAsync(Money amount, Currency target, CancellationToken ct = default);
}
