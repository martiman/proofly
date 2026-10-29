using System.Text.Json.Serialization;

namespace Proofly.Contracts.Common;

/// <summary>
/// The one wire rule for a DecimalString: the exact value, never rounded, with at least 2 fraction digits and trailing zeros beyond the second
/// removed (0 -> "0.00", 1081977.250000 -> "1081977.25", 0.5 -> "0.50", 1.234500 -> "1.2345"). Applied in <see cref="MoneyDto"/> and in the JSON decimal converter.
/// </summary>
public static class DecimalString
{
    // A decimal has at most 28 fraction digits: 2 mandatory + 26 optional never rounds.
    private static readonly string Format = "0.00" + new string('#', 26);

    public static string Normalize(decimal value) => value.ToString(Format, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Normalises a decimal string; a string that is not a plain decimal is returned unchanged (the contract validator rejects it elsewhere).</summary>
    public static string Normalize(string value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? Normalize(d) : value;
}

public sealed record MoneyDto(string Amount, string Currency)
{
    /// <summary>Always serialised through <see cref="DecimalString.Normalize(string)"/>.</summary>
    public string Amount { get; init; } = DecimalString.Normalize(Amount);
}

/// <summary>Contract schema PageInfo. nextCursor is always present (null on the last page); total is omitted, never null, unless includeTotal=true.</summary>
public sealed record PageInfoDto(string? NextCursor, bool HasMore, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Total = null);

/// <summary>The { items, page } envelope shared by every list that has no extra members.</summary>
public sealed record PageDto<T>(IReadOnlyList<T> Items, PageInfoDto Page);

public sealed record UserRefDto(string Id, string Name);

/// <summary>Contract schema Actor: id is null for SYSTEM actors, name when none is recorded.</summary>
public sealed record ActorDto(string Type, string? Id, string? Name);

public sealed record ProblemDto(string Type, string Title, int Status, string? Detail);
