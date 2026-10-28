using System.Globalization;
using Proofly.Domain;

namespace Proofly.Connectors;

/// <summary>
/// Reads the currency-agnostic control totals of a delivery_manifest.csv row: every column named
/// <c>declared_total_&lt;ccy&gt;</c> (currency lower-case ISO 4217, any number of them). A blank cell means "not declared"
/// for that currency; an unknown currency code or a non-decimal value is an error, never repaired.
/// </summary>
public static class ManifestTotals
{
    public const string Prefix = "declared_total_";

    public sealed record Result(IReadOnlyList<Money> Totals, IReadOnlyList<string> Errors);

    public static bool IsTotalColumn(string header) => header.StartsWith(Prefix, StringComparison.Ordinal);

    /// <param name="row">Column name -> raw cell text of one manifest row.</param>
    public static Result Read(IReadOnlyDictionary<string, string> row)
    {
        var totals = new List<Money>();
        var errors = new List<string>();
        foreach (var (column, raw) in row.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!IsTotalColumn(column)) continue;
            var code = column[Prefix.Length..];
            if (code != code.ToLowerInvariant() || !Iso4217.IsValid(code.ToUpperInvariant()))
            {
                errors.Add($"{column}: '{code}' is not a lower-case ISO 4217 currency code.");
                continue;
            }
            var text = raw?.Trim() ?? "";
            if (text.Length == 0) continue;
            if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                || decimal.Round(amount, 6) != amount)
            {
                errors.Add($"{column}: '{text}' is not a decimal with at most 6 fraction digits.");
                continue;
            }
            totals.Add(new Money(amount, code.ToUpperInvariant()));
        }
        return new Result(totals, errors);
    }
}

/// <summary>ISO 4217 alphabetic codes accepted for control totals (active codes; extend when a customer needs another).</summary>
public static class Iso4217
{
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "AED","AFN","ALL","AMD","ANG","AOA","ARS","AUD","AWG","AZN","BAM","BBD","BDT","BGN","BHD","BIF","BMD","BND","BOB","BRL",
        "BSD","BTN","BWP","BYN","BZD","CAD","CDF","CHF","CLP","CNY","COP","CRC","CUP","CVE","CZK","DJF","DKK","DOP","DZD","EGP",
        "ERN","ETB","EUR","FJD","FKP","GBP","GEL","GHS","GIP","GMD","GNF","GTQ","GYD","HKD","HNL","HTG","HUF","IDR","ILS","INR",
        "IQD","IRR","ISK","JMD","JOD","JPY","KES","KGS","KHR","KMF","KPW","KRW","KWD","KYD","KZT","LAK","LBP","LKR","LRD","LSL",
        "LYD","MAD","MDL","MGA","MKD","MMK","MNT","MOP","MRU","MUR","MVR","MWK","MXN","MYR","MZN","NAD","NGN","NIO","NOK","NPR",
        "NZD","OMR","PAB","PEN","PGK","PHP","PKR","PLN","PYG","QAR","RON","RSD","RUB","RWF","SAR","SBD","SCR","SDG","SEK","SGD",
        "SHP","SLE","SOS","SRD","SSP","STN","SYP","SZL","THB","TJS","TMT","TND","TOP","TRY","TTD","TWD","TZS","UAH","UGX","USD",
        "UYU","UZS","VES","VND","VUV","WST","XAF","XCD","XOF","XPF","YER","ZAR","ZMW","ZWG",
    };

    public static bool IsValid(string code) => Codes.Contains(code);
}
