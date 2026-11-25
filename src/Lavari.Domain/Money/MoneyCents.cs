using System.Collections.Frozen;
using Lavari.Domain.Errors;

namespace Lavari.Domain.Money;

/// <summary>
/// Signed integer cents with explicit ISO 4217 2-decimal currency.
/// All calculations use checked integer arithmetic; decimal conversion is restricted to formatting boundaries.
/// </summary>
public readonly record struct MoneyCents : IComparable<MoneyCents>
{
    public const long MaxSafeJsInteger = 9_007_199_254_740_991L;
    public const long MinSafeJsInteger = -9_007_199_254_740_991L;

    public static readonly FrozenSet<string> SupportedTwoDecimalCurrencies = new[]
    {
        "AUD", "USD", "NZD", "GBP", "EUR", "CAD", "SGD", "CHF", "HKD", "ZAR", "MYR", "INR", "CNY"
    }.ToFrozenSet(StringComparer.Ordinal);

    public long Cents { get; }
    public string Currency { get; }

    public MoneyCents(long cents, string currency)
    {
        if (cents is > MaxSafeJsInteger or < MinSafeJsInteger)
        {
            throw new ValidationServiceException(
                "The monetary amount is outside the supported range.",
                ErrorCodes.ArithmeticOverflow,
                "Enter a smaller monetary value.");
        }

        Currency = NormalizeAndValidateCurrency(currency);
        Cents = cents;
    }

    public static MoneyCents Zero(string currency) => new(0L, currency);

    public static string NormalizeAndValidateCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ValidationServiceException(
                "A currency code is required.",
                ErrorCodes.UnsupportedCurrency,
                "Select a supported two-decimal currency such as AUD.");
        }

        var normalized = currency.Trim().ToUpperInvariant();
        if (!SupportedTwoDecimalCurrencies.Contains(normalized))
        {
            throw new ValidationServiceException(
                $"Currency '{normalized}' is not supported. Only two-decimal-place currencies are currently supported.",
                ErrorCodes.UnsupportedCurrency,
                "Choose a supported two-decimal currency such as AUD, USD, NZD, GBP or EUR.");
        }

        return normalized;
    }

    public static MoneyCents FromExactDecimalInput(decimal amount, string currency)
    {
        var scaled = amount * 100m;
        if (decimal.Truncate(scaled) != scaled)
        {
            throw new ValidationServiceException(
                "Amounts with fractions of a cent are not supported.",
                ErrorCodes.SubCentAmountNotSupported,
                "Enter an amount with at most two decimal places.");
        }

        if (scaled is > MaxSafeJsInteger or < MinSafeJsInteger)
        {
            throw new ValidationServiceException(
                "The monetary amount is outside the supported range.",
                ErrorCodes.ArithmeticOverflow);
        }

        return new MoneyCents((long)scaled, currency);
    }

    public MoneyCents Add(MoneyCents other)
    {
        EnsureSameCurrency(other);
        try
        {
            return new MoneyCents(checked(Cents + other.Cents), Currency);
        }
        catch (OverflowException)
        {
            throw new ValidationServiceException(
                "Monetary addition exceeded the supported range.",
                ErrorCodes.ArithmeticOverflow);
        }
    }

    public MoneyCents Subtract(MoneyCents other)
    {
        EnsureSameCurrency(other);
        try
        {
            return new MoneyCents(checked(Cents - other.Cents), Currency);
        }
        catch (OverflowException)
        {
            throw new ValidationServiceException(
                "Monetary subtraction exceeded the supported range.",
                ErrorCodes.ArithmeticOverflow);
        }
    }

    public MoneyCents Negate()
    {
        try
        {
            return new MoneyCents(checked(-Cents), Currency);
        }
        catch (OverflowException)
        {
            throw new ValidationServiceException(
                "Monetary negation exceeded the supported range.",
                ErrorCodes.ArithmeticOverflow);
        }
    }

    public MoneyCents MultiplyByRatio(long numerator, long denominator)
    {
        if (denominator <= 0)
        {
            throw new ValidationServiceException(
                "Calculation divisor must be positive.",
                ErrorCodes.ValidationFailed);
        }

        Int128 product = (Int128)Cents * numerator;
        Int128 quotient = product / denominator;
        Int128 remainder = product % denominator;
        Int128 absTwiceRemainder = Int128.Abs(remainder) * 2;

        if (absTwiceRemainder >= denominator)
        {
            quotient += product >= 0 ? 1 : -1;
        }

        if (quotient > MaxSafeJsInteger || quotient < MinSafeJsInteger)
        {
            throw new ValidationServiceException(
                "Monetary calculation exceeded the supported range.",
                ErrorCodes.ArithmeticOverflow);
        }

        return new MoneyCents((long)quotient, Currency);
    }

    public MoneyCents ApplyBasisPoints(int basisPoints) => MultiplyByRatio(basisPoints, 10_000L);

    public decimal ToHumanDecimal() => Cents / 100m;

    public static MoneyCents Sum(IEnumerable<MoneyCents> items, string currency)
    {
        var total = Zero(currency);
        foreach (var item in items)
        {
            total = total.Add(item);
        }
        return total;
    }

    public int CompareTo(MoneyCents other)
    {
        EnsureSameCurrency(other);
        return Cents.CompareTo(other.Cents);
    }

    private void EnsureSameCurrency(MoneyCents other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new ValidationServiceException(
                $"Cannot combine amounts in different currencies ({Currency} and {other.Currency}).",
                ErrorCodes.CurrencyMismatch,
                "Ensure all items use the same currency before calculating totals.");
        }
    }
}
