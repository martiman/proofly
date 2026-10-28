using Npgsql;

namespace Proofly.Api.Tests.Transactions;

/// <summary>Neutral multi-currency fixture (GBP, EUR, JPY). Run R1 (LE-A, 2026-10-27) holds data; R2 (LE-B) holds one foreign transfer.</summary>
internal static class TxSeed
{
    public const string Sql = """
        INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-A', 'Entity A', 'GBP'), ('LE-B', 'Entity B', 'EUR');
        INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state)
          VALUES ('R1', 'LE-A', '2026-10-27', 1, 'rs-1', 'REVIEW_REQUIRED'), ('R2', 'LE-B', '2026-10-27', 1, 'rs-1', 'REVIEW_REQUIRED');
        INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, state)
          VALUES ('D1', 'R1', 'LE-A', 'mixed', '2026-10-27', '1', 'a.csv', repeat('a', 64), 'RECEIVED'),
                 ('D2', 'R2', 'LE-B', 'mixed', '2026-10-27', '1', 'b.csv', repeat('b', 64), 'RECEIVED');

        INSERT INTO transfer_instructions (delivery_id, source_row_id, instruction_id, client_account_id, legal_entity_id, transaction_type, amount, currency, instruction_timestamp, source_status, external_reference)
          VALUES ('D1', 'T-1', 'I-1', 'ACC-1', 'LE-A', 'OUTGOING', 100.00, 'GBP', '2026-10-27 09:00:00+00', 'SETTLED', 'EXT-1'),
                 ('D1', 'T-2', 'I-2', 'ACC-2', 'LE-A', 'OUTGOING', 50.50, 'EUR', '2026-10-27 10:00:00+00', 'SETTLED', NULL),
                 ('D2', 'T-9', 'I-9', 'ACC-9', 'LE-B', 'OUTGOING', 1.00, 'EUR', '2026-10-27 10:00:00+00', 'SETTLED', NULL);
        INSERT INTO direct_debit_events (delivery_id, source_row_id, instruction_id, client_account_id, legal_entity_id, amount, currency, event_type, event_timestamp, source_status)
          VALUES ('D1', 'DD-1', 'M-1', 'ACC-3', 'LE-A', 5000, 'JPY', 'SUBMITTED', '2026-10-27 08:00:00+00', 'SUBMITTED'),
                 ('D1', 'DD-2', 'M-1', 'ACC-3', 'LE-A', 5000, 'JPY', 'SETTLED', '2026-10-27 11:00:00+00', 'SETTLED');
        INSERT INTO card_events (delivery_id, source_row_id, card_event_id, card_transaction_id, client_account_id, legal_entity_id, event_type, event_timestamp, amount, currency, debit_credit_indicator, source_status)
          VALUES ('D1', 'C-1', 'CE-1', 'CT-1', 'ACC-1', 'LE-A', 'AUTH', '2026-10-27 12:00:00+00', 10.50, 'GBP', 'CREDIT', 'AUTHORISED');
        INSERT INTO bank_transactions (delivery_id, external_transaction_id, external_account_id, booking_timestamp, value_date, amount, currency, debit_credit_indicator, bank_reference, transaction_status)
          VALUES ('D1', 'B-1', 'ACC-1', '2026-10-27 09:30:00+00', '2026-10-27', 100.00, 'GBP', 'DEBIT', 'EXT-1', 'BOOKED');
        INSERT INTO ledger_entries (delivery_id, ledger_entry_id, account_id, legal_entity_id, booking_timestamp, value_date, debit_credit_indicator, amount, currency, transaction_reference, entry_type, source_status)
          VALUES ('D1', 'L-1', 'ACC-1', 'LE-A', '2026-10-27 07:00:00+00', '2026-10-27', 'CREDIT', 500.00, 'GBP', 'TOPUP-1', 'TRANSFER', 'POSTED'),
                 ('D1', 'L-2', 'ACC-1', 'LE-A', '2026-10-27 09:10:00+00', '2026-10-27', 'DEBIT', 100.00, 'GBP', 'I-1', 'TRANSFER', 'POSTED'),
                 ('D1', 'L-3', 'ACC-1', 'LE-A', '2026-10-27 09:20:00+00', '2026-10-27', 'DEBIT', 7.00, 'GBP', 'PEND-1', 'FEE', 'PENDING'),
                 ('D1', 'L-4', 'ACC-2', 'LE-A', '2026-10-27 10:10:00+00', '2026-10-27', 'CREDIT', 20.00, 'EUR', 'TOPUP-2', 'TRANSFER', 'POSTED');
        INSERT INTO account_balances (delivery_id, account_id, legal_entity_id, balance_date, balance_type, opening_balance_amount, balance_amount, currency)
          VALUES ('D1', 'ACC-1', 'LE-A', '2026-10-27', 'DAILY_ROLLFORWARD', 1000.00, 1400.00, 'GBP'),
                 ('D1', 'ACC-2', 'LE-A', '2026-10-27', 'DAILY_ROLLFORWARD', 100.00, 130.00, 'EUR'),
                 ('D1', 'ACC-3', 'LE-A', '2026-10-27', 'DAILY_ROLLFORWARD', 0, 0, 'JPY'),
                 ('D2', 'ACC-9', 'LE-B', '2026-10-27', 'DAILY_ROLLFORWARD', 0, 0, 'EUR');

        INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status, value_amount, value_currency, due_at, blocking, source_file, source_row_id, delivery_id)
          VALUES ('X-1', 'R1', 'LE-A', '2026-10-27', 'Pending fee', 'independent_ledger', 'LEDGER_PENDING', 'LOW', 'OPEN', 7.00, 'GBP', '2026-10-30 00:00:00+00', false, 'a.csv', 'L-3', 'D1'),
                 ('X-2', 'R1', 'LE-A', '2026-10-27', 'JPY account blocked', 'independent_ledger', 'ACCOUNT_BLOCK', 'HIGH', 'OPEN', NULL, NULL, '2026-10-30 00:00:00+00', true, 'a.csv', 'ACC-3', 'D1'),
                 ('X-3', 'R1', 'LE-A', '2026-10-27', 'Accepted card diff', 'matching', 'DIFF', 'LOW', 'ACCEPTED_DIFFERENCE', NULL, NULL, '2026-10-30 00:00:00+00', false, 'a.csv', 'C-1', 'D1');

        INSERT INTO match_groups (id, run_id, legal_entity_id, status) VALUES ('MG-1', 'R1', 'LE-A', 'MATCHED');
        INSERT INTO match_candidates (match_group_id, kind, record_table, record_id, amount, currency, selected)
          SELECT 'MG-1', 'BANK_TRANSFER', 'transfer_instructions', id, amount, currency, true FROM transfer_instructions WHERE source_row_id = 'T-1'
          UNION ALL
          SELECT 'MG-1', 'BANK_EVIDENCE', 'bank_transactions', id, amount, currency, true FROM bank_transactions WHERE external_transaction_id = 'B-1';
        """;

    public static async Task ApplyAsync(NpgsqlDataSource db)
    {
        await using var c = db.CreateCommand(Sql);
        await c.ExecuteNonQueryAsync();
    }
}
