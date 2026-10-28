using System.Text.Json.Nodes;
using Npgsql;
using Proofly.Data.Governance.Commands;
using Proofly.Data.Audit;
using Proofly.Data.Jobs;

namespace Proofly.Data.Orchestration;

public enum ReprocessStatus { Requested, NotFound, VersionMismatch, Superseded, InvalidState }

/// <param name="ExpectedRowVersion">Version of the run row the caller read (the ETag); a different current version is a mismatch.</param>
public sealed record ReprocessRequest(string RunId, long ExpectedRowVersion, string Reason, AuditActor Actor, string? RequestId = null);

public sealed record ReprocessResult(ReprocessStatus Status, string? NewRunId = null, string? NewRunState = null, string? Detail = null);

/// <summary>
/// Reprocess = a new run version. One transaction: the run row is locked and version-checked, the old version is marked
/// SUPERSEDED (its sign-off state SUPERSEDED; an old sign-off is kept as it was and is invalidated when the new result
/// differs, see <see cref="RunProcessor"/>), the new version (version + 1, <c>supersedes_run_id</c>) is inserted as
/// PROCESSING, the audit events RUN_REPROCESS_REQUESTED and RUN_STARTED are written and the <c>run.process</c> job is
/// enqueued, all committed together. Nothing of the old version's results, exceptions or sign-off record is changed.
/// Only the current version can be reprocessed, and only in REVIEW_REQUIRED, READY_FOR_SIGN_OFF, SIGNED_OFF or FAILED.
/// </summary>
public sealed class RunReprocessService(
    NpgsqlDataSource dataSource, IAuditWriter audit, IJobQueue jobs, RunProcessingOptions options,
    IRulesetResolver? rulesets = null, TimeProvider? time = null)
{
    private readonly IRulesetResolver _rulesets = rulesets ?? new RulesetResolver(dataSource);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private static readonly string[] Reprocessable = ["REVIEW_REQUIRED", "READY_FOR_SIGN_OFF", "SIGNED_OFF", "FAILED"];

    public async Task<ReprocessResult> RequestAsync(ReprocessRequest req, CancellationToken ct = default)
    {
        if (req.Reason is null || req.Reason.Trim().Length < 3) throw new ArgumentException("A reason of at least 3 characters is required.", nameof(req));
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var old = await Sql.Read(tx, """
            SELECT id, legal_entity_id, business_date, run_version, state, sign_off_state, version, result_hash, last_evidence_arrival_at, ruleset_version
            FROM runs WHERE id=@id FOR UPDATE
            """, ct, r => (Id: r.GetString(0), Le: r.GetString(1), Date: r.GetFieldValue<DateOnly>(2), Version: r.GetInt32(3), State: r.GetString(4), SignOff: r.GetString(5),
                RowVersion: (long)r.GetInt32(6), Hash: r.IsDBNull(7) ? null : r.GetString(7), Arrival: r.IsDBNull(8) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(8)),
            Sql.P("id", req.RunId));
        if (old.Id is null) return new ReprocessResult(ReprocessStatus.NotFound);
        if (old.State == "SUPERSEDED") return new ReprocessResult(ReprocessStatus.Superseded, Detail: "The run is already superseded by a newer version.");
        if (old.RowVersion != req.ExpectedRowVersion) return new ReprocessResult(ReprocessStatus.VersionMismatch);
        if (!Reprocessable.Contains(old.State))
            return new ReprocessResult(ReprocessStatus.InvalidState, Detail: $"A run in state {old.State} cannot be reprocessed; wait for processing to finish.");

        // The new version starts under the ruleset in force now (run.process re-resolves when it runs).
        var rulesetVersion = $"{(await _rulesets.ResolveAsync(_time.GetUtcNow(), tx, ct)).Version}+{options.RulesetVersion}";
        var newId = RunProcessor.RunId(old.Le, old.Date, old.Version + 1);
        var wasSigned = old.State == "SIGNED_OFF";
        await Sql.Exec(tx, "UPDATE runs SET state='SUPERSEDED', sign_off_state='SUPERSEDED' WHERE id=@id", ct, Sql.P("id", old.Id));
        await Sql.Exec(tx, """
            INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state, sign_off_state, supersedes_run_id, last_evidence_arrival_at)
            VALUES (@id, @le, @d, @v, @rs, 'PROCESSING', 'PREPARATION_INCOMPLETE', @old, @arr)
            """, ct, Sql.P("id", newId), Sql.P("le", old.Le), Sql.P("d", old.Date), Sql.P("v", old.Version + 1), Sql.P("rs", rulesetVersion), Sql.P("old", old.Id),
            Sql.P("arr", old.Arrival));

        await audit.AppendAsync(tx, new AuditEventInput("RUN_REPROCESS_REQUESTED", req.Actor, "RUN", old.Id,
            $"Reprocess of run {old.Id} requested; version {old.Version + 1} created", Reason: req.Reason.Trim(), RunId: old.Id, RequestId: req.RequestId,
            BeforeJson: new JsonObject { ["state"] = old.State, ["signOffState"] = old.SignOff, ["version"] = old.Version, ["resultHash"] = old.Hash }.ToJsonString(),
            AfterJson: new JsonObject { ["state"] = "SUPERSEDED", ["signOffState"] = "SUPERSEDED", ["supersededByRunId"] = newId, ["signedOffRunPreserved"] = wasSigned }.ToJsonString()), ct);
        await audit.AppendAsync(tx, new AuditEventInput("RUN_STARTED", req.Actor, "RUN", newId,
            $"Run {newId} started as version {old.Version + 1} of {old.Le} for {old.Date:yyyy-MM-dd}", Reason: req.Reason.Trim(), RunId: newId, RuleVersion: rulesetVersion,
            RequestId: req.RequestId, AfterJson: new JsonObject { ["version"] = old.Version + 1, ["supersedesRunId"] = old.Id, ["trigger"] = "reprocess" }.ToJsonString()), ct);

        await jobs.EnqueueAsync(new JobRequest(RunJobs.ProcessRun, new ProcessRunPayload(old.Le, old.Date, newId, "reprocess"),
            DedupeKey: $"{RunJobs.ProcessRun}:{newId}", RunId: newId), tx, ct);
        await tx.CommitAsync(ct);
        return new ReprocessResult(ReprocessStatus.Requested, newId, "PROCESSING");
    }
}
