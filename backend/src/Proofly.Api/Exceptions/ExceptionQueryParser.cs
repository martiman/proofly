using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using Proofly.Api.Platform.Problems;
using Proofly.Data.Exceptions;
using Proofly.Domain.Enums;

namespace Proofly.Api.Exceptions;

/// <summary>Builds an <see cref="ExceptionFilter"/> from the query string, with a saved view supplying defaults (explicit params win). Invalid values are rejected with 422, never ignored.</summary>
public static class ExceptionQueryParser
{
    private static readonly HashSet<string> Severities = Enum.GetValues<Severity>().Select(v => WireNames.ToUpperSnake(v)).ToHashSet();
    private static readonly HashSet<string> Statuses = Enum.GetValues<ExceptionStatus>().Select(v => WireNames.ToUpperSnake(v)).ToHashSet();
    private static readonly HashSet<string> Controls = Enum.GetValues<ControlArea>().Select(v => WireNames.ToLowerSnake(v)).ToHashSet();

    public static ExceptionFilter Parse(IQueryCollection query, JsonElement? savedView)
    {
        string[]? Get(string key)
        {
            if (query.TryGetValue(key, out StringValues v) && v.Count > 0) { return v.SelectMany(x => (x ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray(); }
            if (savedView is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(key, out var e))
            {
                return e.ValueKind switch
                {
                    JsonValueKind.Array => e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText()).ToArray(),
                    JsonValueKind.String => e.GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    JsonValueKind.Null => null,
                    _ => [e.GetRawText()],
                };
            }
            return null;
        }

        string? One(string key) => Get(key) is { Length: > 0 } a ? a[0] : null;
        string[]? Enum(string key, HashSet<string> allowed)
        {
            var vals = Get(key);
            if (vals is null) { return null; }
            var bad = vals.FirstOrDefault(x => !allowed.Contains(x));
            if (bad is not null) { throw ProblemException.Validation(key, $"'{bad}' is not a valid {key} value."); }
            return vals;
        }
        bool? Bool(string key) => One(key) is { } s ? (bool.TryParse(s, out var b) ? b : throw ProblemException.Validation(key, $"{key} must be true or false.")) : null;
        decimal? Dec(string key) => One(key) is { } s ? (decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : throw ProblemException.Validation(key, $"{key} must be a decimal string.")) : null;
        int? Int(string key) => One(key) is { } s ? (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : throw ProblemException.Validation(key, $"{key} must be a non-negative integer.")) : null;
        DateOnly? Date(string key) => One(key) is { } s ? (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw ProblemException.Validation(key, $"{key} must be a date (yyyy-MM-dd).")) : null;

        var currency = One("currency");
        if (currency is not null && (currency.Length != 3 || !currency.All(c => c is >= 'A' and <= 'Z'))) { throw ProblemException.Validation("currency", "currency must be an ISO 4217 code."); }
        var filter = new ExceptionFilter
        {
            Severity = Enum("severity", Severities),
            Control = Enum("control", Controls),
            Status = Enum("status", Statuses),
            OwnerId = One("ownerId"),
            Unassigned = Bool("unassigned"),
            Blocking = Bool("blocking"),
            LegalEntityId = One("legalEntityId"),
            Currency = currency,
            ValueMin = Dec("valueMin"),
            ValueMax = Dec("valueMax"),
            BusinessDateFrom = Date("businessDateFrom"),
            BusinessDateTo = Date("businessDateTo"),
            AgeDaysMin = Int("ageDaysMin"),
            AgeDaysMax = Int("ageDaysMax"),
            DueBefore = One("dueBefore") is { } due
                ? (DateTimeOffset.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : throw ProblemException.Validation("dueBefore", "dueBefore must be a date-time."))
                : null,
            Search = query.TryGetValue("search", out var sv) && sv.Count > 0 ? sv[0] : null,
        };
        if ((filter.ValueMin is not null || filter.ValueMax is not null) && filter.Currency is null)
        {
            throw ProblemException.Validation("currency", "valueMin and valueMax require currency; amounts in different currencies are never compared.");
        }
        if (filter.Unassigned == true && filter.OwnerId is not null) { throw ProblemException.Validation("unassigned", "unassigned cannot be combined with ownerId."); }
        return filter;
    }
}
