using Npgsql;
using Proofly.Connectors.Abstractions;
using Proofly.Controls.Completeness;
using Proofly.Controls.Safeguarding;
using Proofly.Data.Ingestion;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Data.Orchestration;

/// <summary>
/// Loads the evidence of a run from the database inside the caller's transaction: the deliveries of the legal entity
/// and business date that are ingested (or rejected at registration), the rejected rows of the non-superseded ones,
/// the accepted canonical records of the deliveries that are RECEIVED, LATE or QUALIFIED (never superseded or rejected
/// ones, so a correction replaces the delivery it corrects and a duplicate delivery adds nothing), and the run's adjustments.
/// </summary>
internal static class RunInputLoader
{
    public sealed record Loaded(RunInputs Inputs, IReadOnlyList<(string DeliveryId, string Source, string Fingerprint, string State)> Manifest, DateTimeOffset? LastArrival);

    public static async Task<Loaded> LoadAsync(NpgsqlTransaction tx, string runId, string legalEntityId, DateOnly date, IReadOnlyList<string> mandatory, CancellationToken ct)
    {
        var conn = tx.Connection!;
        NpgsqlCommand Cmd(string sql) => new(sql, conn, tx);

        var totals = new Dictionary<string, (List<Money> Declared, List<Money> Actual)>();
        await using (var c = Cmd("""
            SELECT t.delivery_id, t.currency::text, t.declared_amount, t.actual_amount FROM delivery_control_totals t
            JOIN source_deliveries d ON d.id = t.delivery_id WHERE d.legal_entity_id = @le AND d.business_date = @d ORDER BY t.delivery_id, t.currency
            """))
        {
            c.Parameters.AddWithValue("le", legalEntityId);
            c.Parameters.AddWithValue("d", date);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetString(0);
                if (!totals.TryGetValue(id, out var t)) totals[id] = t = ([], []);
                if (!r.IsDBNull(2)) t.Declared.Add(new Money(r.GetDecimal(2), r.GetString(1)));
                if (!r.IsDBNull(3)) t.Actual.Add(new Money(r.GetDecimal(3), r.GetString(1)));
            }
        }
        var failures = new Dictionary<string, HashSet<string>>();
        await using (var c = Cmd("""
            SELECT r.delivery_id, r.reason_code FROM delivery_rejections r JOIN source_deliveries d ON d.id = r.delivery_id
            WHERE d.legal_entity_id = @le AND d.business_date = @d AND r.row_number IS NULL AND NOT r.superseded_by_retry
            """))
        {
            c.Parameters.AddWithValue("le", legalEntityId);
            c.Parameters.AddWithValue("d", date);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) (failures.TryGetValue(r.GetString(0), out var s) ? s : failures[r.GetString(0)] = []).Add(r.GetString(1));
        }

        var deliveries = new List<EvidenceDelivery>();
        var manifest = new List<(string, string, string, string)>();
        var recordDeliveries = new List<string>();
        DateTimeOffset? lastArrival = null;
        await using (var c = Cmd("""
            SELECT id, source_name, file_name, fingerprint_sha256, declared_record_count, COALESCE(total_row_count, accepted_record_count + rejected_record_count),
                   rejected_record_count, supersedes_delivery_id, sequence_number, expected_by, received_at, state, schema_version, ingested_at
            FROM source_deliveries
            WHERE legal_entity_id = @le AND business_date = @d AND (ingested_at IS NOT NULL OR state = 'REJECTED')
            ORDER BY id
            """))
        {
            c.Parameters.AddWithValue("le", legalEntityId);
            c.Parameters.AddWithValue("d", date);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetString(0);
                var state = DeliveryStates.Parse(r.GetString(11));
                var fp = r.GetString(3).Trim();
                var fail = failures.GetValueOrDefault(id) ?? [];
                var t = totals.GetValueOrDefault(id);
                var seq = fail.Contains(ReasonCodes.SequenceGap) ? SequenceStatus.Gap : r.IsDBNull(8) ? SequenceStatus.NotApplicable : SequenceStatus.Ok;
                var received = r.IsDBNull(10) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(10);
                var info = new DeliveryInfo(id, r.GetString(1), state, fp, r.IsDBNull(4) ? null : r.GetInt32(4), r.GetInt32(5),
                    t.Declared ?? [], t.Actual ?? [], !fail.Contains(ReasonCodes.SchemaUnsupported), seq, r.GetInt32(6),
                    r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9), received);
                deliveries.Add(new EvidenceDelivery(info, r.GetString(2)));
                manifest.Add((id, r.GetString(1), fp, r.GetString(11)));
                if (state is DeliveryState.Received or DeliveryState.Late or DeliveryState.Qualified && !r.IsDBNull(13)) recordDeliveries.Add(id);
                if (received is { } rc && (lastArrival is null || rc > lastArrival)) lastArrival = rc;
            }
        }

        var rejections = new List<RejectedRecord>();
        await using (var c = Cmd("""
            SELECT r.delivery_id, d.source_name, r.row_number, r.source_row_id, r.reason_code, r.reason
            FROM delivery_rejections r JOIN source_deliveries d ON d.id = r.delivery_id
            WHERE d.legal_entity_id = @le AND d.business_date = @d AND d.state <> 'SUPERSEDED' AND r.row_number IS NOT NULL AND NOT r.superseded_by_retry
            ORDER BY r.delivery_id, r.row_number, r.id
            """))
        {
            c.Parameters.AddWithValue("le", legalEntityId);
            c.Parameters.AddWithValue("d", date);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rejections.Add(new RejectedRecord(r.GetString(1), r.GetString(0), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5)));
        }

        var records = new List<LoadedRecord>();
        var ids = recordDeliveries.ToArray();
        foreach (var reader in Readers)
        {
            await using var c = Cmd(reader.Sql);
            c.Parameters.AddWithValue("ids", ids);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var rec = reader.Read(r);
                var (table, source) = RecordKinds.Of(rec);
                records.Add(new LoadedRecord(table, r.GetInt64(0), r.GetString(1), source, rec, r.IsDBNull(2) ? null : r.GetInt32(2)));
            }
        }

        var adjustments = new List<AdjustmentInput>();
        await using (var c = Cmd("SELECT id, amount, currency::text, status, expires_on FROM safeguarding_adjustments WHERE run_id = @r ORDER BY id"))
        {
            c.Parameters.AddWithValue("r", runId);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                adjustments.Add(new AdjustmentInput(r.GetString(0), new Money(r.GetDecimal(1), r.GetString(2)), Enum<AdjustmentStatus>(r.GetString(3)),
                    r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4)));
        }

        var inputs = new RunInputs(runId, legalEntityId, date, mandatory, deliveries, rejections, records, adjustments);
        return new Loaded(inputs, manifest, lastArrival);
    }

    private static T Enum<T>(string wire) where T : struct, Enum =>
        WireEnum.TryParse<T>(wire, out var v) ? v : throw new InvalidOperationException($"Stored value '{wire}' is not a valid {typeof(T).Name}.");

    private static Money Mn(NpgsqlDataReader r, int amount, int currency) => new(r.GetDecimal(amount), r.GetString(currency));
    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateOnly? D(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i);
    private static DateTimeOffset T(NpgsqlDataReader r, int i) => r.GetFieldValue<DateTimeOffset>(i);

    private sealed record TableReader(string Sql, Func<NpgsqlDataReader, ISourceRecord> Read);

    // Column 0 = id, 1 = delivery_id, 2 = row_number; the record columns follow.
    private static readonly TableReader[] Readers =
    [
        new("""
            SELECT id, delivery_id, row_number, source_row_id, instruction_id, client_account_id, legal_entity_id, transaction_type, amount, currency::text,
                   instruction_timestamp, requested_value_date, source_status, external_reference, destination_reference
            FROM transfer_instructions WHERE delivery_id = ANY(@ids) ORDER BY id
            """,
            r => new TransferInstruction(r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), Mn(r, 8, 9), T(r, 10), D(r, 11),
                Enum<TransferStatus>(r.GetString(12)), S(r, 13), S(r, 14))),
        new("""
            SELECT id, delivery_id, row_number, source_row_id, instruction_id, mandate_reference, client_account_id, legal_entity_id, amount, currency::text,
                   collection_date, event_type, event_timestamp, source_status, return_reason, original_instruction_reference
            FROM direct_debit_events WHERE delivery_id = ANY(@ids) ORDER BY id
            """,
            r => new DirectDebitEvent(r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), Mn(r, 8, 9), D(r, 10),
                Enum<DirectDebitEventType>(r.GetString(11)), T(r, 12), Enum<DirectDebitStatus>(r.GetString(13)), S(r, 14), S(r, 15))),
        new("""
            SELECT id, delivery_id, row_number, source_row_id, card_event_id, card_transaction_id, client_account_id, legal_entity_id, tokenized_card_reference,
                   event_type, event_timestamp, amount, currency::text, debit_credit_indicator, merchant_reference, settlement_id, original_transaction_id, source_status
            FROM card_events WHERE delivery_id = ANY(@ids) ORDER BY id
            """,
            r => new CardEvent(r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), S(r, 8), Enum<CardEventType>(r.GetString(9)), T(r, 10),
                Mn(r, 11, 12), Enum<DebitCredit>(r.GetString(13)), S(r, 14), S(r, 15), S(r, 16), Enum<CardEventStatus>(r.GetString(17)))),
        new("""
            SELECT id, delivery_id, row_number, source_row_id, settlement_id, processor, legal_entity_id, settlement_date, gross_amount, fees, adjustments,
                   net_amount, currency::text, destination_account, source_status
            FROM card_settlements WHERE delivery_id = ANY(@ids) ORDER BY id
            """,
            r =>
            {
                var ccy = r.GetString(12);
                return new CardSettlement(r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetFieldValue<DateOnly>(7), new Money(r.GetDecimal(8), ccy),
                    new Money(r.GetDecimal(9), ccy), new Money(r.GetDecimal(10), ccy), new Money(r.GetDecimal(11), ccy), r.GetString(13), Enum<SettlementStatus>(r.GetString(14)));
            }),
        new("""
            SELECT id, delivery_id, row_number, ledger_entry_id, account_id, legal_entity_id, booking_timestamp, value_date, debit_credit_indicator, amount, currency::text,
                   transaction_reference, entry_type, source_status
            FROM ledger_entries WHERE delivery_id = ANY(@ids) ORDER BY id
            """,
            r => new LedgerEntry(r.GetString(3), r.GetString(4), r.GetString(5), T(r, 6), r.GetFieldValue<DateOnly>(7), Enum<DebitCredit>(r.GetString(8)), Mn(r, 9, 10),
                S(r, 11), Enum<LedgerEntryType>(r.GetString(12)), Enum<LedgerEntryStatus>(r.GetString(13)))),
        new("""
            SELECT id, delivery_id, row_number, account_id, legal_entity_id, balance_date, balance_type, opening_balance_amount, balance_amount, currency::text, source_timestamp
            FROM account_balances WHERE delivery_id = ANY(@ids) ORDER BY id
            """,
            r =>
            {
                var ccy = r.GetString(9);
                return new AccountBalance(r.GetString(3), r.GetString(4), r.GetFieldValue<DateOnly>(5), Enum<AccountBalanceType>(r.GetString(6)),
                    r.IsDBNull(7) ? null : new Money(r.GetDecimal(7), ccy), new Money(r.GetDecimal(8), ccy), T(r, 10));
            }),
        new("""
            SELECT id, delivery_id, row_number, external_transaction_id, external_account_id, booking_timestamp, value_date, amount, currency::text,
                   debit_credit_indicator, bank_reference, transaction_status, source_timestamp
            FROM bank_transactions WHERE delivery_id = ANY(@ids) ORDER BY id
            """,
            r => new BankTransaction(r.GetString(3), r.GetString(4), T(r, 5), r.GetFieldValue<DateOnly>(6), Mn(r, 7, 8), Enum<DebitCredit>(r.GetString(9)), S(r, 10),
                Enum<BankTransactionStatus>(r.GetString(11)), T(r, 12))),
        new("""
            SELECT id, delivery_id, row_number, source_row_id, external_account_id, legal_entity_id, balance_timestamp, balance_type, balance_amount, currency::text,
                   evidence_reference, source_timestamp
            FROM safeguarding_balances WHERE delivery_id = ANY(@ids) ORDER BY id
            """,
            r => new SafeguardingBalance(r.GetString(3), r.GetString(4), r.GetString(5), T(r, 6), Enum<SafeguardingBalanceType>(r.GetString(7)), Mn(r, 8, 9),
                r.GetString(10), T(r, 11))),
    ];
}
