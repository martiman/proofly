namespace Proofly.Data.Transactions;

/// <summary>
/// Shared SQL fragments. The five record tables are unified with a UNION ALL inside a CTE (no database view, so the
/// schema stays untouched). Every query is scoped to one run through source_deliveries.run_id (parameter @run).
/// </summary>
internal static class TransactionSql
{
    /// <summary>CTE <c>src</c> (the five record tables, normalised) and <c>src_run</c> (those of the run): cheap columns only, no enrichment.</summary>
    public const string SrcCte = """
        src AS NOT MATERIALIZED (
          SELECT 'TRF-' || t.id AS id, 'BANK_TRANSFER' AS kind, 'transfer_instructions' AS source, 'transfer_instructions' AS rt, t.id AS rid,
                 t.delivery_id, t.source_row_id, COALESCE(t.external_reference, t.instruction_id) AS reference, t.client_account_id AS account_id,
                 t.instruction_timestamp AS ts, t.amount, t.currency::text AS currency, 'DEBIT'::text AS direction, t.source_status,
                 t.instruction_id AS key_a, t.external_reference AS key_b, t.row_number AS rn
            FROM transfer_instructions t
          UNION ALL
          SELECT 'DD-' || t.id, 'DIRECT_DEBIT', 'direct_debit_events', 'direct_debit_events', t.id,
                 t.delivery_id, t.source_row_id, t.instruction_id, t.client_account_id,
                 t.event_timestamp, t.amount, t.currency::text, 'CREDIT'::text, t.source_status,
                 t.instruction_id, NULL::text, t.row_number
            FROM direct_debit_events t
          UNION ALL
          SELECT 'CRD-' || t.id, 'CARD', 'card_transactions', 'card_events', t.id,
                 t.delivery_id, t.source_row_id, t.card_transaction_id, t.client_account_id,
                 t.event_timestamp, t.amount, t.currency::text, t.debit_credit_indicator, t.source_status,
                 t.card_transaction_id, NULL::text, t.row_number
            FROM card_events t
          UNION ALL
          SELECT 'BNK-' || t.id, 'BANK_EVIDENCE', 'banking_transactions', 'bank_transactions', t.id,
                 t.delivery_id, t.external_transaction_id, COALESCE(t.bank_reference, t.external_transaction_id), t.external_account_id,
                 t.booking_timestamp, t.amount, t.currency::text, t.debit_credit_indicator, t.transaction_status,
                 t.bank_reference, NULL::text, t.row_number
            FROM bank_transactions t
          UNION ALL
          SELECT 'LED-' || t.id, 'LEDGER_ENTRY', 'internal_ledger_entries', 'ledger_entries', t.id,
                 t.delivery_id, t.ledger_entry_id, COALESCE(t.transaction_reference, t.ledger_entry_id), t.account_id,
                 t.booking_timestamp, t.amount, t.currency::text, t.debit_credit_indicator, t.source_status,
                 t.transaction_reference, NULL::text, t.row_number
            FROM ledger_entries t
        ),
        src_run AS NOT MATERIALIZED (
          SELECT s.*, d.fingerprint_sha256 AS fingerprint
            FROM src s JOIN source_deliveries d ON d.id = s.delivery_id AND d.run_id = @run
        )
        """;

