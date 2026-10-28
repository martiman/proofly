using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Mapping;
using Proofly.Domain;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Schemas;

/// <summary>
/// The per-source schemas (version 1.0 = the layouts of docs/integrations/input-contracts.md and the synthetic
/// fixture headers). Optionality follows the canonical record: a field the record types as nullable is optional.
/// Rules in the builders: settlement net must equal gross - fees + adjustments (CONTROL_VALUE_MISMATCH);
/// transaction/instruction amounts, settlement gross and fees are non-negative magnitudes (direction is a separate
/// field); client account balances may be negative; a negative safeguarding balance is INVALID_VALUE (explicit
/// policy needed, expected finding F-015).
/// </summary>
public static class SourceSchemas
{
    public const string V1 = "1.0";

    private static IReadOnlyDictionary<string, IReadOnlyList<ColumnSpec>> Versions(params ColumnSpec[] v1) =>
        new Dictionary<string, IReadOnlyList<ColumnSpec>> { [V1] = v1 };

    private const AmountSign NonNeg = AmountSign.NonNegative;

    public static readonly SourceSchema<TransferInstruction> TransferInstructions = new(
        SourceNames.TransferInstructions,
        Versions(
            ColumnSpec.Text("source_row_id", maxLength: 64), ColumnSpec.Text("instruction_id", maxLength: 64),
            ColumnSpec.Text("client_account_id", maxLength: 64), ColumnSpec.Text("legal_entity_id", maxLength: 64),
            ColumnSpec.Text("transaction_type", maxLength: 40), ColumnSpec.Amount("amount", sign: NonNeg), ColumnSpec.Currency(),
            ColumnSpec.Timestamp("instruction_timestamp"), ColumnSpec.Date("requested_value_date", required: false),
            ColumnSpec.Enum<TransferStatus>("source_status"), ColumnSpec.Text("external_reference", false, 64),
            ColumnSpec.Text("destination_reference", false, 64)),
        "source_row_id", "amount",
        v => new TransferInstruction(v.Str("source_row_id"), v.Str("instruction_id"), v.Str("client_account_id"), v.Str("legal_entity_id"),
            v.Str("transaction_type"), v.Money("amount"), v.Ts("instruction_timestamp"), v.OptDate("requested_value_date"),
            v.Enum<TransferStatus>("source_status"), v.OptStr("external_reference"), v.OptStr("destination_reference")));

    public static readonly SourceSchema<DirectDebitEvent> DirectDebitEvents = new(
        SourceNames.DirectDebitEvents,
        Versions(
            ColumnSpec.Text("source_row_id", maxLength: 64), ColumnSpec.Text("instruction_id", maxLength: 64),
            ColumnSpec.Text("mandate_reference", maxLength: 64), ColumnSpec.Text("client_account_id", maxLength: 64),
            ColumnSpec.Text("legal_entity_id", maxLength: 64), ColumnSpec.Amount("amount", sign: NonNeg), ColumnSpec.Currency(),
            ColumnSpec.Date("collection_date", required: false), ColumnSpec.Enum<DirectDebitEventType>("event_type"),
            ColumnSpec.Timestamp("event_timestamp"), ColumnSpec.Enum<DirectDebitStatus>("source_status"),
            ColumnSpec.Text("return_reason", false, 64), ColumnSpec.Text("original_instruction_reference", false, 64)),
        "source_row_id", "amount",
        v => new DirectDebitEvent(v.Str("source_row_id"), v.Str("instruction_id"), v.Str("mandate_reference"), v.Str("client_account_id"),
            v.Str("legal_entity_id"), v.Money("amount"), v.OptDate("collection_date"), v.Enum<DirectDebitEventType>("event_type"),
            v.Ts("event_timestamp"), v.Enum<DirectDebitStatus>("source_status"), v.OptStr("return_reason"), v.OptStr("original_instruction_reference")));

