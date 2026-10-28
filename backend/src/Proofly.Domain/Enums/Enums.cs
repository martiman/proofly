namespace Proofly.Domain.Enums;

// Mirror docs/integrations/api/openapi.yaml components/schemas. Wire names are the
// upper snake case of the member (ControlArea: lower snake case); Proofly.Contracts converts.
public enum Severity { Critical, High, Medium, Low }
public enum ControlArea { DataCompleteness, Matching, IndependentLedger, TransactionLifecycle, Safeguarding }
public enum ControlState { Passed, ReviewRequired, Failed, Blocked, Qualified }
public enum RunState { AwaitingEvidence, Processing, ReviewRequired, ReadyForSignOff, SignedOff, Superseded, Failed }
public enum SignOffState { PreparationIncomplete, ReadyForReview, Returned, ApprovalPending, SignedOff, Invalidated, Superseded }
public enum ExceptionStatus { Open, InInvestigation, WaitingForData, PendingApproval, Resolved, AcceptedDifference, FalsePositive }
public enum ResolutionKind { Resolved, AcceptedDifference, FalsePositive }
public enum TransactionKind { BankTransfer, DirectDebit, Card, BankEvidence, LedgerEntry }
public enum MatchStatus { Matched, Unmatched, ValueMismatch, NotApplicable, PendingApproval }
public enum AdjustmentStatus { Proposed, Approved, Rejected, Expired }
public enum SafeguardingState { Covered, Shortfall, Surplus, Qualified, NoObligation }
public enum DeliveryState { Received, Late, Missing, Rejected, Superseded, Qualified }
public enum ActorType { User, System, ServiceAccount }
public enum Role { ControlAnalyst, ControlOwner, Approver, AuditReviewer, Admin, ConfigAdmin, SecurityAdmin }

public enum Permission
{
    AuthSelf, RunView, RunReprocess, SourceView, SourceRetry, SourceSupersede,
    ExceptionView, ExceptionAssign, ExceptionInvestigate, ExceptionPropose, ExceptionApprove, ExceptionReopen,
    TransactionView, MatchingView, MatchingPropose, LedgerView, SafeguardingView,
    SafeguardingAdjustmentPropose, SafeguardingAdjustmentApprove,
    SignoffView, SignoffPrepare, SignoffDecide, EvidenceView, EvidenceExport, AuditView,
    SettingsView, SettingsDraft, SettingsApprove, AccessManage, ApprovalView,
}
