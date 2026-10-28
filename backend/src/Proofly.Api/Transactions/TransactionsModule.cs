using System.Globalization;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Json;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Common;
using Proofly.Contracts.Transactions;
using Proofly.Data.Transactions;
using Proofly.Domain.Enums;

namespace Proofly.Api.Transactions;

public static class TransactionsModule
{
    public static IServiceCollection AddTransactions(this IServiceCollection services) =>
        services.AddTransactionData().AddSingleton<IEndpointModule, TransactionEndpoints>().AddSingleton<IEndpointModule, LineageEndpoint>();
}

internal sealed class TransactionEndpoints(ITransactionReadRepository repo) : IEndpointModule
{
    private const int RelatedTransactionLimit = 100;

    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/runs/{runId}");
        g.MapGet("/transactions", ListTransactions);
        g.MapGet("/transactions/{transactionId}", GetTransaction);
        g.MapGet("/transactions/{transactionId}/lineage", GetLineage);
        g.MapGet("/accounts", ListAccounts);
        g.MapGet("/accounts/{accountId}/ledger", GetLedger);
    }

    private async Task<RunScope> Scope(HttpContext ctx, string runId, Permission permission)
    {
        var scope = await repo.FindRunAsync(runId, ctx.RequestAborted) ?? throw new ProblemException(ProblemTypes.NotFound);
        ctx.GetCaller().Require(permission, scope.LegalEntityId);
        return scope;
    }

    private static string? Q(HttpContext ctx, string name) => ctx.Request.Query.TryGetValue(name, out var v) && v.Count > 0 && v[0] is { Length: > 0 } s ? s : null;

    private static DateTimeOffset? Ts(HttpContext ctx, string name) =>
        Q(ctx, name) is { } s ? (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
            : throw ProblemException.Validation(name, "Not a valid date-time.")) : null;

    // ---- mapping ----

    internal static string Amt(decimal d)
    {
        var n = decimal.Round(d, 6) / 1.000000000000000000m; // trims trailing zeros of NUMERIC(38,6)
        var s = n.ToString(CultureInfo.InvariantCulture);
        var dot = s.IndexOf('.');
        return dot < 0 ? s + ".00" : s.Length - dot - 1 < 2 ? s + "0" : s;
    }

    internal static MoneyDto Money(decimal amount, string currency) => new(Amt(amount), currency);
    internal static MoneyDto? Money(decimal? amount, string currency) => amount is { } a ? Money(a, currency) : null;

    internal static TransactionDto ToDto(TransactionRow t) => new(t.Id, t.RunId, t.Kind, t.Source, t.SourceRowId, t.RecordId, t.DeliveryId, t.Reference,
        t.AccountId, t.Timestamp, Money(t.Amount, t.Currency), t.Direction, t.SourceStatus, t.Lifecycle, t.MatchStatus, t.MatchGroupId, t.ExceptionIds, t.Accepted);

    private static AccountDto ToDto(string runId, AccountRow a) => new(a.AccountId, runId, a.Currency, a.LegalEntityId, Money(a.Opening, a.Currency),
        Money(a.Recorded, a.Currency), Money(a.Independent, a.Currency), Money(a.Difference, a.Currency), a.State, a.ExceptionIds);

    // ---- handlers ----

    private async Task<IResult> ListTransactions(string runId, HttpContext ctx)
    {
        await Scope(ctx, runId, Permission.TransactionView);
        var page = PageRequest.From(ctx);
        var filter = new TransactionFilter(Q(ctx, "kind"), Q(ctx, "matchStatus"), Q(ctx, "accountId"), Q(ctx, "currency"),
            Q(ctx, "hasException") is { } he ? bool.Parse(he) : null, Ts(ctx, "timestampFrom"), Ts(ctx, "timestampTo"), Q(ctx, "search"));
        var res = await repo.ListTransactionsAsync(runId, filter, page.Decode<TransactionCursor>(), page.Limit, page.IncludeTotal, ctx.RequestAborted);

        var fetched = res.HasMore ? res.Rows.Append(res.Rows[^1]).ToList() : res.Rows; // Paging.Create expects limit+1 rows when more exist
        var p = Paging.Create(fetched.Select(ToDto).ToList(), page, t => new TransactionCursor(t.Timestamp, t.Id), res.Total);
        var aggs = res.Aggregates.Select(a => new TransactionAggregateDto(a.Currency, a.Count, Money(a.DebitTotal, a.Currency), Money(a.CreditTotal, a.Currency))).ToList();
        return Results.Ok(new TransactionPageDto(p.Items, p.Page, aggs));
    }

    private async Task<IResult> GetTransaction(string runId, string transactionId, HttpContext ctx)
    {
        await Scope(ctx, runId, Permission.TransactionView);
        var t = await repo.GetTransactionAsync(runId, transactionId, ctx.RequestAborted) ?? throw new ProblemException(ProblemTypes.NotFound);
        var dto = ToDto(t);
        ctx.Set(ETags.ForText(System.Text.Json.JsonSerializer.Serialize(dto, WireJson.Options)));
        return Results.Ok(dto);
    }

    private async Task<IResult> GetLineage(string runId, string transactionId, HttpContext ctx)
    {
        await Scope(ctx, runId, Permission.TransactionView);
        var l = await repo.GetLineageAsync(runId, transactionId, ctx.RequestAborted) ?? throw new ProblemException(ProblemTypes.NotFound);
        return Results.Ok(new LineageDto("TRANSACTION", l.TransactionId,
            l.Nodes.Select(n => new EvidenceNodeDto(n.Id, n.Kind, n.Label, n.Source, n.SourceRowId, n.RecordId, n.Amount is { } a ? Money(a, n.Currency!) : null)).ToList(),
            l.Edges.Select(e => new EvidenceEdgeDto(e.From, e.To, e.Relation)).ToList(),
            l.Deliveries.Select(d => new LineageDeliveryDto(d.DeliveryId, d.Fingerprint, d.RowNumber, d.SchemaVersion, null)).ToList()));
    }

    private async Task<IResult> ListAccounts(string runId, HttpContext ctx)
    {
        await Scope(ctx, runId, Permission.LedgerView);
        var page = PageRequest.From(ctx);
        var res = await repo.ListAccountsAsync(runId, new AccountFilter(Q(ctx, "currency"), Q(ctx, "state"), Q(ctx, "search")),
            page.Decode<AccountCursor>(), page.Limit, ctx.RequestAborted);
        var fetched = res.HasMore ? res.Rows.Append(res.Rows[^1]).ToList() : res.Rows;
        var p = Paging.Create(fetched, page, a => new AccountCursor(a.AccountId, a.Currency));
        var aggs = res.Aggregates.Select(a => new AccountAggregateDto(a.Currency, a.Accounts, Money(a.TotalDifference, a.Currency))).ToList();
        return Results.Ok(new AccountPageDto(p.Items.Select(a => ToDto(runId, a)).ToList(), new PageInfoDto(p.Page.NextCursor, p.Page.HasMore), aggs));
    }

    private async Task<IResult> GetLedger(string runId, string accountId, HttpContext ctx)
    {
        var scope = await Scope(ctx, runId, Permission.LedgerView);
        var page = PageRequest.From(ctx);
        var currencyParam = Q(ctx, "currency");
        var found = await repo.FindAccountAsync(runId, accountId, currencyParam, ctx.RequestAborted);
        if (found.Count == 0) { throw new ProblemException(ProblemTypes.NotFound); }
        if (found.Count > 1)
        {
            throw ProblemException.Validation("currency", $"Account {accountId} holds several currencies ({string.Join(", ", found.Select(a => a.Currency))}); specify currency.");
        }
        var acc = found[0];
        var entries = await repo.ListLedgerEntriesAsync(runId, acc.AccountId, acc.Currency, page.Decode<LedgerCursor>(), page.Limit, ctx.RequestAborted);
        var next = entries.HasMore ? Cursors.Encode(new LedgerCursor(entries.Rows[^1].Timestamp, entries.Rows[^1].InternalId)) : null;

        var related = await repo.ListTransactionsAsync(runId, new TransactionFilter(AccountId: acc.AccountId, Currency: acc.Currency), null, RelatedTransactionLimit, false, ctx.RequestAborted);
        var exceptions = await repo.ListExceptionsAsync(runId, acc.ExceptionIds, ctx.RequestAborted);

        var ccy = acc.Currency;
        // Lines the control could not compute are left out rather than shown as zero.
        var rollForward = new List<RollForwardLineDto>();
        void Line(string label, MoneyDto? amount) { if (amount is not null) { rollForward.Add(new RollForwardLineDto(label, amount)); } }
        Line("Opening balance", Money(acc.Opening, ccy));
        Line("Credits", Money(acc.Credits, ccy));
        Line("Debits", Money(-acc.Debits, ccy));
        if (acc.Adjustments != 0m) { Line("Adjustments", Money(acc.Adjustments, ccy)); }
        Line("Independent closing balance", Money(acc.Independent, ccy));
        Line("Recorded closing balance", Money(acc.Recorded, ccy));
        Line("Difference", Money(acc.Difference, ccy));
        return Results.Ok(new AccountLedgerViewDto(
            ToDto(runId, acc),
            entries.Rows.Select(e => new LedgerEntryDto(e.Id, e.AccountId, e.Timestamp, e.ValueDate, e.Direction, Money(e.Amount, e.Currency), e.Reference,
                e.EntryType, e.SourceStatus, e.RecordId, e.ExceptionIds)).ToList(),
            new PageInfoDto(next, entries.HasMore),
            rollForward,
            related.Rows.Select(ToDto).ToList(),
            exceptions.Select(e => new AccountExceptionSummaryDto(e.Id, e.RunId, e.Title, e.Control, e.ReasonCode, e.Severity, e.Status,
                e.ValueAmount is { } v ? Money(v, e.ValueCurrency!) : null, e.LegalEntityId, e.BusinessDate,
                e.OwnerId is null ? null : new UserRefDto(e.OwnerId, e.OwnerName ?? e.OwnerId), e.CreatedAt, e.DueAt, e.Blocking,
                e.SourceFile, e.SourceRowId, ETags.ForVersion(e.Version))).ToList()));
    }
}

