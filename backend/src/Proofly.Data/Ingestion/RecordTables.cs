using Npgsql;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Data.Ingestion;

/// <summary>Maps canonical records to their tables (0002_records.sql). Money columns are NUMERIC(38,6); no float anywhere.</summary>
internal static class RecordTables
{
    private sealed record Map(string Table, string[] Columns, Func<ISourceRecord, object?[]> Values);

    private static string W<T>(T v) where T : struct, Enum => WireNames.ToUpperSnake(v);
    private static DateTimeOffset U(DateTimeOffset t) => t.ToUniversalTime();

    private static readonly Dictionary<Type, Map> Maps = new()
    {
        [typeof(TransferInstruction)] = new("transfer_instructions",
            ["source_row_id", "instruction_id", "client_account_id", "legal_entity_id", "transaction_type", "amount", "currency",
             "instruction_timestamp", "requested_value_date", "source_status", "external_reference", "destination_reference"],
            r => { var x = (TransferInstruction)r; return [x.SourceRowId, x.InstructionId, x.ClientAccountId, x.LegalEntityId, x.TransactionType,
                x.Amount.Amount, x.Amount.Currency, U(x.InstructionTimestamp), x.RequestedValueDate, W(x.SourceStatus), x.ExternalReference, x.DestinationReference]; }),
        [typeof(DirectDebitEvent)] = new("direct_debit_events",
            ["source_row_id", "instruction_id", "mandate_reference", "client_account_id", "legal_entity_id", "amount", "currency",
             "collection_date", "event_type", "event_timestamp", "source_status", "return_reason", "original_instruction_reference"],
            r => { var x = (DirectDebitEvent)r; return [x.SourceRowId, x.InstructionId, x.MandateReference, x.ClientAccountId, x.LegalEntityId,
                x.Amount.Amount, x.Amount.Currency, x.CollectionDate, W(x.EventType), U(x.EventTimestamp), W(x.SourceStatus), x.ReturnReason, x.OriginalInstructionReference]; }),
        [typeof(CardEvent)] = new("card_events",
            ["source_row_id", "card_event_id", "card_transaction_id", "client_account_id", "legal_entity_id", "tokenized_card_reference",
             "event_type", "event_timestamp", "amount", "currency", "debit_credit_indicator", "merchant_reference", "settlement_id",
             "original_transaction_id", "source_status"],
            r => { var x = (CardEvent)r; return [x.SourceRowId, x.CardEventId, x.CardTransactionId, x.ClientAccountId, x.LegalEntityId, x.TokenizedCardReference,
                W(x.EventType), U(x.EventTimestamp), x.Amount.Amount, x.Amount.Currency, W(x.DebitCredit), x.MerchantReference, x.SettlementId,
                x.OriginalTransactionId, W(x.SourceStatus)]; }),
        [typeof(CardSettlement)] = new("card_settlements",
            ["source_row_id", "settlement_id", "processor", "legal_entity_id", "settlement_date", "gross_amount", "fees", "adjustments",
             "net_amount", "currency", "destination_account", "source_status"],
            r => { var x = (CardSettlement)r; return [x.SourceRowId, x.SettlementId, x.Processor, x.LegalEntityId, x.SettlementDate, x.GrossAmount.Amount,
                x.Fees.Amount, x.Adjustments.Amount, x.NetAmount.Amount, x.GrossAmount.Currency, x.DestinationAccount, W(x.SourceStatus)]; }),
        [typeof(LedgerEntry)] = new("ledger_entries",
            ["ledger_entry_id", "account_id", "legal_entity_id", "booking_timestamp", "value_date", "debit_credit_indicator", "amount",
             "currency", "transaction_reference", "entry_type", "source_status"],
            r => { var x = (LedgerEntry)r; return [x.LedgerEntryId, x.AccountId, x.LegalEntityId, U(x.BookingTimestamp), x.ValueDate, W(x.DebitCredit),
                x.Amount.Amount, x.Amount.Currency, x.TransactionReference, W(x.EntryType), W(x.SourceStatus)]; }),
        [typeof(AccountBalance)] = new("account_balances",
            ["account_id", "legal_entity_id", "balance_date", "balance_type", "opening_balance_amount", "balance_amount", "currency", "source_timestamp"],
            r => { var x = (AccountBalance)r; return [x.AccountId, x.LegalEntityId, x.BalanceDate, W(x.BalanceType), x.OpeningBalance?.Amount,
                x.Balance.Amount, x.Balance.Currency, U(x.SourceTimestamp)]; }),
        [typeof(BankTransaction)] = new("bank_transactions",
            ["external_transaction_id", "external_account_id", "booking_timestamp", "value_date", "amount", "currency", "debit_credit_indicator",
             "bank_reference", "transaction_status", "source_timestamp"],
            r => { var x = (BankTransaction)r; return [x.ExternalTransactionId, x.ExternalAccountId, U(x.BookingTimestamp), x.ValueDate, x.Amount.Amount,
                x.Amount.Currency, W(x.DebitCredit), x.BankReference, W(x.Status), U(x.SourceTimestamp)]; }),
        [typeof(SafeguardingBalance)] = new("safeguarding_balances",
            ["source_row_id", "external_account_id", "legal_entity_id", "balance_timestamp", "balance_type", "balance_amount", "currency",
             "evidence_reference", "source_timestamp"],
            r => { var x = (SafeguardingBalance)r; return [x.SourceRowId, x.ExternalAccountId, x.LegalEntityId, U(x.BalanceTimestamp), W(x.BalanceType),
                x.Balance.Amount, x.Balance.Currency, x.EvidenceReference, U(x.SourceTimestamp)]; }),
    };

    public static bool Supports(Type recordType) => Maps.ContainsKey(recordType);

    public static async Task InsertAsync(NpgsqlTransaction tx, string deliveryId, IReadOnlyList<(ISourceRecord Record, int RowNumber)> records, CancellationToken ct)
    {
        foreach (var group in records.GroupBy(r => r.Record.GetType()))
        {
            var map = Maps[group.Key];
            var cols = "delivery_id, row_number, " + string.Join(", ", map.Columns);
            var ps = string.Join(", ", Enumerable.Range(1, map.Columns.Length + 2).Select(i => "$" + i));
            var sql = $"INSERT INTO {map.Table} ({cols}) VALUES ({ps})";
            foreach (var chunk in group.Chunk(500))
            {
                await using var batch = new NpgsqlBatch(tx.Connection, tx);
                foreach (var (rec, rowNumber) in chunk)
                {
                    var cmd = new NpgsqlBatchCommand(sql);
                    cmd.Parameters.Add(new NpgsqlParameter { Value = deliveryId });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = rowNumber });
                    foreach (var v in map.Values(rec)) cmd.Parameters.Add(new NpgsqlParameter { Value = v ?? DBNull.Value });
                    batch.BatchCommands.Add(cmd);
                }
                await batch.ExecuteNonQueryAsync(ct);
            }
        }
    }
}