    /// <summary>
    /// SELECT enriching the rows of <paramref name="from"/> (a CTE of src_run columns) with lifecycle, match group and exceptions. Every
    /// enrichment is a per-row index lookup (lifecycle: the rows sharing the instruction/reference key, by the per-table key indexes), so
    /// it costs in proportion to the rows selected, not to the size of the run: select the page first, enrich after.
    /// </summary>
    public static string Enrich(string from) => """
          SELECT s.*,
                 lc.lifecycle,
                 COALESCE(m.gstatus, 'UNMATCHED') AS match_status, m.gid AS match_group_id,
                 COALESCE(ex.ids, ARRAY[]::text[]) AS exception_ids, COALESCE(ex.acc, false) AS accepted
            FROM {FROM} s
            LEFT JOIN LATERAL (
              -- source_status of every record of the same kind and key (partition) in transaction order; a record without key is its own partition
              SELECT array_agg(x.st ORDER BY x.ts, x.id) AS lifecycle FROM (
                SELECT t.source_status AS st, t.instruction_timestamp AS ts, 'TRF-' || t.id AS id FROM transfer_instructions t
                  JOIN source_deliveries dl ON dl.id = t.delivery_id AND dl.run_id = @run
                 WHERE s.kind = 'BANK_TRANSFER' AND s.key_a IS NOT NULL AND t.instruction_id = s.key_a
                UNION ALL
                SELECT t.source_status, t.event_timestamp, 'DD-' || t.id FROM direct_debit_events t
                  JOIN source_deliveries dl ON dl.id = t.delivery_id AND dl.run_id = @run
                 WHERE s.kind = 'DIRECT_DEBIT' AND s.key_a IS NOT NULL AND t.instruction_id = s.key_a
                UNION ALL
                SELECT t.source_status, t.event_timestamp, 'CRD-' || t.id FROM card_events t
                  JOIN source_deliveries dl ON dl.id = t.delivery_id AND dl.run_id = @run
                 WHERE s.kind = 'CARD' AND s.key_a IS NOT NULL AND t.card_transaction_id = s.key_a
                UNION ALL
                SELECT t.transaction_status, t.booking_timestamp, 'BNK-' || t.id FROM bank_transactions t
                  JOIN source_deliveries dl ON dl.id = t.delivery_id AND dl.run_id = @run
                 WHERE s.kind = 'BANK_EVIDENCE' AND s.key_a IS NOT NULL AND t.bank_reference = s.key_a
                UNION ALL
                SELECT t.source_status, t.booking_timestamp, 'LED-' || t.id FROM ledger_entries t
                  JOIN source_deliveries dl ON dl.id = t.delivery_id AND dl.run_id = @run
                 WHERE s.kind = 'LEDGER_ENTRY' AND s.key_a IS NOT NULL AND t.transaction_reference = s.key_a
                UNION ALL
                SELECT s.source_status, s.ts, s.id WHERE s.key_a IS NULL
              ) x) lc ON true
            LEFT JOIN LATERAL (
              SELECT g.id AS gid, g.status AS gstatus
                FROM match_candidates mc JOIN match_groups g ON g.id = mc.match_group_id
               WHERE mc.record_table = s.rt AND mc.record_id = s.rid AND g.run_id = @run
               ORDER BY mc.selected DESC, g.id LIMIT 1) m ON true
            LEFT JOIN LATERAL (
              SELECT array_agg(x.id ORDER BY x.id) AS ids,
                     bool_and(x.status IN ('ACCEPTED_DIFFERENCE', 'FALSE_POSITIVE')) AS acc
                FROM (SELECT e.id, e.status FROM exceptions e
                       WHERE e.run_id = @run AND e.delivery_id = s.delivery_id AND e.source_row_id = s.source_row_id
                      UNION
                      SELECT e.id, e.status FROM exceptions e JOIN exception_links l ON l.exception_id = e.id
                       WHERE e.run_id = @run AND l.record_table = s.rt AND l.record_id = s.rid) x) ex ON true
        """.Replace("{FROM}", from);

    /// <summary>CTE chain ending in <c>tx</c>: one row per normalised transaction of the run, enriched, for callers that filter on enriched columns or look one up by an enriched value.</summary>
    public static readonly string TxCte = SrcCte + ",\n        tx AS (" + Enrich("src_run") + "        )";


    public const string TxColumns =
        "id, kind, source, source_row_id, delivery_id, reference, account_id, ts, amount, currency, direction, source_status, lifecycle, match_status, match_group_id, exception_ids, accepted, key_a, key_b, fingerprint, rn, rid";

