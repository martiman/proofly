using Proofly.Api.Platform.Http;
using Proofly.Contracts.Common;
using Proofly.Contracts.Exceptions;
using Proofly.Data.Exceptions;
using Proofly.Domain.Enums;

namespace Proofly.Api.Exceptions;

public static class ExceptionMapping
{
    public static MoneyDto? Money(decimal? amount, string? currency) =>
        amount is { } a && currency is not null ? new MoneyDto(a.ToString(System.Globalization.CultureInfo.InvariantCulture), currency) : null;

    public static string Etag(int version) => ETags.ForVersion(version);

    public static ExceptionSummaryDto ToSummary(ExceptionRow r) => new(
        r.Id, r.RunId, r.Title, r.Control, r.ReasonCode, r.Severity, r.Status, Money(r.ValueAmount, r.ValueCurrency),
        r.LegalEntityId, r.BusinessDate.ToString("yyyy-MM-dd"), r.OwnerId is null ? null : new UserRefDto(r.OwnerId, r.OwnerName ?? r.OwnerId),
        r.CreatedAt, r.AgeDays, r.DueAt, r.Blocking, r.SourceFile, r.SourceRowId, Etag(r.Version));

    public static ExceptionsSummaryDto ToDto(ExceptionsSummaryData s) => new(
        s.Total, s.Blocking, s.Unassigned, s.Overdue,
        Fill(s.BySeverity, Enum.GetValues<Severity>().Select(v => WireNames.ToUpperSnake(v))),
        Fill(s.ByStatus, Enum.GetValues<ExceptionStatus>().Select(v => WireNames.ToUpperSnake(v))),
        Fill(s.ByControl, Enum.GetValues<ControlArea>().Select(v => WireNames.ToLowerSnake(v))),
        s.ByCurrency.Select(c => new CurrencyAggregateDto(c.Currency, c.Count, Money(c.TotalValue, c.Currency)!, Money(c.AbsoluteValue, c.Currency)!)).ToList(),
        s.ByReason.Select(x => new ReasonCountDto(x.ReasonCode, x.Count)).ToList(),
        s.BySeverityCurrency.Select(x => new SeverityCurrencyDto(x.Severity, x.Currency, x.Count, Money(x.AbsoluteValue, x.Currency)!)).ToList(),
        s.WithoutValue, s.ComputedAt);

    /// <summary>Every known key is present (0 when absent) so clients need no existence checks; keys are the contract wire names.</summary>
    private static Dictionary<string, int> Fill(IReadOnlyDictionary<string, int> counts, IEnumerable<string> keys) =>
        keys.ToDictionary(k => k, k => counts.TryGetValue(k, out var n) ? n : 0, StringComparer.Ordinal);

    private static readonly HashSet<string> NodeKinds = ["INSTRUCTION", "LEDGER_ENTRY", "BANK_TRANSACTION", "LIFECYCLE_EVENT", "BALANCE", "CONTROL_RESULT", "DELIVERY"];
    private static readonly HashSet<string> TransactionKinds = ["INSTRUCTION", "LEDGER_ENTRY", "BANK_TRANSACTION", "TRANSACTION", "LIFECYCLE_EVENT"];
    private static readonly HashSet<string> TimelineTypes = ["ASSIGNMENT", "NOTE", "EVIDENCE_REQUEST", "STATUS_CHANGE", "RESOLUTION_PROPOSED", "APPROVAL", "REOPENED", "SYSTEM"];