    public static readonly SourceSchema<LedgerEntry> InternalLedgerEntries = new(
        SourceNames.InternalLedgerEntries,
        Versions(
            ColumnSpec.Text("ledger_entry_id", maxLength: 64), ColumnSpec.Text("account_id", maxLength: 64),
            ColumnSpec.Text("legal_entity_id", maxLength: 64), ColumnSpec.Timestamp("booking_timestamp"), ColumnSpec.Date("value_date"),
            ColumnSpec.Enum<DebitCredit>("debit_credit_indicator"), ColumnSpec.Amount("amount", sign: NonNeg), ColumnSpec.Currency(),
            ColumnSpec.Text("transaction_reference", false, 64), ColumnSpec.Enum<LedgerEntryType>("entry_type"),
            ColumnSpec.Enum<LedgerEntryStatus>("source_status")),
        "ledger_entry_id", "amount",
        v => new LedgerEntry(v.Str("ledger_entry_id"), v.Str("account_id"), v.Str("legal_entity_id"), v.Ts("booking_timestamp"),
            v.Date("value_date"), v.Enum<DebitCredit>("debit_credit_indicator"), v.Money("amount"), v.OptStr("transaction_reference"),
            v.Enum<LedgerEntryType>("entry_type"), v.Enum<LedgerEntryStatus>("source_status")));

    public static readonly SourceSchema<AccountBalance> ClientAccountBalances = new(
        SourceNames.ClientAccountBalances,
        Versions(
            ColumnSpec.Text("account_id", maxLength: 64), ColumnSpec.Text("legal_entity_id", maxLength: 64), ColumnSpec.Date("balance_date"),
            ColumnSpec.Enum<AccountBalanceType>("balance_type"), ColumnSpec.Amount("opening_balance_amount", required: false),
            ColumnSpec.Amount("balance_amount"), ColumnSpec.Currency(), ColumnSpec.Timestamp("source_timestamp")),
        "account_id", "balance_amount",
        v => new AccountBalance(v.Str("account_id"), v.Str("legal_entity_id"), v.Date("balance_date"), v.Enum<AccountBalanceType>("balance_type"),
            v.OptMoney("opening_balance_amount"), v.Money("balance_amount"), v.Ts("source_timestamp")));

    public static readonly SourceSchema<BankTransaction> BankingTransactions = new(
        SourceNames.BankingTransactions,
        Versions(
            ColumnSpec.Text("external_transaction_id", maxLength: 64), ColumnSpec.Text("external_account_id", maxLength: 64),
            ColumnSpec.Timestamp("booking_timestamp"), ColumnSpec.Date("value_date"), ColumnSpec.Amount("amount", sign: NonNeg), ColumnSpec.Currency(),
            ColumnSpec.Enum<DebitCredit>("debit_credit_indicator"), ColumnSpec.Text("bank_reference", false, 64),
            ColumnSpec.Enum<BankTransactionStatus>("transaction_status"), ColumnSpec.Timestamp("source_timestamp")),
        "external_transaction_id", "amount",
        v => new BankTransaction(v.Str("external_transaction_id"), v.Str("external_account_id"), v.Ts("booking_timestamp"), v.Date("value_date"),
            v.Money("amount"), v.Enum<DebitCredit>("debit_credit_indicator"), v.OptStr("bank_reference"),
            v.Enum<BankTransactionStatus>("transaction_status"), v.Ts("source_timestamp")));

    public static readonly SourceSchema<SafeguardingBalance> SafeguardingBalances = new(
        SourceNames.SafeguardingBalances,
        Versions(
            ColumnSpec.Text("source_row_id", maxLength: 64), ColumnSpec.Text("external_account_id", maxLength: 64),
            ColumnSpec.Text("legal_entity_id", maxLength: 64), ColumnSpec.Timestamp("balance_timestamp"),
            ColumnSpec.Enum<SafeguardingBalanceType>("balance_type"), ColumnSpec.Amount("balance_amount", sign: NonNeg), ColumnSpec.Currency(),
            ColumnSpec.Text("evidence_reference", maxLength: 128), ColumnSpec.Timestamp("source_timestamp")),
        "source_row_id", "balance_amount",
        v => new SafeguardingBalance(v.Str("source_row_id"), v.Str("external_account_id"), v.Str("legal_entity_id"), v.Ts("balance_timestamp"),
            v.Enum<SafeguardingBalanceType>("balance_type"), v.Money("balance_amount"), v.Str("evidence_reference"), v.Ts("source_timestamp")));