/// <summary>getLineage: GET /lineage/{subjectKind}/{subjectId}. Entity scope and evidence.view of the subject's legal entity (404 when out of scope).</summary>
internal sealed class LineageEndpoint(ILineageRepository lineage) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app) => app.MapGet("/api/v1/lineage/{subjectKind}/{subjectId}", async (string subjectKind, string subjectId, HttpContext ctx) =>
    {
        if (subjectKind is not (LineageSubjectKinds.Exception or LineageSubjectKinds.Transaction or LineageSubjectKinds.Record or LineageSubjectKinds.ControlResult))
        {
            throw ProblemException.Validation("subjectKind", "subjectKind must be EXCEPTION, TRANSACTION, RECORD or CONTROL_RESULT.");
        }
        var runId = ctx.Request.Query.TryGetValue("runId", out var v) && v.Count > 0 && v[0] is { Length: > 0 } r ? r : null;
        var subject = await lineage.FindSubjectAsync(subjectKind, subjectId, runId, ctx.RequestAborted) ?? throw new ProblemException(ProblemTypes.NotFound);
        ctx.GetCaller().Require(Permission.EvidenceView, subject.LegalEntityId);
        var l = await lineage.GetLineageAsync(subject, ctx.RequestAborted) ?? throw new ProblemException(ProblemTypes.NotFound);
        return Results.Ok(new LineageDto(subjectKind, subjectId,
            l.Nodes.Select(n => new EvidenceNodeDto(n.Id, n.Kind, n.Label, n.Source, n.SourceRowId, n.RecordId, n.Amount is { } a ? TransactionEndpoints.Money(a, n.Currency!) : null)).ToList(),
            l.Edges.Select(e => new EvidenceEdgeDto(e.From, e.To, e.Relation)).ToList(),
            l.Deliveries.Select(d => new LineageDeliveryDto(d.DeliveryId, d.Fingerprint, d.RowNumber, d.SchemaVersion, null)).ToList()));
    });
}
