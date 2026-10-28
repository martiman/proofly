namespace Proofly.Domain.Records;

/// <summary>
/// Canonical, immutable representation of one accepted source row. Adapters produce these; controls consume them.
/// Column names refer to the synthetic fixture headers, which are the proposed input contract
/// (docs/integrations/input-contracts.md). A record exists only if every required field was present and valid:
/// the adapters never repair or default a value.
/// </summary>
public interface ISourceRecord
{
    /// <summary>Row identity within a delivery (<c>source_row_id</c>, or the source's natural id where none exists).</summary>
    string SourceRowId { get; }

    /// <summary>
    /// Uniqueness scope of the record inside a delivery; the framework rejects later rows with a repeated key as
    /// DUPLICATE_RECORD (first occurrence wins).
    /// </summary>
    string NaturalKey { get; }

    /// <summary>Legal entity, or null when the source has none (banking_transactions).</summary>
    string? LegalEntityId { get; }

    /// <summary>
    /// The amount that the manifest's declared control totals are taken over (by currency); null when the source
    /// declares none. Rejected rows contribute theirs through <c>RowRejection.ControlAmount</c>.
    /// </summary>
    Money? ControlAmount { get; }

    /// <summary>Every monetary value of the record; the framework rejects excess precision (more than 6 decimals).</summary>
    IEnumerable<Money> Amounts { get; }
}

/// <summary>transfer_instructions.csv. Table: transfer_instructions. ControlAmount = amount.</summary>
/// <param name="SourceRowId">source_row_id</param>
/// <param name="InstructionId">instruction_id (unique within the delivery)</param>
/// <param name="ClientAccountId">client_account_id (required)</param>
/// <param name="LegalEntityId">legal_entity_id</param>
/// <param name="TransactionType">transaction_type, as received (e.g. BANK_TRANSFER)</param>
/// <param name="Amount">amount + currency</param>
/// <param name="InstructionTimestamp">instruction_timestamp (offset required)</param>
/// <param name="RequestedValueDate">requested_value_date</param>
/// <param name="SourceStatus">source_status</param>
/// <param name="ExternalReference">external_reference (optional)</param>
/// <param name="DestinationReference">destination_reference (optional)</param>
public sealed record TransferInstruction(
    string SourceRowId, string InstructionId, string ClientAccountId, string LegalEntityId, string TransactionType,
    Money Amount, DateTimeOffset InstructionTimestamp, DateOnly? RequestedValueDate, TransferStatus SourceStatus,
    string? ExternalReference, string? DestinationReference) : ISourceRecord
{
    public string NaturalKey => InstructionId;
    string? ISourceRecord.LegalEntityId => LegalEntityId;
    public Money? ControlAmount => Amount;
    public IEnumerable<Money> Amounts => [Amount];
}

/// <summary>direct_debit_events.csv. Table: direct_debit_events. ControlAmount = amount. One row per event (several per instruction).</summary>
/// <param name="SourceRowId">source_row_id</param>
/// <param name="InstructionId">instruction_id</param>
/// <param name="MandateReference">mandate_reference (required)</param>
/// <param name="ClientAccountId">client_account_id</param>
/// <param name="LegalEntityId">legal_entity_id</param>
/// <param name="Amount">amount + currency</param>
/// <param name="CollectionDate">collection_date</param>
/// <param name="EventType">event_type</param>
/// <param name="EventTimestamp">event_timestamp</param>
/// <param name="SourceStatus">source_status</param>
/// <param name="ReturnReason">return_reason (returns/rejections)</param>
/// <param name="OriginalInstructionReference">original_instruction_reference (returns/reversals/settlements)</param>
public sealed record DirectDebitEvent(
    string SourceRowId, string InstructionId, string MandateReference, string ClientAccountId, string LegalEntityId,
    Money Amount, DateOnly? CollectionDate, DirectDebitEventType EventType, DateTimeOffset EventTimestamp,
    DirectDebitStatus SourceStatus, string? ReturnReason, string? OriginalInstructionReference) : ISourceRecord
{
    /// <summary>instruction_id + event_type + event_timestamp (an instruction legitimately has several events).</summary>
    public string NaturalKey => $"{InstructionId}|{WireEnum.ToWire(EventType)}|{WireEnum.Iso(EventTimestamp)}";
    string? ISourceRecord.LegalEntityId => LegalEntityId;
    public Money? ControlAmount => Amount;
    public IEnumerable<Money> Amounts => [Amount];
}

