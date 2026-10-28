using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Proofly.Domain.Enums;

namespace Proofly.Data.Governance.Commands;

public sealed record PayloadError(string Pointer, string Message);

/// <summary>
/// Pure validation of a proposed configuration object against the stored payload shapes (docs/integrations/api/README.md, "Stored payload shapes").
/// Unknown keys and invalid values are rejected, never dropped or repaired. Read-only echoes of the GET shape (ruleId, version, ...) are
/// accepted when they equal the target and are not stored.
/// </summary>
public static partial class ConfigPayloadValidator
{
    private static readonly HashSet<string> ControlAreas = Enum.GetValues<ControlArea>().Select(WireNames.ToLowerSnake).ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> Severities = Enum.GetValues<Severity>().Select(WireNames.ToUpperSnake).ToHashSet(StringComparer.Ordinal);
    public static readonly IReadOnlySet<string> RoleNames = Enum.GetValues<Role>().Select(WireNames.ToUpperSnake).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"^(?<amount>\d+(\.\d+)?) (?<ccy>[A-Z]{3})$")] private static partial Regex ToleranceRx();
    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")] private static partial Regex TimeRx();

    /// <summary>Returns the normalised stored object, or the errors (never both).</summary>
    public static (JsonObject? Value, IReadOnlyList<PayloadError> Errors) Validate(string targetKind, string targetId, JsonObject after)
    {
        var errors = new List<PayloadError>();
        var value = new JsonObject();
        void Err(string key, string msg) => errors.Add(new PayloadError("/after" + (key.Length == 0 ? "" : "/" + key), msg));

        (string[] required, string[] optional, (string Key, string? Expect)[] echoes) = targetKind switch
        {
            ConfigTargetKinds.Rule => (new[] { "control", "description", "tolerance", "severity", "effectiveFrom" }, Array.Empty<string>(),
                new (string, string?)[] { ("ruleId", targetId), ("version", null), ("pendingChangeId", null) }),
            ConfigTargetKinds.Source => (new[] { "schemaVersion", "expectedByLocalTime", "mandatory" }, Array.Empty<string>(),
                new (string, string?)[] { ("sourceName", null), ("pendingChangeId", null) }),
            ConfigTargetKinds.ApprovalPolicy => (new[] { "description" }, new[] { "requiredRole" },
                new (string, string?)[] { ("name", null), ("version", null) }),
            ConfigTargetKinds.UserAccess => (new[] { "roles", "legalEntityIds" }, Array.Empty<string>(),
                new (string, string?)[] { ("id", targetId), ("name", null), ("email", null), ("pendingChangeId", null) }),
            _ => throw new ArgumentOutOfRangeException(nameof(targetKind)),
        };

        foreach (var (k, v) in after)
        {
            if (required.Contains(k) || optional.Contains(k)) { continue; }
            var echo = echoes.FirstOrDefault(e => e.Key == k);
            if (echo.Key is null) { Err(k, "Unknown key."); continue; }
            if (echo.Expect is not null && (v is not JsonValue jv || !jv.TryGetValue<string>(out var s) || s != echo.Expect)) { Err(k, $"Must equal '{echo.Expect}'."); }
        }
        foreach (var k in required) { if (!after.ContainsKey(k) || after[k] is null) { Err(k, "Required."); } }

        string? Str(string key, bool allowEmpty = false)
        {
            if (after[key] is not JsonValue v || !v.TryGetValue<string>(out var s)) { if (after.ContainsKey(key)) { Err(key, "Must be a string."); } return null; }
            if (!allowEmpty && string.IsNullOrWhiteSpace(s)) { Err(key, "Must not be empty."); return null; }
            return s;
        }

        switch (targetKind)
        {
            case ConfigTargetKinds.Rule:
                if (Str("control") is { } c) { if (!ControlAreas.Contains(c)) { Err("control", "Unknown control area."); } else { value["control"] = c; } }
                if (Str("description") is { } d) { value["description"] = d; }
                if (Str("tolerance") is { } t)
                {
                    var m = ToleranceRx().Match(t);
                    if (!m.Success || !decimal.TryParse(m.Groups["amount"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amt) || amt < 0)
                    { Err("tolerance", "Must be a non-negative decimal and a 3-letter currency, e.g. \"0.01 GBP\"."); }
                    else { value["tolerance"] = t; }
                }
                if (Str("severity") is { } sev) { if (!Severities.Contains(sev)) { Err("severity", "Unknown severity."); } else { value["severity"] = sev; } }
                if (Str("effectiveFrom") is { } ef)
                {
                    if (!DateOnly.TryParseExact(ef, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) { Err("effectiveFrom", "Must be a date (yyyy-MM-dd)."); }
                    else { value["effectiveFrom"] = ef; }
                }
                break;
            case ConfigTargetKinds.Source:
                if (Str("schemaVersion") is { } sv) { value["schemaVersion"] = sv; }
                if (Str("expectedByLocalTime") is { } et) { if (!TimeRx().IsMatch(et)) { Err("expectedByLocalTime", "Must be HH:mm."); } else { value["expectedByLocalTime"] = et; } }
                if (after["mandatory"] is { } mn) { if (mn is JsonValue mv && mv.TryGetValue<bool>(out var b)) { value["mandatory"] = b; } else { Err("mandatory", "Must be a boolean."); } }
                break;
            case ConfigTargetKinds.ApprovalPolicy:
                if (Str("description") is { } pd) { value["description"] = pd; }
                if (after.ContainsKey("requiredRole") && Str("requiredRole") is { } rr) { if (!RoleNames.Contains(rr)) { Err("requiredRole", "Unknown role."); } else { value["requiredRole"] = rr; } }
                break;
            case ConfigTargetKinds.UserAccess:
                var roles = StringSet(after, "roles", Err, allowEmpty: true, RoleNames);
                var les = StringSet(after, "legalEntityIds", Err, allowEmpty: false, null);
                if (roles is not null) { value["roles"] = new JsonArray(roles.Select(r => (JsonNode)r).ToArray()); }
                if (les is not null) { value["legalEntityIds"] = new JsonArray(les.Select(r => (JsonNode)r).ToArray()); }
                break;
        }
        return errors.Count == 0 ? (value, errors) : (null, errors);
    }

    private static string[]? StringSet(JsonObject o, string key, Action<string, string> err, bool allowEmpty, IReadOnlySet<string>? allowed)
    {
        if (o[key] is not JsonArray a) { if (o.ContainsKey(key) && o[key] is not null) { err(key, "Must be an array of strings."); } return null; }
        var list = new List<string>();
        foreach (var n in a)
        {
            if (n is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) { err(key, "Must be an array of non-empty strings."); return null; }
            if (allowed is not null && !allowed.Contains(s)) { err(key, $"Unknown value '{s}'."); return null; }
            list.Add(s);
        }
        if (list.Count != list.Distinct(StringComparer.Ordinal).Count()) { err(key, "Duplicate entries."); return null; }
        if (!allowEmpty && list.Count == 0) { err(key, "Must not be empty."); return null; }
        list.Sort(StringComparer.Ordinal);
        return [.. list];
    }

    /// <summary>Key-wise diff of two objects (union of keys, ordinal order). Equal values are omitted.</summary>
    public static IReadOnlyList<(string Path, JsonNode? From, JsonNode? To)> Diff(JsonObject? before, JsonObject after)
    {
        var keys = (before?.Select(p => p.Key) ?? []).Concat(after.Select(p => p.Key)).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);
        var res = new List<(string, JsonNode?, JsonNode?)>();
        foreach (var k in keys)
        {
            var f = before?[k]; var t = after[k];
            if (!JsonNode.DeepEquals(f, t)) { res.Add((k, f?.DeepClone(), t?.DeepClone())); }
        }
        return res;
    }
}
