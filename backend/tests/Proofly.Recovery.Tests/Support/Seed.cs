namespace Proofly.Recovery.Tests.Support;

/// <summary>Neutral GBP/EUR/JPY run inputs inserted as accepted canonical records (what a clean ingestion would leave).</summary>
public static class Seed
{
    public static readonly DateOnly Date = new(2026, 10, 27);
    public static readonly string[] Mandatory = ["transfer_instructions", "internal_ledger_entries", "client_account_balances", "banking_transactions", "safeguarding_balances"];

    private static string Fp(string s) => $"encode(sha256('{s}'::bytea),'hex')";

    public static async Task Users(RecoveryDb db) => await db.Exec(
        "INSERT INTO users (id, name, email) VALUES ('u-owner','Owner','owner@x.test'), ('u-signer','Signer','signer@x.test'), ('u-prep','Preparer','prep@x.test') ON CONFLICT DO NOTHING");

    /// <summary>One legal entity with all five mandatory deliveries: GBP shortfall, JPY covered, EUR surplus, JPY value mismatch.</summary>
    public static async Task RunInputs(RecoveryDb db, string le)
    {
        var sources = new (string Source, int Count)[] { ("transfer_instructions", 2), ("internal_ledger_entries", 2), ("client_account_balances", 3), ("banking_transactions", 2), ("safeguarding_balances", 3) };
        var sql = $"INSERT INTO legal_entities (id, name, base_currency, currencies) VALUES ('{le}','Entity {le}','GBP','{{EUR,JPY}}');\n";
        foreach (var (s, n) in sources)
        {
            var id = $"D-{le}-{s}";
            sql += $"""
                INSERT INTO source_deliveries (id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, declared_record_count, received_at, state,
                                               accepted_record_count, rejected_record_count, total_row_count, ingested_at)
                VALUES ('{id}','{le}','{s}','2026-10-27','1.0','{s}.csv',{Fp(id)},{n},'2026-10-28T06:00Z','RECEIVED',{n},0,{n},now());

                """;
        }
        string D(string s) => $"D-{le}-{s}";
        sql += $"""
            INSERT INTO transfer_instructions (delivery_id, row_number, source_row_id, instruction_id, client_account_id, legal_entity_id, transaction_type, amount, currency,
                instruction_timestamp, requested_value_date, source_status, external_reference) VALUES
              ('{D("transfer_instructions")}',1,'S-1','TRF-1','A1','{le}','BANK_TRANSFER',100,'GBP','2026-10-27T09:00Z','2026-10-27','SETTLED','REF-1'),
              ('{D("transfer_instructions")}',2,'S-2','TRF-2','A2','{le}','BANK_TRANSFER',5000,'JPY','2026-10-27T09:00Z','2026-10-27','SETTLED','REF-2');
            INSERT INTO ledger_entries (delivery_id, row_number, ledger_entry_id, account_id, legal_entity_id, booking_timestamp, value_date, debit_credit_indicator, amount, currency,
                transaction_reference, entry_type, source_status) VALUES
              ('{D("internal_ledger_entries")}',1,'L-1','A1','{le}','2026-10-27T10:00Z','2026-10-27','DEBIT',100,'GBP','TRF-1','TRANSFER','POSTED'),
              ('{D("internal_ledger_entries")}',2,'L-2','A2','{le}','2026-10-27T10:00Z','2026-10-27','DEBIT',5000,'JPY','TRF-2','TRANSFER','POSTED');
            INSERT INTO account_balances (delivery_id, row_number, account_id, legal_entity_id, balance_date, balance_type, opening_balance_amount, balance_amount, currency, source_timestamp) VALUES
              ('{D("client_account_balances")}',1,'A1','{le}','2026-10-27','DAILY_ROLLFORWARD',1000,900,'GBP','2026-10-27T23:59:59Z'),
              ('{D("client_account_balances")}',2,'A2','{le}','2026-10-27','DAILY_ROLLFORWARD',20000,15000,'JPY','2026-10-27T23:59:59Z'),
              ('{D("client_account_balances")}',3,'A3','{le}','2026-10-27','DAILY_ROLLFORWARD',500,500,'EUR','2026-10-27T23:59:59Z');
            INSERT INTO bank_transactions (delivery_id, row_number, external_transaction_id, external_account_id, booking_timestamp, value_date, amount, currency,
                debit_credit_indicator, bank_reference, transaction_status, source_timestamp) VALUES
              ('{D("banking_transactions")}',1,'BTX-1','EXT-1','2026-10-27T11:00Z','2026-10-27',100,'GBP','DEBIT','REF-1','BOOKED','2026-10-28T06:00Z'),
              ('{D("banking_transactions")}',2,'BTX-2','EXT-1','2026-10-27T11:00Z','2026-10-27',5200,'JPY','DEBIT','REF-2','BOOKED','2026-10-28T06:00Z');
            INSERT INTO safeguarding_balances (delivery_id, row_number, source_row_id, external_account_id, legal_entity_id, balance_timestamp, balance_type, balance_amount, currency,
                evidence_reference, source_timestamp) VALUES
              ('{D("safeguarding_balances")}',1,'SG-1','SGA-GBP','{le}','2026-10-27T23:59:59Z','CLOSING_AVAILABLE',800,'GBP','STMT-1','2026-10-28T06:30Z'),
              ('{D("safeguarding_balances")}',2,'SG-2','SGA-JPY','{le}','2026-10-27T23:59:59Z','CLOSING_AVAILABLE',15000,'JPY','STMT-2','2026-10-28T06:30Z'),
              ('{D("safeguarding_balances")}',3,'SG-3','SGA-EUR','{le}','2026-10-27T23:59:59Z','CLOSING_AVAILABLE',600,'EUR','STMT-3','2026-10-28T06:30Z');
            """;
        await db.Exec(sql);
    }
}