    /// <summary>CTEs up to <c>acc_base</c> (per account and currency): balances from the control's persisted result (fallback: independent recalculation from posted ledger entries while the run has none). @acct is NULL for all accounts or one account id.</summary>
    public const string AccBaseCte = """
        rr AS (SELECT id, legal_entity_id, business_date FROM runs WHERE id = @run),
        bal AS (
          SELECT DISTINCT ON (ab.account_id, ab.currency)
                 ab.account_id, ab.currency::text AS currency, COALESCE(ab.opening_balance_amount, 0) AS opening, ab.balance_amount AS recorded
            FROM account_balances ab
            JOIN source_deliveries d ON d.id = ab.delivery_id AND d.run_id = @run
            JOIN rr ON ab.balance_date = rr.business_date
           WHERE ab.balance_type = 'DAILY_ROLLFORWARD' AND (@acct::text IS NULL OR ab.account_id = @acct)
           ORDER BY ab.account_id, ab.currency, d.received_at DESC NULLS LAST, ab.id DESC
        ),
        mov AS (
          SELECT le.account_id, le.currency::text AS currency,
                 COALESCE(SUM(le.amount) FILTER (WHERE le.debit_credit_indicator = 'CREDIT'), 0) AS credits,
                 COALESCE(SUM(le.amount) FILTER (WHERE le.debit_credit_indicator = 'DEBIT'), 0) AS debits
            FROM ledger_entries le
            JOIN source_deliveries d ON d.id = le.delivery_id AND d.run_id = @run
            JOIN rr ON le.value_date <= rr.business_date
           WHERE le.source_status = 'POSTED' AND (@acct::text IS NULL OR le.account_id = @acct)
           GROUP BY le.account_id, le.currency
        ),
        acc_base AS (
          -- The independent-ledger control's persisted result (account_control_results) is authoritative ...
          SELECT c.account_id, c.currency::text AS currency, c.opening, c.recorded, c.credits, c.debits, c.adjustments, c.independent, c.difference
            FROM account_control_results c WHERE c.run_id = @run AND (@acct::text IS NULL OR c.account_id = @acct)
          UNION ALL
          -- ... the recomputation from POSTED ledger entries applies only while the run has no persisted control result.
          SELECT b.account_id, b.currency, b.opening, b.recorded, COALESCE(m.credits, 0), COALESCE(m.debits, 0), 0::numeric,
                 b.opening + COALESCE(m.credits, 0) - COALESCE(m.debits, 0),
                 b.opening + COALESCE(m.credits, 0) - COALESCE(m.debits, 0) - b.recorded
            FROM bal b LEFT JOIN mov m ON m.account_id = b.account_id AND m.currency = b.currency
           WHERE NOT EXISTS (SELECT 1 FROM account_control_results x WHERE x.run_id = @run)
        )
        """;

    /// <summary>
    /// CTEs <c>acc_ex</c>, <c>acc_exs</c> and <c>acc</c> over the account rows of <paramref name="baseName"/> (acc_base, or a page of it): the exceptions of
    /// each account and its state. <paramref name="transactionScope"/> is a predicate on <c>s.account_id</c> that narrows the transaction branches to the
    /// accounts wanted (all, one, or the accounts of a page); an account's exceptions are those raised on the account itself and those on its transactions
    /// (by delivery/row or by explicit link): three index-friendly branches instead of testing every account against every exception.
    /// </summary>
    public static string AccExCte(string baseName, string transactionScope) => """
        acc_ex AS (
          SELECT a.account_id, a.currency, e.id, e.status, e.blocking
            FROM {BASE} a JOIN exceptions e ON e.run_id = @run AND e.source_row_id = a.account_id
          UNION
          SELECT s.account_id, s.currency, e.id, e.status, e.blocking
            FROM src_run s JOIN exceptions e ON e.run_id = @run AND e.delivery_id = s.delivery_id AND e.source_row_id = s.source_row_id
           WHERE {SCOPE}
          UNION
          SELECT s.account_id, s.currency, e.id, e.status, e.blocking
            FROM src_run s JOIN exception_links l ON l.record_table = s.rt AND l.record_id = s.rid
                           JOIN exceptions e ON e.id = l.exception_id AND e.run_id = @run
           WHERE {SCOPE}
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
                      WHEN a.difference IS DISTINCT FROM 0 OR COALESCE(x.open_any, false) THEN 'REVIEW_REQUIRED'
                      ELSE 'PASSED' END AS state
            FROM {BASE} a CROSS JOIN rr
            LEFT JOIN acc_exs x ON x.account_id = a.account_id AND x.currency = a.currency
        )
        """.Replace("{BASE}", baseName).Replace("{SCOPE}", transactionScope);

    /// <summary>CTE chain ending in <c>acc</c> for all accounts (@acct NULL) or one (@acct): balances, exceptions, state. Needs SrcCte first (it uses src_run).</summary>
    public static readonly string AccCte = AccBaseCte + ",\n" + AccExCte("acc_base", "(@acct::text IS NULL OR s.account_id = @acct)");
}
