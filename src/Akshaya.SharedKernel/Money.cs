using System.Globalization;

namespace Akshaya.SharedKernel;

/// <summary>
/// ISO 4217 currency code. A struct wrapper rather than a string so that the type system
/// stops you adding SGD to INR — the single most expensive class of bug in a cross-border
/// trading system.
/// </summary>
public readonly record struct Currency
{
    public Currency(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length != 3)
        {
            throw new ArgumentException($"Currency must be a 3-letter ISO 4217 code, got '{code}'.", nameof(code));
        }

        Code = code.ToUpperInvariant();
    }

    public string Code { get; }

    public static readonly Currency Inr = new("INR");
    public static readonly Currency Sgd = new("SGD");
    public static readonly Currency Usd = new("USD");
    public static readonly Currency Hkd = new("HKD");
    public static readonly Currency Jpy = new("JPY");
    public static readonly Currency Aud = new("AUD");
    public static readonly Currency Eur = new("EUR");

    public override string ToString() => Code;

    public static Currency Parse(string code) => new(code);
}

/// <summary>
/// An amount that always knows what it is denominated in. Arithmetic between different
/// currencies throws rather than silently producing nonsense; converting requires an
/// explicit FX rate, which forces the caller to think about which rate and when.
/// </summary>
public readonly record struct Money(decimal Amount, Currency Currency) : IComparable<Money>
{
    public static Money Zero(Currency currency) => new(0m, currency);

    public bool IsZero => Amount == 0m;

    public static Money operator +(Money a, Money b)
    {
        Assert(a, b, "add");
        return new Money(a.Amount + b.Amount, a.Currency);
    }

    public static Money operator -(Money a, Money b)
    {
        Assert(a, b, "subtract");
        return new Money(a.Amount - b.Amount, a.Currency);
    }

    public static Money operator -(Money a) => new(-a.Amount, a.Currency);

    public static Money operator *(Money a, decimal factor) => new(a.Amount * factor, a.Currency);

    public static Money operator *(decimal factor, Money a) => new(a.Amount * factor, a.Currency);

    public static Money operator /(Money a, decimal divisor) => new(a.Amount / divisor, a.Currency);

    public static bool operator >(Money a, Money b)
    {
        Assert(a, b, "compare");
        return a.Amount > b.Amount;
    }

    public static bool operator <(Money a, Money b)
    {
        Assert(a, b, "compare");
        return a.Amount < b.Amount;
    }

    public static bool operator >=(Money a, Money b) => a > b || a.Amount == b.Amount;

    public static bool operator <=(Money a, Money b) => a < b || a.Amount == b.Amount;

    public int CompareTo(Money other)
    {
        Assert(this, other, "compare");
        return Amount.CompareTo(other.Amount);
    }

    /// <summary>
    /// Converts using an explicitly supplied rate. There is deliberately no ambient
    /// "current rate" lookup here: historic P&amp;L converted at today's rate is a
    /// reporting bug, and making the rate a parameter forces the caller to choose.
    /// </summary>
    public Money ConvertTo(Currency target, decimal rate)
    {
        if (rate <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "FX rate must be positive.");
        }

        return target == Currency ? this : new Money(Amount * rate, target);
    }

    public Money Round(int decimals = 2) =>
        new(Math.Round(Amount, decimals, MidpointRounding.ToEven), Currency);

    private static void Assert(Money a, Money b, string op)
    {
        if (a.Currency != b.Currency)
        {
            throw new InvalidOperationException(
                $"Cannot {op} {a.Currency} and {b.Currency}. Convert explicitly with an FX rate first.");
        }
    }

    public override string ToString() =>
        $"{Amount.ToString("N2", CultureInfo.InvariantCulture)} {Currency}";
}

/// <summary>
/// Quantity is decimal, not int, because fractional shares are real (IBKR, most US brokers)
/// and because an int would silently truncate them. Connectors that only support whole
/// units declare <c>fractionalQuantity: false</c> in their manifest and the risk gate
/// rejects fractions before they ever reach the broker.
/// </summary>
public readonly record struct Quantity(decimal Value) : IComparable<Quantity>
{
    public static readonly Quantity Zero = new(0m);

    public bool IsFractional => Value != Math.Truncate(Value);

    public static Quantity operator +(Quantity a, Quantity b) => new(a.Value + b.Value);

    public static Quantity operator -(Quantity a, Quantity b) => new(a.Value - b.Value);

    public static bool operator >(Quantity a, Quantity b) => a.Value > b.Value;

    public static bool operator <(Quantity a, Quantity b) => a.Value < b.Value;

    public static bool operator >=(Quantity a, Quantity b) => a.Value >= b.Value;

    public static bool operator <=(Quantity a, Quantity b) => a.Value <= b.Value;

    public int CompareTo(Quantity other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
