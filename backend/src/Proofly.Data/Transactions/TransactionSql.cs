namespace Proofly.Data.Transactions;

/// <summary>
/// Shared SQL fragments. The five record tables are unified with a UNION ALL inside a CTE (no database view, so the
/// schema stays untouched). Every query is scoped to one run through source_deliveries.run_id (parameter @run).
/// </summary>
internal static class TransactionSql
{
    /// <summary>CTE chain ending in <c>tx</c>: one row per normalised transaction of the run, enriched with lifecycle, match group and exceptions.</summary>
    public const string TxCte = """
        src AS (
          SELECT 'TRF-' || t.id AS id, 'BANK_TRANSFER' AS kind, 'transfer_instructions' AS source, 'transfer_instructions' AS rt, t.id AS rid,
                 t.delivery_id, t.source_row_id, COALESCE(t.external_reference, t.instruction_id) AS reference, t.client_account_id AS account_id,
                 t.instruction_timestamp AS ts, t.amount, t.currency::text AS currency, NULL::text AS direction, t.source_status,
                 t.instruction_id AS key_a, t.external_reference AS key_b
            FROM transfer_instructions t
          UNION ALL
          SELECT 'DD-' || t.id, 'DIRECT_DEBIT', 'direct_debit_events', 'direct_debit_events', t.id,
                 t.delivery_id, t.source_row_id, t.instruction_id, t.client_account_id,
                 t.event_timestamp, t.amount, t.currency::text, NULL::text, t.source_status,
                 t.instruction_id, NULL::text
            FROM direct_debit_events t
          UNION ALL
          SELECT 'CRD-' || t.id, 'CARD', 'card_transactions', 'card_events', t.id,
                 t.delivery_id, t.source_row_id, t.card_transaction_id, t.client_account_id,
                 t.event_timestamp, t.amount, t.currency::text, t.debit_credit_indicator, t.source_status,
                 t.card_transaction_id, NULL::text
            FROM card_events t
          UNION ALL
          SELECT 'BNK-' || t.id, 'BANK_EVIDENCE', 'banking_transactions', 'bank_transactions', t.id,
                 t.delivery_id, t.external_transaction_id, COALESCE(t.bank_reference, t.external_transaction_id), t.external_account_id,
                 t.booking_timestamp, t.amount, t.currency::text, t.debit_credit_indicator, t.transaction_status,
                 t.bank_reference, NULL::text
            FROM bank_transactions t
          UNION ALL
          SELECT 'LED-' || t.id, 'LEDGER_ENTRY', 'internal_ledger_entries', 'ledger_entries', t.id,
                 t.delivery_id, t.ledger_entry_id, COALESCE(t.transaction_reference, t.ledger_entry_id), t.account_id,
                 t.booking_timestamp, t.amount, t.currency::text, t.debit_credit_indicator, t.source_status,
                 t.transaction_reference, NULL::text
            FROM ledger_entries t
        ),
        src_run AS (
          SELECT s.*, d.fingerprint_sha256 AS fingerprint
            FROM src s JOIN source_deliveries d ON d.id = s.delivery_id AND d.run_id = @run
        ),
        tx AS (
          SELECT s.*,
                 array_agg(s.source_status) OVER (PARTITION BY s.kind, COALESCE(s.key_a, s.id) ORDER BY s.ts, s.id
                   ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS lifecycle,
                 COALESCE(m.gstatus, 'UNMATCHED') AS match_status, m.gid AS match_group_id,
                 COALESCE(ex.ids, ARRAY[]::text[]) AS exception_ids, COALESCE(ex.acc, false) AS accepted
            FROM src_run s
            LEFT JOIN LATERAL (
              SELECT g.id AS gid, g.status AS gstatus
                FROM match_candidates mc JOIN match_groups g ON g.id = mc.match_group_id
               WHERE mc.record_table = s.rt AND mc.record_id = s.rid AND g.run_id = @run
               ORDER BY mc.selected DESC, g.id LIMIT 1) m ON true
            LEFT JOIN LATERAL (
              SELECT array_agg(e.id ORDER BY e.id) AS ids,
                     bool_and(e.status IN ('ACCEPTED_DIFFERENCE', 'FALSE_POSITIVE')) AS acc
                FROM exceptions e
               WHERE e.run_id = @run AND e.delivery_id = s.delivery_id AND e.source_row_id = s.source_row_id) ex ON true
        )
        """;

