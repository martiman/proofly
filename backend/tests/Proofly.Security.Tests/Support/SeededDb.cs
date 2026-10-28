using Proofly.Api.Auth;
using Proofly.Data.Auth;

namespace Proofly.Security.Tests.Support;

/// <summary>
/// Two neutral legal entities (SECA = in scope, SECB = must never be visible to an SECA-scoped caller), each with one run and one
/// resource of every kind that has an id in the contract. Everything that belongs to entity B carries the marker "SECB".
/// </summary>
public class SeededDb : SecDb
{
    public const string MarkerB = "SECB";

    private const string Empty = "{}";
    public static string Seed(string x, string currency, string baseCcy) => $"""
        INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-SEC{x}', 'Entity SEC{x}', '{baseCcy}');
        INSERT INTO users (id, name, email) VALUES ('u-prop-{x}', 'Proposer SEC{x}', 'prop.{x}@sec.test');
        INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state, sign_off_state)
          VALUES ('RUN-SEC{x}', 'LE-SEC{x}', '2026-10-27', 1, 'rs-1', 'REVIEW_REQUIRED', 'PREPARATION_INCOMPLETE');
        INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, state)
          VALUES ('DEL-SEC{x}', 'RUN-SEC{x}', 'LE-SEC{x}', 'transfer_instructions', '2026-10-27', '1', 'sec{x}.csv', repeat('{(x == "A" ? "a" : "b")}', 64), 'RECEIVED');
        INSERT INTO transfer_instructions (delivery_id, source_row_id, instruction_id, client_account_id, legal_entity_id, transaction_type, amount, currency, instruction_timestamp, source_status, external_reference)
          VALUES ('DEL-SEC{x}', 'row-1', 'I-SEC{x}', 'ACC-SEC{x}', 'LE-SEC{x}', 'TRANSFER', 100.50, '{currency}', '2026-10-27T09:00:00Z', 'SETTLED', 'REF-SEC{x}');
        INSERT INTO bank_transactions (delivery_id, external_transaction_id, external_account_id, booking_timestamp, value_date, amount, currency, debit_credit_indicator, bank_reference, transaction_status)
          VALUES ('DEL-SEC{x}', 'BANK-SEC{x}', 'ACC-SEC{x}', '2026-10-27T09:05:00Z', '2026-10-27', 100.50, '{currency}', 'CREDIT', 'REF-SEC{x}', 'BOOKED');
        INSERT INTO ledger_entries (delivery_id, ledger_entry_id, account_id, legal_entity_id, booking_timestamp, value_date, debit_credit_indicator, amount, currency, transaction_reference, entry_type, source_status)
          VALUES ('DEL-SEC{x}', 'LED-SEC{x}', 'ACC-SEC{x}', 'LE-SEC{x}', '2026-10-27T09:10:00Z', '2026-10-27', 'CREDIT', 100.50, '{currency}', 'REF-SEC{x}', 'TRANSFER', 'POSTED');
        INSERT INTO account_balances (delivery_id, account_id, legal_entity_id, balance_date, balance_type, opening_balance_amount, balance_amount, currency)
          VALUES ('DEL-SEC{x}', 'ACC-SEC{x}', 'LE-SEC{x}', '2026-10-27', 'DAILY_ROLLFORWARD', 0, 100.50, '{currency}');
        INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, due_at, delivery_id, source_row_id, value_amount, value_currency)
          VALUES ('EX-SEC{x}', 'RUN-SEC{x}', 'LE-SEC{x}', '2026-10-27', 'Unmatched SEC{x}', 'matching', 'UNMATCHED', 'HIGH', now(), 'DEL-SEC{x}', 'row-1', 100.50, '{currency}');
        INSERT INTO resolution_proposals (id, exception_id, kind, rationale, proposed_by_user_id)
          VALUES ('PR-SEC{x}', 'EX-SEC{x}', 'RESOLVED', 'Timing difference SEC{x}', 'u-prop-{x}');
        INSERT INTO match_groups (id, run_id, legal_entity_id, status) VALUES ('MG-SEC{x}', 'RUN-SEC{x}', 'LE-SEC{x}', 'UNMATCHED');
        INSERT INTO safeguarding_positions (run_id, legal_entity_id, currency, obligation_amount, eligible_amount, difference_amount, state, detail)
          VALUES ('RUN-SEC{x}', 'LE-SEC{x}', '{currency}', 100, 90, -10, 'SHORTFALL', '{Empty}');
        INSERT INTO safeguarding_adjustments (id, run_id, currency, amount, reason, status, proposed_by_user_id, evidence_refs, created_at)
          VALUES ('ADJ-SEC{x}', 'RUN-SEC{x}', '{currency}', 5, 'Timing SEC{x}', 'PROPOSED', 'u-prop-{x}', '["ev-1"]', '2026-10-27T08:00:00Z');
        INSERT INTO approvals (id, object_type, object_id, run_id, proposal_version, proposer_user_id, required_role, status, evidence_refs, policy_version, proposed_at)
          VALUES ('AP-SEC{x}', 'SAFEGUARDING_ADJUSTMENT', 'ADJ-SEC{x}', 'RUN-SEC{x}', 1, 'u-prop-{x}', 'APPROVER', 'PENDING', '["ev-1"]', 'pol-1', '2026-10-27T09:00:00Z');
        INSERT INTO audit_events (event_type, actor_type, actor_id, subject_kind, subject_id, run_id, reason, chain_id, chain_seq, hash)
          VALUES ('ADJUSTMENT_PROPOSED', 'USER', 'u-prop-{x}', 'ADJUSTMENT', 'ADJ-SEC{x}', 'RUN-SEC{x}', 'Timing SEC{x}', 'seed-{x}', 1, repeat('{(x == "A" ? "c" : "d")}', 64));
        """;

    protected override string? SeedSql => Seed("A", "GBP", "GBP") + Seed("B", "EUR", "EUR");

    /// <summary>Id values for entity B per contract path-parameter name.</summary>
    public async Task<Dictionary<string, string>> IdsOf(string x)
    {
        var trf = await Scalar<long>($"SELECT id FROM transfer_instructions WHERE delivery_id='DEL-SEC{x}'");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runId"] = $"RUN-SEC{x}",
            ["legalEntityId"] = $"LE-SEC{x}",
            ["deliveryId"] = $"DEL-SEC{x}",
            ["recordId"] = $"transfer_instructions:{trf}",
            ["exceptionId"] = $"EX-SEC{x}",
            ["proposalId"] = $"PR-SEC{x}",
            ["transactionId"] = $"TRF-{trf}",
            ["groupId"] = $"MG-SEC{x}",
            ["accountId"] = $"ACC-SEC{x}",
            ["adjustmentId"] = $"ADJ-SEC{x}",
            ["currency"] = x == "A" ? "GBP" : "EUR", // the currency Seed(x, ...) gives the entity's safeguarding position
            ["subjectKind"] = "EXCEPTION",
            ["subjectId"] = $"EX-SEC{x}",
        };
    }

    /// <summary>Path parameters that name resources which are not owned by a legal entity (user preferences, global configuration).</summary>
    public static readonly HashSet<string> GlobalIds = ["changeId", "viewId"];

    public async Task<string> UserWith(SecFactory f, string key, params (string Role, string Le)[] roles)
    {
        await new UserDirectory(Db).EnsureUserAsync(new UserRecord("u-" + key, key, key + "@sec.test", "sub-" + key, true,
            roles.Select(r => new RoleAssignment(r.Role, r.Le)).ToList()));
        return f.Tokens.Issue("sub-" + key);
    }
}