    public static ExceptionDetailDto ToDetail(ExceptionDetailData d, Caller caller)
    {
        var r = d.Row;
        var s = ToSummary(r);
        var money = Money(r.ValueAmount, r.ValueCurrency);
        var rootId = "exc-" + r.Id;

        var nodes = new List<EvidenceNodeDto> { new(rootId, "CONTROL_RESULT", r.Title, r.Control, r.SourceRowId, null, money) };
        var edges = new List<EvidenceEdgeDto>();
        if (d.DeliverySource is not null)
        {
            nodes.Add(new EvidenceNodeDto("dlv-" + r.Id, "DELIVERY", r.SourceFile ?? d.DeliverySource, d.DeliverySource, r.SourceRowId, null, null));
            edges.Add(new EvidenceEdgeDto("dlv-" + r.Id, rootId, "delivered_in"));
        }
        foreach (var e in d.Evidence)
        {
            var kind = e.Kind.ToUpperInvariant();
            var known = NodeKinds.Contains(kind);
            nodes.Add(new EvidenceNodeDto("ev-" + e.Id, known ? kind : "CONTROL_RESULT", known ? e.Ref : $"{e.Kind}: {e.Ref}", r.Control, e.Ref, null, null));
            edges.Add(new EvidenceEdgeDto("ev-" + e.Id, rootId, "evidences"));
        }

        var inputs = new List<RuleTraceInputDto> { new("reasonCode", r.ReasonCode), new("businessDate", s.BusinessDate), new("legalEntityId", r.LegalEntityId) };
        if (money is not null) { inputs.Add(new RuleTraceInputDto("value", $"{money.Amount} {money.Currency}")); }
        // The control's recorded trace when one exists; otherwise the values derivable from the exception itself (tolerance stays null).
        var trace = d.Trace is { } t ? ToTrace(r, t) : new RuleTraceDto(r.Control, r.ReasonCode, d.RulesetVersion, inputs, null,
            $"{r.Control} control raised {r.ReasonCode}", r.Title);

        var timeline = d.Timeline.Select(t => new TimelineEventDto("tl-" + t.Id, t.At,
            new ActorDto(t.ActorType, t.ActorId, t.ActorName),
            TimelineTypes.Contains(t.Kind.ToUpperInvariant()) ? t.Kind.ToUpperInvariant() : "SYSTEM", t.Summary)).ToList();

        var status = Enum.GetValues<ExceptionStatus>().Single(v => WireNames.ToUpperSnake(v) == r.Status);
        var pending = d.Approvals.LastOrDefault(a => a.Status == "PENDING");
        Func<Permission, bool> can = p => caller.Can(p, r.LegalEntityId);
        var approvals = d.Approvals.Select(a => ToApproval(a, a.Status == "PENDING" && a.ProposerId != caller.UserId && can(Permission.ExceptionApprove))).ToList();

        var states = new List<string>();
        if (r.Status == "WAITING_FOR_DATA") { states.Add("WAITING_FOR_DATA"); }
        if (r.Status == "PENDING_APPROVAL") { states.Add("PENDING_APPROVAL"); }
        if (d.RunState == "SUPERSEDED") { states.Add("SUPERSEDED_RUN"); }

        var allowed = AllowedActions.Compute(new ActionContext(status, d.RunState == "SUPERSEDED", caller.UserId, pending?.ProposerId, can));

        return new ExceptionDetailDto(
            s.Id, s.RunId, s.Title, s.Control, s.ReasonCode, s.Severity, s.Status, s.Value, s.LegalEntityId, s.BusinessDate, s.Owner,
            s.CreatedAt, s.AgeDays, s.DueAt, s.Blocking, s.SourceFile, s.SourceRowId, s.Etag,
            d.Trace?.Description ?? r.Title,
            d.Trace?.ExpectedEffect ?? (r.Blocking ? "Blocks run sign-off until resolved." : "Does not block run sign-off."), r.Title,
            nodes, edges, trace, timeline, states,
            d.Evidence.Where(e => TransactionKinds.Contains(e.Kind.ToUpperInvariant())).Select(e => e.Ref).Distinct().ToList(),
            d.Evidence.Where(e => e.Kind.Equals("ACCOUNT", StringComparison.OrdinalIgnoreCase)).Select(e => e.Ref).Distinct().ToList(),
            pending?.PolicyVersion ?? d.Approvals.LastOrDefault()?.PolicyVersion ?? d.ConfiguredApprovalPolicyVersion ?? "none",
            pending is null ? null : approvals.Single(a => a.Id == pending.Id), approvals, allowed);
    }

    private static RuleTraceDto ToTrace(ExceptionRow r, RuleTraceRow t)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(t.CalculationJson);
        var root = doc.RootElement;
        string? S(string name) => root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        var inputs = new List<RuleTraceInputDto>();
        if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("inputs", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var i in arr.EnumerateArray())
            {
                if (i.TryGetProperty("name", out var n) && i.TryGetProperty("value", out var v)) { inputs.Add(new RuleTraceInputDto(n.GetString()!, v.GetString()!)); }
            }
        }
        return new RuleTraceDto(r.Control, t.RuleId, t.RuleVersion, inputs, t.Tolerance, S("text") ?? t.Description ?? "", S("result") ?? r.Title);
    }

    private static ApprovalDto ToApproval(ApprovalRow a, bool canDecide) => new(
        a.Id, a.ObjectType, a.ObjectId, a.RunId, a.ProposalVersion, new UserRefDto(a.ProposerId, a.ProposerName),
        a.ApproverId is null ? null : new UserRefDto(a.ApproverId, a.ApproverName ?? a.ApproverId),
        a.RequiredRole, a.Status, a.Decision, a.Rationale, a.EvidenceRefs, a.PolicyVersion, a.ProposedAt, a.DecidedAt, canDecide, a.Kind);
}
