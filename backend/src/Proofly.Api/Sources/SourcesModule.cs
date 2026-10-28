using System.Text.RegularExpressions;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Common;
using Proofly.Contracts.Sources;
using Proofly.Data.Sources;
using Proofly.Domain.Enums;

namespace Proofly.Api.Sources;

public static class SourcesModule
{
    public static IServiceCollection AddSources(this IServiceCollection services)
    {
        services.AddSourceReads();
        services.AddSingleton<IEndpointModule, SourceEndpoints>();
        Commands.SourceCommandsModule.AddSourceCommands(services);
        return services;
    }

}

internal sealed record RecordCursor(int G, long O);

internal sealed partial class SourceEndpoints(ISourceReadRepository repo) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/runs/{runId}/deliveries", async (string runId, HttpContext ctx, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var items = await repo.ListRunDeliveriesAsync(runId, ScopeOf(caller), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            foreach (var d in items) { Require(caller, d); }
            return Results.Ok(new SourceDeliveryListDto(items.Select(ToDto).ToList()));
        });

        app.MapGet("/api/v1/deliveries/{deliveryId}", async (string deliveryId, HttpContext ctx, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var detail = await repo.GetDeliveryAsync(deliveryId, ScopeOf(caller), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            Require(caller, detail.Delivery);
            var etag = ETags.ForVersion(detail.Delivery.Version);
            if (ctx.IsNotModified(etag)) { return Results.StatusCode(StatusCodes.Status304NotModified); }
            ctx.Set(etag);
            return Results.Ok(ToDetailDto(detail, caller));
        });

        app.MapGet("/api/v1/deliveries/{deliveryId}/processing-history", async (string deliveryId, HttpContext ctx, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            await RequireDeliveryAsync(caller, deliveryId, ct);
            var steps = await repo.GetProcessingHistoryAsync(deliveryId, ScopeOf(caller), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            return Results.Ok(new ProcessingHistoryDto(steps.Select(s => new ProcessingStepDto(s.Id, s.At, s.Stage, s.Outcome,
                new ActorDto(s.ActorType, s.ActorId, s.ActorName), s.Message)).ToList()));
        });

        app.MapGet("/api/v1/deliveries/{deliveryId}/records", async (string deliveryId, HttpContext ctx, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            await RequireDeliveryAsync(caller, deliveryId, ct);
            var page = PageRequest.From(ctx);
            var cursor = page.Decode<RecordCursor>();
            bool? accepted = ctx.Request.Query.TryGetValue("accepted", out var a) && bool.TryParse(a, out var ab) ? ab : null;
            var search = ctx.Request.Query.TryGetValue("search", out var s) ? s.ToString() : null;
            var result = await repo.ListRecordsAsync(deliveryId, new RecordQuery(accepted, search, page.Limit, cursor?.G, cursor?.O, page.IncludeTotal), ScopeOf(caller), ct)
                ?? throw new ProblemException(ProblemTypes.NotFound);
            var paged = Paging.Create(result.Items, page, r => new RecordCursor(r.Group, r.Ordinal), result.Total);
            return Results.Ok(new PageDto<SourceRecordDto>(paged.Items.Select(ToDto).ToList(), paged.Page));
        });

        app.MapGet("/api/v1/source-records/{recordId}", async (string recordId, HttpContext ctx, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var found = await repo.GetRecordAsync(recordId, ScopeOf(caller), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            Require(caller, found.Delivery);
            return Results.Ok(ToDto(found.Record));
        });
    }

    private async Task RequireDeliveryAsync(Caller caller, string deliveryId, CancellationToken ct)
    {
        var d = await repo.GetDeliveryAsync(deliveryId, ScopeOf(caller), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        Require(caller, d.Delivery);
    }

    internal static EntityScope ScopeOf(Caller c) => c.LegalEntityIds.Contains(Caller.AllEntities)
        ? EntityScope.Everything : new EntityScope(false, c.LegalEntityIds.ToArray());

    /// <summary>Entity-less deliveries are only visible to all-entity callers; otherwise 404 out-of-scope, 403 without the permission.</summary>
    internal static void Require(Caller caller, DeliverySummary d)
    {
        if (d.LegalEntityId is null)
        {
            if (!caller.LegalEntityIds.Contains(Caller.AllEntities)) { throw new ProblemException(ProblemTypes.NotFound); }
            if (!caller.Has(Permission.SourceView)) { throw new ProblemException(ProblemTypes.Forbidden); }
            return;
        }
        caller.Require(Permission.SourceView, d.LegalEntityId);
    }

    private static MoneyDto M(MoneyAmount m) => new(m.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Currency);

    private static SourceDeliveryDto ToDto(DeliverySummary d) => new(d.Id, d.SourceName, d.FileName, d.BusinessDate, d.SchemaVersion,
        d.DeclaredRecordCount, d.TotalRowCount ?? d.AcceptedRecords + d.RejectedRecords, d.DeclaredTotals.Select(M).ToList(),
        d.ExpectedBy, d.ReceivedAt, d.CorrectionOfDeliveryId, d.SupersededByDeliveryId, d.State, d.AcceptedRecords, d.RejectedRecords, d.Qualifications, d.FindingIds);

    private static SourceRecordDto ToDto(SourceRecordRow r) => new(r.RecordId, r.SourceName, r.SourceRowId, r.DeliveryId, r.RowNumber, r.Accepted,
        r.Fields, r.RejectionReasons, r.ContentFingerprint, r.FindingIds);

    [GeneratedRegex(@"delivered as (\S+?)\.?$")]
    private static partial Regex DuplicateOf();

    internal static SourceDeliveryDetailDto ToDetailDto(DeliveryDetail detail, Caller caller)
    {
        var d = detail.Delivery;
        var b = ToDto(d);
        var le = d.LegalEntityId;
        bool Can(Permission p) => le is null ? caller.Has(p) : caller.Can(p, le);
        var failure = (string code) => detail.Failures.FirstOrDefault(f => f.ReasonCode == code);
        var duplicate = failure("DUPLICATE_DELIVERY");
        var actions = new List<string>();
        if (Can(Permission.SourceRetry) && DeliveryRules.CanRetryIngestion(d.State, d.Ingested, duplicate is not null)) { actions.Add("RETRY_INGESTION"); }
        if (Can(Permission.SourceSupersede) && DeliveryRules.CanSupersede(d.State, d.SupersededByDeliveryId is not null)) { actions.Add("MARK_SUPERSEDED"); }
        return new SourceDeliveryDetailDto(b.DeliveryId, b.SourceName, b.FileName, b.BusinessDate, b.SchemaVersion, b.DeclaredRecordCount, b.ActualRecordCount,
            b.DeclaredTotals, b.ExpectedBy, b.ReceivedAt, b.CorrectionOfDeliveryId, b.SupersededByDeliveryId, b.State, b.AcceptedRecords, b.RejectedRecords,
            b.Qualifications, b.FindingIds,
            new FingerprintDto("SHA-256", d.Fingerprint), d.SequenceNumber, null, null, Checks(d, detail, duplicate), d.ActualTotals.Select(M).ToList(), actions);
    }

    private static List<CheckResultDto> Checks(DeliverySummary d, DeliveryDetail detail, DeliveryFailureRow? duplicate)
    {
        DeliveryFailureRow? F(string code) => detail.Failures.FirstOrDefault(f => f.ReasonCode == code);
        string? related = duplicate is null ? null : DuplicateOf().Match(duplicate.Reason) is { Success: true } m ? m.Groups[1].Value : null;
        var rejectedAtRegistration = d.State == "REJECTED";
        var measured = d.Ingested;

        CheckResultDto Fail(string check, DeliveryFailureRow f, string? rel = null) => new(check, "FAIL", f.Reason, rel);
        var list = new List<CheckResultDto>();

        list.Add((F("EVIDENCE_MISSING") ?? F("FILE_UNREADABLE")) is { } fp ? Fail("FINGERPRINT", fp) : new("FINGERPRINT", "PASS", "SHA-256 recorded for the received bytes", null));
        list.Add(duplicate is not null ? Fail("DUPLICATE", duplicate, related) : new("DUPLICATE", "PASS", "No earlier delivery with the same content", null));
        list.Add(d.SequenceNumber is null && F("SEQUENCE_GAP") is null ? new("SEQUENCE", "NOT_APPLICABLE", "Source does not number its deliveries", null)
            : F("SEQUENCE_GAP") is { } sg ? Fail("SEQUENCE", sg) : new("SEQUENCE", "PASS", $"Sequence {d.SequenceNumber} follows the previous delivery", null));
        list.Add(F("SCHEMA_UNSUPPORTED") is { } sc ? Fail("SCHEMA", sc)
            : !measured && !rejectedAtRegistration ? new("SCHEMA", "NOT_APPLICABLE", "Not ingested yet", null) : new("SCHEMA", "PASS", $"Schema version {d.SchemaVersion}", null));

        if (F("CONTROL_COUNT_MISMATCH") is { } cc) { list.Add(Fail("RECORD_COUNT", cc)); }
        else if (d.DeclaredRecordCount is null || d.TotalRowCount is null) { list.Add(new("RECORD_COUNT", "NOT_APPLICABLE", d.DeclaredRecordCount is null ? "No record count declared" : "Not ingested yet", null)); }
        else if (d.DeclaredRecordCount == d.TotalRowCount) { list.Add(new("RECORD_COUNT", "PASS", $"{d.TotalRowCount} rows as declared", null)); }
        else { list.Add(new("RECORD_COUNT", "FAIL", $"Declared {d.DeclaredRecordCount}, received {d.TotalRowCount}", null)); }

        if (F("CONTROL_VALUE_MISMATCH") is { } cv) { list.Add(Fail("CONTROL_TOTALS", cv)); }
        else if (d.DeclaredTotals.Count == 0) { list.Add(new("CONTROL_TOTALS", "NOT_APPLICABLE", "No control totals declared", null)); }
        else if (!measured) { list.Add(new("CONTROL_TOTALS", "NOT_APPLICABLE", "Not ingested yet", null)); }
        else
        {
            var actual = d.ActualTotals.ToDictionary(a => a.Currency, a => a.Amount);
            var bad = d.DeclaredTotals.Where(x => !actual.TryGetValue(x.Currency, out var v) || v != x.Amount).Select(x => x.Currency).ToList();
            list.Add(bad.Count == 0 ? new("CONTROL_TOTALS", "PASS", "Declared and actual totals agree in every currency", null)
                : new("CONTROL_TOTALS", "FAIL", "Declared and actual totals differ in " + string.Join(", ", bad), null));
        }
        return list;
    }
}