/// <summary>card_transactions.csv. Table: card_events. ControlAmount = amount.</summary>
/// <param name="SourceRowId">source_row_id</param>
/// <param name="CardEventId">card_event_id (unique within the delivery)</param>
/// <param name="CardTransactionId">card_transaction_id (the journey the event belongs to)</param>
/// <param name="ClientAccountId">client_account_id</param>
/// <param name="LegalEntityId">legal_entity_id</param>
/// <param name="TokenizedCardReference">tokenized_card_reference (never a PAN)</param>
/// <param name="EventType">event_type</param>
/// <param name="EventTimestamp">event_timestamp</param>
/// <param name="Amount">amount + currency</param>
/// <param name="DebitCredit">debit_credit_indicator</param>
/// <param name="MerchantReference">merchant_reference</param>
/// <param name="SettlementId">settlement_id (when assigned)</param>
/// <param name="OriginalTransactionId">original_transaction_id (reversal/refund/chargeback)</param>
/// <param name="SourceStatus">source_status</param>
public sealed record CardEvent(
    string SourceRowId, string CardEventId, string CardTransactionId, string ClientAccountId, string LegalEntityId,
    string? TokenizedCardReference, CardEventType EventType, DateTimeOffset EventTimestamp, Money Amount,
    DebitCredit DebitCredit, string? MerchantReference, string? SettlementId, string? OriginalTransactionId,
    CardEventStatus SourceStatus) : ISourceRecord
{
    public string NaturalKey => CardEventId;
    string? ISourceRecord.LegalEntityId => LegalEntityId;
    public Money? ControlAmount => Amount;
    public IEnumerable<Money> Amounts => [Amount];
}

/// <summary>card_settlements.csv. Table: card_settlements. ControlAmount = gross_amount. All four amounts share one currency.</summary>
/// <param name="SourceRowId">source_row_id</param>
/// <param name="SettlementId">settlement_id (unique within the delivery)</param>
/// <param name="Processor">processor</param>
/// <param name="LegalEntityId">legal_entity_id</param>
/// <param name="SettlementDate">settlement_date</param>
/// <param name="GrossAmount">gross_amount + currency</param>
/// <param name="Fees">fees</param>
/// <param name="Adjustments">adjustments (signed)</param>
/// <param name="NetAmount">net_amount; adapters reject rows where net != gross - fees + adjustments</param>
/// <param name="DestinationAccount">destination_account (required)</param>
/// <param name="SourceStatus">source_status</param>
public sealed record CardSettlement(
    string SourceRowId, string SettlementId, string Processor, string LegalEntityId, DateOnly SettlementDate,
    Money GrossAmount, Money Fees, Money Adjustments, Money NetAmount, string DestinationAccount,
    SettlementStatus SourceStatus) : ISourceRecord
{
    public string NaturalKey => SettlementId;
    string? ISourceRecord.LegalEntityId => LegalEntityId;
    public Money? ControlAmount => GrossAmount;
    public IEnumerable<Money> Amounts => [GrossAmount, Fees, Adjustments, NetAmount];
}

/// <summary>internal_ledger_entries.csv. Table: ledger_entries. SourceRowId = ledger_entry_id. ControlAmount = amount.</summary>
/// <param name="LedgerEntryId">ledger_entry_id (also the row id)</param>
/// <param name="AccountId">account_id</param>
/// <param name="LegalEntityId">legal_entity_id</param>
/// <param name="BookingTimestamp">booking_timestamp</param>
/// <param name="ValueDate">value_date</param>
/// <param name="DebitCredit">debit_credit_indicator</param>
/// <param name="Amount">amount + currency (always positive magnitude; direction is DebitCredit)</param>
/// <param name="TransactionReference">transaction_reference</param>
/// <param name="EntryType">entry_type</param>
/// <param name="SourceStatus">source_status</param>
public sealed record LedgerEntry(
    string LedgerEntryId, string AccountId, string LegalEntityId, DateTimeOffset BookingTimestamp, DateOnly ValueDate,
    DebitCredit DebitCredit, Money Amount, string? TransactionReference, LedgerEntryType EntryType,
    LedgerEntryStatus SourceStatus) : ISourceRecord
{
    public string SourceRowId => LedgerEntryId;
    public string NaturalKey => LedgerEntryId;
    string? ISourceRecord.LegalEntityId => LegalEntityId;
    public Money? ControlAmount => Amount;
    public IEnumerable<Money> Amounts => [Amount];
}

/// <summary>client_account_balances.csv. Table: account_balances. ControlAmount = balance_amount.</summary>
/// <param name="AccountId">account_id</param>
/// <param name="LegalEntityId">legal_entity_id</param>
/// <param name="BalanceDate">balance_date</param>
/// <param name="BalanceType">balance_type</param>
/// <param name="OpeningBalance">opening_balance_amount (optional; same currency as the balance)</param>
/// <param name="Balance">balance_amount + currency (may be negative)</param>
/// <param name="SourceTimestamp">source_timestamp</param>
public sealed record AccountBalance(
    string AccountId, string LegalEntityId, DateOnly BalanceDate, AccountBalanceType BalanceType, Money? OpeningBalance,
    Money Balance, DateTimeOffset SourceTimestamp) : ISourceRecord
{
    /// <summary>account_id|balance_type|balance_date.</summary>
    public string NaturalKey => $"{AccountId}|{WireEnum.ToWire(BalanceType)}|{BalanceDate:yyyy-MM-dd}";
    public string SourceRowId => NaturalKey;
    string? ISourceRecord.LegalEntityId => LegalEntityId;
    public Money? ControlAmount => Balance;
    public IEnumerable<Money> Amounts => OpeningBalance is { } o ? [o, Balance] : [Balance];
}

