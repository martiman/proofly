using System.Globalization;
using Proofly.Domain.Enums;

namespace Proofly.Domain.Records;

// Source-status vocabularies observed in the synthetic fixture (examples/synthetic-data/2026-10-27). Real
// vocabularies are agreed per pilot source; a value outside the enum is REJECTED by the adapter (INVALID_VALUE),
// never mapped to a guess. Wire form is UPPER_SNAKE (see WireEnum).
public enum DebitCredit { Debit, Credit }
public enum TransferStatus { Submitted, Settled, Rejected, Cancelled }
public enum DirectDebitEventType { Submitted, Settled, Returned, Rejected, Reversed }
public enum DirectDebitStatus { Submitted, Settled, Returned, Rejected, Reversed }
public enum CardEventType { Authorization, Presentment, Reversal, Refund, Chargeback }
public enum CardEventStatus { Authorized, Presented, Reversed, Refunded, ChargedBack }
public enum SettlementStatus { Settled, Pending, Failed }
public enum LedgerEntryType { Transfer, DirectDebit, Card, Fee, Adjustment, Reversal }
public enum LedgerEntryStatus { Posted, Pending, Reversed }
public enum AccountBalanceType { DailyRollforward }
public enum BankTransactionStatus { Booked, Pending, Reversed }
public enum SafeguardingBalanceType { ClosingAvailable, ClosingLedger }

/// <summary>Parses and formats the UPPER_SNAKE wire form of the record enums (e.g. DIRECT_DEBIT, CLOSING_AVAILABLE).</summary>
public static class WireEnum
{
    /// <summary>Exact, case-sensitive match on the wire name; no trimming or case folding (reject, never repair).</summary>
    public static bool TryParse<T>(string? wire, out T value) where T : struct, Enum
    {
        if (wire is not null)
        {
            foreach (var candidate in Enum.GetValues<T>())
            {
                if (string.Equals(WireNames.ToUpperSnake(candidate), wire, StringComparison.Ordinal))
                {
                    value = candidate;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    public static string ToWire<T>(T value) where T : struct, Enum => WireNames.ToUpperSnake(value);

    internal static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
