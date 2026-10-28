namespace Proofly.Domain.Records;

/// <summary>
/// Source names exactly as used in the delivery manifest column <c>source_name</c> (and as file stem of the
/// synthetic fixture). Adapters register under these names.
/// </summary>
public static class SourceNames
{
    public const string TransferInstructions = "transfer_instructions";
    public const string DirectDebitEvents = "direct_debit_events";
    public const string InternalLedgerEntries = "internal_ledger_entries";
    public const string ClientAccountBalances = "client_account_balances";
    public const string BankingTransactions = "banking_transactions";
    public const string SafeguardingBalances = "safeguarding_balances";
    public const string CardTransactions = "card_transactions";
    public const string CardSettlements = "card_settlements";
    public const string DeliveryManifest = "delivery_manifest";

    /// <summary>The eight data sources (everything except the manifest).</summary>
    public static readonly IReadOnlyList<string> DataSources =
    [
        TransferInstructions, DirectDebitEvents, InternalLedgerEntries, ClientAccountBalances,
        BankingTransactions, SafeguardingBalances, CardTransactions, CardSettlements,
    ];
}