    public const string TxColumns =
        "id, kind, source, source_row_id, delivery_id, reference, account_id, ts, amount, currency, direction, source_status, lifecycle, match_status, match_group_id, exception_ids, accepted, key_a, key_b, fingerprint";

    /// <summary>CTE chain ending in <c>acc</c> (per account and currency): balances, independent recalculation from posted ledger entries, exceptions, state. Needs TxCte first.</summary>
    public const string AccCte = """
        rr AS (SELECT id, legal_entity_id, business_date FROM runs WHERE id = @run),
        bal AS (
          SELECT DISTINCT ON (ab.account_id, ab.currency)
                 ab.account_id, ab.currency::text AS currency, COALESCE(ab.opening_balance_amount, 0) AS opening, ab.balance_amount AS recorded
            FROM account_balances ab
            JOIN source_deliveries d ON d.id = ab.delivery_id AND d.run_id = @run
            JOIN rr ON ab.balance_date = rr.business_date
           WHERE ab.balance_type = 'DAILY_ROLLFORWARD'
           ORDER BY ab.account_id, ab.currency, d.received_at DESC NULLS LAST, ab.id DESC
        ),
        mov AS (
          SELECT le.account_id, le.currency::text AS currency,
                 COALESCE(SUM(le.amount) FILTER (WHERE le.debit_credit_indicator = 'CREDIT'), 0) AS credits,
                 COALESCE(SUM(le.amount) FILTER (WHERE le.debit_credit_indicator = 'DEBIT'), 0) AS debits
            FROM ledger_entries le
            JOIN source_deliveries d ON d.id = le.delivery_id AND d.run_id = @run
            JOIN rr ON le.value_date <= rr.business_date
           WHERE le.source_status = 'POSTED'
           GROUP BY le.account_id, le.currency
        ),
        acc_base AS (
          SELECT b.account_id, b.currency, b.opening, b.recorded, COALESCE(m.credits, 0) AS credits, COALESCE(m.debits, 0) AS debits,
                 b.opening + COALESCE(m.credits, 0) - COALESCE(m.debits, 0) AS independent
            FROM bal b LEFT JOIN mov m ON m.account_id = b.account_id AND m.currency = b.currency
        ),
        acc_ex AS (
          SELECT a.account_id, a.currency, e.id, e.status, e.blocking
            FROM acc_base a
            JOIN exceptions e ON e.run_id = @run
             AND (e.source_row_id = a.account_id
                  OR EXISTS (SELECT 1 FROM tx t WHERE t.account_id = a.account_id AND t.currency = a.currency
                                AND t.delivery_id = e.delivery_id AND t.source_row_id = e.source_row_id))
        ),
        acc_exs AS (
          SELECT account_id, currency, array_agg(DISTINCT id) AS ids,
                 bool_or(status NOT IN ('RESOLVED', 'ACCEPTED_DIFFERENCE', 'FALSE_POSITIVE')) AS open_any,
                 bool_or(blocking AND status NOT IN ('RESOLVED', 'ACCEPTED_DIFFERENCE', 'FALSE_POSITIVE')) AS open_blocking
            FROM acc_ex GROUP BY account_id, currency
        ),
        acc AS (
          SELECT a.*, rr.legal_entity_id, COALESCE(x.ids, ARRAY[]::text[]) AS exception_ids,
                 CASE WHEN COALESCE(x.open_blocking, false) THEN 'FAILED'
                      WHEN a.recorded <> a.independent OR COALESCE(x.open_any, false) THEN 'REVIEW_REQUIRED'
                      ELSE 'PASSED' END AS state
            FROM acc_base a CROSS JOIN rr
            LEFT JOIN acc_exs x ON x.account_id = a.account_id AND x.currency = a.currency
        )
        """;
}
