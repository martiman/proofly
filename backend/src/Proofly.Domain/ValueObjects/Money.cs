namespace Proofly.Domain;

/// <summary>Amount in a single ISO 4217 currency. Decimal only; never floating point.</summary>
public readonly record struct Money
{
    public decimal Amount { get; }
    public string Currency { get; }

    public Money(decimal amount, string currency)
    {
        if (currency is null || currency.Length != 3 || !currency.All(c => c is >= 'A' and <= 'Z'))
        {
            throw new ArgumentException("Currency must be a 3-letter upper-case ISO 4217 code.", nameof(currency));
        }
        Amount = amount;
        Currency = currency;
    }

    public static Money Zero(string currency) => new(0m, currency);

    public Money Add(Money other) => Amount_(other, other.Amount);
    public Money Subtract(Money other) => Amount_(other, -other.Amount);
    public Money Negate() => new(-Amount, Currency);

    private Money Amount_(Money other, decimal delta)
    {
        if (other.Currency != Currency)
        {
            throw new InvalidOperationException($"Currency mismatch: {Currency} vs {other.Currency}.");
        }
        return new Money(Amount + delta, Currency);
    }

    public static Money operator +(Money a, Money b) => a.Add(b);
    public static Money operator -(Money a, Money b) => a.Subtract(b);

    /// <summary>Contract wire format: DecimalString, optional minus, up to 6 fraction digits.</summary>
    public string AmountString => Amount.ToString("0.00####", System.Globalization.CultureInfo.InvariantCulture);

    public override string ToString() => $"{AmountString} {Currency}";
}
