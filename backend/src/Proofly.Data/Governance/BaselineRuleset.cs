using System.Text.Json.Nodes;
using Npgsql;
using Proofly.Data.Governance.Commands;
using Proofly.Domain.Records;

namespace Proofly.Data.Governance;

/// <summary>
/// A sensible baseline configuration for a development or demonstration database: control parameters, one source configuration per data source,
/// the approval policy and the freshness policy, in the stored payload shapes of docs/integrations/api/README.md. Payloads of the rule, source and
/// approval-policy kinds are checked with <see cref="ConfigPayloadValidator"/> before anything is written. Installing creates the ruleset version v1
/// snapshot exactly as migration 0045 would have (all enabled config_rules, ordered by id), so runs that start afterwards record <c>v1</c>.
/// </summary>
public static class BaselineRuleset
{
    public sealed record Entry(string Id, string Kind, string Name, string ConfigVersion, JsonObject Payload);

    public const string Version = "v1";
    private const string EffectiveFrom = "2026-01-01";

    private static Entry Rule(string id, string name, string control, string description, string tolerance, string severity) =>
        new(id, "rule", name, Version, new JsonObject
        {
            ["control"] = control, ["description"] = description, ["tolerance"] = tolerance, ["severity"] = severity, ["effectiveFrom"] = EffectiveFrom,
        });

    private static Entry Source(string name, string expectedBy) =>
        new("SRC-" + name.ToUpperInvariant().Replace('_', '-'), "source", name, Version,
            new JsonObject { ["schemaVersion"] = "1.0", ["expectedByLocalTime"] = expectedBy, ["mandatory"] = true });

    public static IReadOnlyList<Entry> Entries { get; } =
    [
        // Matching: per-currency amount tolerance (one rule per currency, the currency is part of the tolerance text) and the severities of the matching outcomes.
        Rule("MATCH-AMOUNT-TOLERANCE", "Matching amount tolerance GBP", "matching", "Amount difference tolerated when matching in GBP", "0.01 GBP", "MEDIUM"),
        Rule("MATCH-AMOUNT-TOLERANCE-EUR", "Matching amount tolerance EUR", "matching", "Amount difference tolerated when matching in EUR", "0.01 EUR", "MEDIUM"),
        Rule("MATCH-AMOUNT-TOLERANCE-JPY", "Matching amount tolerance JPY", "matching", "Amount difference tolerated when matching in JPY (no minor unit)", "1 JPY", "MEDIUM"),
        Rule("MATCH-UNMATCHED", "Unmatched record severity", "matching", "Severity of a record with no counterpart", "0 GBP", "MEDIUM"),
        Rule("MATCH-AMBIGUOUS", "Ambiguous candidates severity", "matching", "Severity of a record with several equally good counterparts", "0 GBP", "MEDIUM"),
        // Independent ledger.
        Rule("LEDGER-BALANCE-TOLERANCE", "Ledger balance tolerance", "independent_ledger", "Difference between the independent and the recorded balance that is tolerated", "0.01 GBP", "HIGH"),
        // Lifecycle and safeguarding severities.
        Rule("LIFECYCLE-DEFAULT", "Lifecycle default severity", "transaction_lifecycle", "Severity of a broken lifecycle", "0 GBP", "HIGH"),
        Rule("LIFECYCLE-TIMING", "Lifecycle timing severity", "transaction_lifecycle", "Severity of an overdue or late lifecycle stage", "0 GBP", "MEDIUM"),
        Rule("SAFEGUARDING-STALE", "Stale safeguarding balance severity", "safeguarding", "Severity of an external balance older than the freshness threshold", "0 GBP", "HIGH"),
        // Source configurations: expected-by local time and the mandatory flag, one per data source.
        Source(SourceNames.TransferInstructions, "08:00"),
        Source(SourceNames.DirectDebitEvents, "08:00"),
        Source(SourceNames.CardTransactions, "08:00"),
        Source(SourceNames.CardSettlements, "08:30"),
        Source(SourceNames.InternalLedgerEntries, "08:00"),
        Source(SourceNames.ClientAccountBalances, "08:30"),
        Source(SourceNames.BankingTransactions, "09:00"),
        Source(SourceNames.SafeguardingBalances, "09:00"),
        // Four-eyes policy (ADR 0005) and the freshness policy text shown in settings.
        new("POLICY-FOUR-EYES", "approval_policy", "Second person (ADR 0005)", Version,
            new JsonObject { ["description"] = "A second person with the APPROVER role decides every material change; the proposer never decides their own.", ["requiredRole"] = "APPROVER" }),
        new("freshness_policy", "setting", "Freshness policy", Version,
            new JsonObject { ["text"] = "Evidence must arrive by 09:00 local time on the business day after the business date; external balances older than 24 hours are stale." }),
    ];

    /// <summary>Validation errors of the payloads that have a documented validator (rule, source, approval policy); empty when all are valid.</summary>
    public static IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var e in Entries)
        {
            var kind = e.Kind switch { "rule" => ConfigTargetKinds.Rule, "source" => ConfigTargetKinds.Source, "approval_policy" => ConfigTargetKinds.ApprovalPolicy, _ => null };
            if (kind is null) { continue; }
            var (_, errs) = ConfigPayloadValidator.Validate(kind, e.Id, (JsonObject)e.Payload.DeepClone());
            errors.AddRange(errs.Select(x => $"{e.Id}{x.Pointer}: {x.Message}"));
        }
        return errors;
    }

    /// <summary>Inserts the baseline and refreshes the v1 ruleset snapshot. Idempotent: existing rows are left alone. Throws when a payload is invalid.</summary>
    public static async Task InstallAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        if (Validate() is { Count: > 0 } errors) { throw new InvalidOperationException("Baseline ruleset is invalid: " + string.Join("; ", errors)); }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var e in Entries)
        {
            await using var c = new NpgsqlCommand(
                "INSERT INTO config_rules (id, kind, name, config_version, payload) VALUES (@id, @k, @n, @v, @p::jsonb) ON CONFLICT (id) DO NOTHING", conn, tx);
            c.Parameters.AddWithValue("id", e.Id);
            c.Parameters.AddWithValue("k", e.Kind);
            c.Parameters.AddWithValue("n", e.Name);
            c.Parameters.AddWithValue("v", e.ConfigVersion);
            c.Parameters.AddWithValue("p", e.Payload.ToJsonString());
            await c.ExecuteNonQueryAsync(ct);
        }
        // Same definition as migration 0045 used for the initial v1 row.
        await using (var c = new NpgsqlCommand("""
            UPDATE ruleset_versions SET snapshot = COALESCE((SELECT jsonb_agg(jsonb_build_object('id', id, 'kind', kind, 'name', name, 'version', config_version, 'payload', payload) ORDER BY id)
                                                             FROM config_rules WHERE enabled), '[]'::jsonb)
             WHERE version = 'v1'
            """, conn, tx))
        {
            await c.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