/// <summary>banking_transactions.csv. Table: bank_transactions. No legal entity. SourceRowId = external_transaction_id. ControlAmount = amount.</summary>
/// <param name="ExternalTransactionId">external_transaction_id</param>
/// <param name="ExternalAccountId">external_account_id</param>
/// <param name="BookingTimestamp">booking_timestamp</param>
/// <param name="ValueDate">value_date</param>
/// <param name="Amount">amount + currency</param>
/// <param name="DebitCredit">debit_credit_indicator</param>
/// <param name="BankReference">bank_reference</param>
/// <param name="Status">transaction_status</param>
/// <param name="SourceTimestamp">source_timestamp</param>
public sealed record BankTransaction(
    string ExternalTransactionId, string ExternalAccountId, DateTimeOffset BookingTimestamp, DateOnly ValueDate,
    Money Amount, DebitCredit DebitCredit, string? BankReference, BankTransactionStatus Status,
    DateTimeOffset SourceTimestamp) : ISourceRecord
{
    public string SourceRowId => ExternalTransactionId;
    public string NaturalKey => ExternalTransactionId;
    public string? LegalEntityId => null;
    public Money? ControlAmount => Amount;
    public IEnumerable<Money> Amounts => [Amount];
}

/// <summary>safeguarding_balances.csv. Table: safeguarding_balances. ControlAmount = balance_amount. Eligibility is not a source field.</summary>
/// <param name="SourceRowId">source_row_id</param>
/// <param name="ExternalAccountId">external_account_id</param>
/// <param name="LegalEntityId">legal_entity_id</param>
/// <param name="BalanceTimestamp">balance_timestamp</param>
/// <param name="BalanceType">balance_type</param>
/// <param name="Balance">balance_amount + currency (negative values are valid input; policy decides, see F-015)</param>
/// <param name="EvidenceReference">evidence_reference (required)</param>
/// <param name="SourceTimestamp">source_timestamp</param>
public sealed record SafeguardingBalance(
    string SourceRowId, string ExternalAccountId, string LegalEntityId, DateTimeOffset BalanceTimestamp,
    SafeguardingBalanceType BalanceType, Money Balance, string EvidenceReference, DateTimeOffset SourceTimestamp)
    : ISourceRecord
{
    /// <summary>external_account_id|balance_timestamp|balance_type (F-018: same account and timestamp twice).</summary>
    public string NaturalKey => $"{ExternalAccountId}|{WireEnum.Iso(BalanceTimestamp)}|{WireEnum.ToWire(BalanceType)}";
    string? ISourceRecord.LegalEntityId => LegalEntityId;
    public Money? ControlAmount => Balance;
    public IEnumerable<Money> Amounts => [Balance];
}

/// <summary>
/// delivery_manifest.csv row. Not persisted as such: it becomes the <c>source_deliveries</c> row at registration.
/// Declared totals cover every source row of the file (accepted and rejected alike), by currency.
/// </summary>
/// <param name="SourceName">source_name (see SourceNames)</param>
/// <param name="DeliveryId">delivery_id</param>
/// <param name="BusinessDate">business_date</param>
/// <param name="SchemaVersion">schema_version</param>
/// <param name="FileName">file_name</param>
/// <param name="DeclaredRecordCount">declared_record_count (rows in the file, data rows only)</param>
/// <param name="DeclaredTotals">declared_total_&lt;ccy&gt; columns: the declared sum of the source's ControlAmount per currency; empty = no totals declared (check skipped)</param>
/// <param name="ExpectedBy">expected_by</param>
/// <param name="ReceivedAt">received_at</param>
/// <param name="CorrectionOfDeliveryId">correction_of_delivery_id; set when this delivery corrects an earlier one</param>
/// <param name="SequenceNumber">optional sequence_number for sources that number their deliveries</param>
public sealed record DeliveryManifestEntry(
    string SourceName, string DeliveryId, DateOnly BusinessDate, string SchemaVersion, string FileName,
    int? DeclaredRecordCount, IReadOnlyList<Money> DeclaredTotals, DateTimeOffset? ExpectedBy,
    DateTimeOffset? ReceivedAt, string? CorrectionOfDeliveryId, int? SequenceNumber = null) : ISourceRecord
{
    public string SourceRowId => DeliveryId;
    public string NaturalKey => DeliveryId;
    public string? LegalEntityId => null;
    public Money? ControlAmount => null;
    public IEnumerable<Money> Amounts => [];
}