    public static readonly SourceSchema<CardEvent> CardTransactions = new(
        SourceNames.CardTransactions,
        Versions(
            ColumnSpec.Text("source_row_id", maxLength: 64), ColumnSpec.Text("card_event_id", maxLength: 64),
            ColumnSpec.Text("card_transaction_id", maxLength: 64), ColumnSpec.Text("client_account_id", maxLength: 64),
            ColumnSpec.Text("legal_entity_id", maxLength: 64), ColumnSpec.Text("tokenized_card_reference", false, 64),
            ColumnSpec.Enum<CardEventType>("event_type"), ColumnSpec.Timestamp("event_timestamp"), ColumnSpec.Amount("amount", sign: NonNeg),
            ColumnSpec.Currency(), ColumnSpec.Enum<DebitCredit>("debit_credit_indicator"), ColumnSpec.Text("merchant_reference", false, 64),
            ColumnSpec.Text("settlement_id", false, 64), ColumnSpec.Text("original_transaction_id", false, 64),
            ColumnSpec.Enum<CardEventStatus>("source_status")),
        "source_row_id", "amount",
        v => new CardEvent(v.Str("source_row_id"), v.Str("card_event_id"), v.Str("card_transaction_id"), v.Str("client_account_id"),
            v.Str("legal_entity_id"), v.OptStr("tokenized_card_reference"), v.Enum<CardEventType>("event_type"), v.Ts("event_timestamp"),
            v.Money("amount"), v.Enum<DebitCredit>("debit_credit_indicator"), v.OptStr("merchant_reference"), v.OptStr("settlement_id"),
            v.OptStr("original_transaction_id"), v.Enum<CardEventStatus>("source_status")));

    public static readonly SourceSchema<CardSettlement> CardSettlements = new(
        SourceNames.CardSettlements,
        Versions(
            ColumnSpec.Text("source_row_id", maxLength: 64), ColumnSpec.Text("settlement_id", maxLength: 64),
            ColumnSpec.Text("processor", maxLength: 64), ColumnSpec.Text("legal_entity_id", maxLength: 64), ColumnSpec.Date("settlement_date"),
            ColumnSpec.Amount("gross_amount", sign: NonNeg), ColumnSpec.Amount("fees", sign: NonNeg), ColumnSpec.Amount("adjustments"),
            ColumnSpec.Amount("net_amount"), ColumnSpec.Currency(), ColumnSpec.Text("destination_account", maxLength: 64),
            ColumnSpec.Enum<SettlementStatus>("source_status")),
        "source_row_id", "gross_amount",
        v =>
        {
            var gross = v.Money("gross_amount"); var fees = v.Money("fees"); var adj = v.Money("adjustments"); var net = v.Money("net_amount");
            var expected = gross - fees + adj;
            if (net != expected)
                throw RowValues.Reject(ReasonCodes.ControlValueMismatch, "net_amount",
                    $"net amount {net.AmountString} differs from gross minus fees plus adjustments of {expected.AmountString}");
            return new CardSettlement(v.Str("source_row_id"), v.Str("settlement_id"), v.Str("processor"), v.Str("legal_entity_id"),
                v.Date("settlement_date"), gross, fees, adj, net, v.Str("destination_account"), v.Enum<SettlementStatus>("source_status"));
        });

    public static readonly SourceSchema<DeliveryManifestEntry> DeliveryManifest = new(
        SourceNames.DeliveryManifest,
        Versions(
            ColumnSpec.Text("source_name", maxLength: 64), ColumnSpec.Text("delivery_id", maxLength: 64), ColumnSpec.Date("business_date"),
            ColumnSpec.Text("schema_version", maxLength: 16), ColumnSpec.Text("file_name", maxLength: 200),
            ColumnSpec.Integer("declared_record_count", required: false), ColumnSpec.Timestamp("expected_by", false),
            ColumnSpec.Timestamp("received_at", false), ColumnSpec.Text("correction_of_delivery_id", false, 64),
            ColumnSpec.Integer("sequence_number", required: false)),
        "delivery_id", null,
        v =>
        {
            var totals = ManifestTotals.Read(v.PrefixCells());
            if (totals.Errors.Count > 0)
                throw RowValues.Reject(ReasonCodes.InvalidValue, totals.Errors[0].Split(':')[0], totals.Errors[0]);
            return new DeliveryManifestEntry(v.Str("source_name"), v.Str("delivery_id"), v.Date("business_date"), v.Str("schema_version"),
                v.Str("file_name"), v.OptInt("declared_record_count"), totals.Totals, v.OptTs("expected_by"), v.OptTs("received_at"),
                v.OptStr("correction_of_delivery_id"), v.OptInt("sequence_number"));
        },
        ManifestTotals.Prefix);

    /// <summary>Every schema, manifest included.</summary>
    public static IReadOnlyList<ISourceSchema> All { get; } =
        [TransferInstructions, DirectDebitEvents, InternalLedgerEntries, ClientAccountBalances, BankingTransactions,
         SafeguardingBalances, CardTransactions, CardSettlements, DeliveryManifest];
}
