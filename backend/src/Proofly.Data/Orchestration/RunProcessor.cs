using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Proofly.Controls.Common;
using Proofly.Controls.Ledger;
using Proofly.Data.Audit;
using Proofly.Data.Exceptions;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Domain;
using Proofly.Domain.Enums;

namespace Proofly.Data.Orchestration;

/// <summary>
/// Processes one run version in ONE transaction: locks (or creates) the run, loads its accepted canonical evidence,
/// evaluates the five controls (<see cref="RunEvaluator"/>), persists exceptions with rule traces and record links, match
/// groups with candidates, safeguarding positions, independent account results and per-control results, sets the run
/// state, the result hash and the input manifest, and writes the audit events, all before commit. A run that already
/// has a result is never touched again (idempotent under at-least-once job delivery). A reprocessed version is a new
/// run (see <see cref="RunReprocessService"/>); its exceptions continue those of the version it supersedes
/// (<see cref="CarryOver"/>).
/// </summary>
public sealed class RunProcessor(NpgsqlDataSource dataSource, IAuditWriter audit, IngestionOptions ingestion, RunProcessingOptions options)
{
    internal static readonly AuditActor Actor = AuditActor.System("run-orchestration");

    public static string RunId(string legalEntityId, DateOnly date, int version) => $"RUN-{legalEntityId}-{date:yyyyMMdd}-v{version}";

    public async Task<RunProcessingOutcome> ProcessAsync(ProcessRunPayload p, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Sql.Exec(tx, "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", ct, Sql.P("k", $"run|{p.LegalEntityId}|{p.BusinessDate:yyyy-MM-dd}"));

        // 1. Create or lock the run.
        var run = await Sql.Read(tx, p.RunId is null
            ? "SELECT id, run_version, state, result_hash, supersedes_run_id, ruleset_version FROM runs WHERE legal_entity_id=@le AND business_date=@d ORDER BY run_version DESC LIMIT 1 FOR UPDATE"
            : "SELECT id, run_version, state, result_hash, supersedes_run_id, ruleset_version FROM runs WHERE id=@id FOR UPDATE", ct,
            r => (Id: r.GetString(0), Version: r.GetInt32(1), State: r.GetString(2), Hash: r.IsDBNull(3) ? null : r.GetString(3), Supersedes: r.IsDBNull(4) ? null : r.GetString(4)),
            Sql.P("id", p.RunId), Sql.P("le", p.LegalEntityId), Sql.P("d", p.BusinessDate));
        if (run.Id is null && p.RunId is not null) return new RunProcessingOutcome(RunProcessingStatus.NotFound, p.RunId, null, null, 0, 0);
        if (run.Id is null)
        {
            var id = RunId(p.LegalEntityId, p.BusinessDate, 1);
            await Sql.Exec(tx, """
                INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state, sign_off_state)
                VALUES (@id, @le, @d, 1, @rs, 'PROCESSING', 'PREPARATION_INCOMPLETE')
                """, ct, Sql.P("id", id), Sql.P("le", p.LegalEntityId), Sql.P("d", p.BusinessDate), Sql.P("rs", options.RulesetVersion));
            await audit.AppendAsync(tx, new AuditEventInput("RUN_STARTED", Actor, "RUN", id, $"Run {id} started for {p.LegalEntityId} on {p.BusinessDate:yyyy-MM-dd}",
                RunId: id, RuleVersion: options.RulesetVersion, AfterJson: new JsonObject { ["version"] = 1, ["trigger"] = p.Trigger }.ToJsonString()), ct);
            run = (id, 1, "PROCESSING", null, null);
        }
        if (run.State == "SUPERSEDED") return new RunProcessingOutcome(RunProcessingStatus.RunSuperseded, run.Id, run.Hash, run.State, 0, 0);
        if (run.Hash is not null || run.State is not ("AWAITING_EVIDENCE" or "PROCESSING" or "FAILED"))
            return new RunProcessingOutcome(RunProcessingStatus.AlreadyProcessed, run.Id, run.Hash, run.State, 0, 0);

        // 2. Evidence in, run marked as processing, deliveries linked to this run version.
        RunInputLoader.Loaded loaded;
        using (RunTelemetry.Source.StartActivity("run.load")) loaded = await RunInputLoader.LoadAsync(tx, run.Id, p.LegalEntityId, p.BusinessDate, ingestion.MandatorySources, ct);
        await Sql.Exec(tx, "UPDATE runs SET state='PROCESSING', ruleset_version=@rs, last_evidence_arrival_at=COALESCE(@arr, last_evidence_arrival_at) WHERE id=@id", ct,
            Sql.P("id", run.Id), Sql.P("rs", options.RulesetVersion), Sql.P("arr", loaded.LastArrival));
        await Sql.Exec(tx, "UPDATE source_deliveries SET run_id=@r WHERE legal_entity_id=@le AND business_date=@d AND run_id IS DISTINCT FROM @r", ct,
            Sql.P("r", run.Id), Sql.P("le", p.LegalEntityId), Sql.P("d", p.BusinessDate));

        // 3. Controls (pure).
        RunEvaluation eval;
        using (RunTelemetry.Source.StartActivity("run.evaluate")) eval = RunEvaluator.Evaluate(loaded.Inputs, options);

        // 4. Persist.
        using var persistActivity = RunTelemetry.Source.StartActivity("run.persist");
        var predecessor = run.Supersedes is null ? null : await CarryOver.LoadPredecessorAsync(tx, run.Supersedes, ct);
        var items = new List<RunBulkPersistence.ExceptionItem>(eval.Exceptions.Count);
        foreach (var e in eval.Exceptions)
        {
            CarryOver.Predecessor? pred = null;
            predecessor?.TryGetValue(e.Finding.Key, out pred);
            items.Add(new RunBulkPersistence.ExceptionItem(e, pred, pred is null ? null : CarryOver.Decide(pred, e)));
        }
        var insertedExceptions = await RunBulkPersistence.InsertExceptionsAsync(tx, audit, Actor, run.Id, p, items, ct);
        var carried = items.Count(i => i.Pred is not null && insertedExceptions.Contains(i.Draft.Id));
        await RunBulkPersistence.InsertGroupsAsync(tx, run.Id, p.LegalEntityId, eval.Groups, ct);
        await RunBulkPersistence.InsertBalancesAsync(tx, run.Id, options.Ledger.RuleVersion, eval.Balances, ct);
        foreach (var pos in eval.Positions)
        {
            var sp = pos.Position;
            await Sql.Exec(tx, """
                INSERT INTO safeguarding_positions (run_id, legal_entity_id, currency, obligation_amount, eligible_amount, difference_amount, state, detail)
                VALUES (@r, @le, @c, @o, @e, @d, @s, @detail) ON CONFLICT (run_id, currency) DO NOTHING
                """, ct, Sql.P("r", run.Id), Sql.P("le", p.LegalEntityId), Sql.P("c", sp.Currency), Sql.P("o", sp.Obligation.Amount), Sql.P("e", sp.EligibleTotal.Amount),
                Sql.P("d", sp.Position?.Amount) /* null: no difference computed (QUALIFIED) */, Sql.P("s", WireNames.ToUpperSnake(sp.State)), Sql.Json("detail", pos.DetailJson));
        }
        foreach (var c in eval.Controls)
        {
            await Sql.Exec(tx, """
                INSERT INTO run_control_results (run_id, control, state, rule_version, record_count, finding_count, blocking_finding_count)
                VALUES (@r, @c, @s, @v, @n, @f, @b) ON CONFLICT (run_id, control) DO NOTHING
                """, ct, Sql.P("r", run.Id), Sql.P("c", WireNames.ToLowerSnake(c.Area)), Sql.P("s", WireNames.ToUpperSnake(c.State)), Sql.P("v", c.RuleVersion),
                Sql.P("n", c.RecordCount), Sql.P("f", c.FindingCount), Sql.P("b", c.BlockingCount));
        }

        // 5. Run state: READY_FOR_SIGN_OFF only when completeness did not fail and no blocking exception is unresolved.
        var ready = !await UnresolvedBlockingExceptions.HasAsync(conn, tx, run.Id, ct) && eval.CompletenessState != ControlState.Failed;
        var state = ready ? "READY_FOR_SIGN_OFF" : "REVIEW_REQUIRED";
        var inputs = new JsonArray(loaded.Manifest.Select(m => (JsonNode)new JsonObject
            { ["deliveryId"] = m.DeliveryId, ["sourceName"] = m.Source, ["fingerprint"] = m.Fingerprint, ["state"] = m.State }).ToArray());
        await Sql.Exec(tx, """
            UPDATE runs SET state=@s, sign_off_state=@so, result_hash=@h, input_manifest=@m, processed_at=now() WHERE id=@id
            """, ct, Sql.P("id", run.Id), Sql.P("s", state), Sql.P("so", ready ? "READY_FOR_REVIEW" : "PREPARATION_INCOMPLETE"), Sql.P("h", eval.ResultHash),
            Sql.Json("m", inputs.ToJsonString()));

        // 6. The version this run supersedes: a signed-off result whose conclusion changed is invalidated, otherwise superseded.
        if (run.Supersedes is not null)
        {
            await Sql.Exec(tx, """
                UPDATE runs SET sign_off_state = 'INVALIDATED'
                WHERE id=@old AND sign_off_state <> 'INVALIDATED' AND result_hash IS DISTINCT FROM @h
                  AND EXISTS (SELECT 1 FROM run_signoffs s WHERE s.run_id = runs.id AND s.state = 'SIGNED_OFF')
                """, ct, Sql.P("old", run.Supersedes), Sql.P("h", eval.ResultHash));
        }

        persistActivity?.Stop();
        // 7. Audit: one event per control and one for the run with the ruleset version and the input fingerprints.
        foreach (var c in eval.Controls)
        {
            await audit.AppendAsync(tx, new AuditEventInput("CONTROL_EXECUTED", Actor, "RUN", run.Id,
                $"{WireNames.ToLowerSnake(c.Area)} control executed: {WireNames.ToUpperSnake(c.State)}, {c.FindingCount} finding(s)",
                RunId: run.Id, RuleVersion: c.RuleVersion,
                AfterJson: new JsonObject { ["control"] = WireNames.ToLowerSnake(c.Area), ["state"] = WireNames.ToUpperSnake(c.State), ["findings"] = c.FindingCount,
                    ["blocking"] = c.BlockingCount, ["records"] = c.RecordCount }.ToJsonString()), ct);
        }
        await audit.AppendAsync(tx, new AuditEventInput("RUN_PROCESSED", Actor, "RUN", run.Id,
            $"Run {run.Id} processed (version {run.Version}): {state}, {eval.Exceptions.Count} exception(s), {carried} continued from the previous version",
            RunId: run.Id, RuleVersion: eval.RulesetVersion,
            AfterJson: new JsonObject
            {
                ["event"] = "RUN_PROCESSED", ["state"] = state, ["version"] = run.Version, ["resultHash"] = eval.ResultHash, ["rulesetVersion"] = eval.RulesetVersion,
                ["exceptions"] = eval.Exceptions.Count, ["carriedOver"] = carried, ["inputs"] = inputs,
            }.ToJsonString()), ct);

        await tx.CommitAsync(ct);
        return new RunProcessingOutcome(RunProcessingStatus.Processed, run.Id, eval.ResultHash, state, eval.Exceptions.Count, carried);
    }

    /// <summary>
    /// Marks a run FAILED after the job gave up, in its own transaction (the processing transaction was rolled back). When the first
    /// attempt failed no run row exists, so a FAILED run marker (version 1, no result, no exceptions) is created together with
    /// its RUN_STARTED and failure audit events; a later successful attempt processes that same run (FAILED is a processable state).
    /// </summary>
    public async Task MarkFailedAsync(ProcessRunPayload p, string reason, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Sql.Exec(tx, "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", ct, Sql.P("k", $"run|{p.LegalEntityId}|{p.BusinessDate:yyyy-MM-dd}"));
        var id = await Sql.Scalar<string>(tx, p.RunId is null
            ? "UPDATE runs SET state='FAILED' WHERE id = (SELECT id FROM runs WHERE legal_entity_id=@le AND business_date=@d ORDER BY run_version DESC LIMIT 1) AND result_hash IS NULL AND state IN ('AWAITING_EVIDENCE','PROCESSING') RETURNING id"
            : "UPDATE runs SET state='FAILED' WHERE id=@id AND result_hash IS NULL AND state IN ('AWAITING_EVIDENCE','PROCESSING') RETURNING id", ct,
            Sql.P("id", p.RunId), Sql.P("le", p.LegalEntityId), Sql.P("d", p.BusinessDate));
        if (id is null && p.RunId is null)
        {
            var exists = await Sql.Scalar<long>(tx, "SELECT count(*) FROM runs WHERE legal_entity_id=@le AND business_date=@d", ct, Sql.P("le", p.LegalEntityId), Sql.P("d", p.BusinessDate));
            if (exists == 0)
            {
                id = RunId(p.LegalEntityId, p.BusinessDate, 1);
                await Sql.Exec(tx, """
                    INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state, sign_off_state)
                    VALUES (@id, @le, @d, 1, @rs, 'FAILED', 'PREPARATION_INCOMPLETE')
                    """, ct, Sql.P("id", id), Sql.P("le", p.LegalEntityId), Sql.P("d", p.BusinessDate), Sql.P("rs", options.RulesetVersion));
                await audit.AppendAsync(tx, new AuditEventInput("RUN_STARTED", Actor, "RUN", id, $"Run {id} started for {p.LegalEntityId} on {p.BusinessDate:yyyy-MM-dd}",
                    RunId: id, RuleVersion: options.RulesetVersion, AfterJson: new JsonObject { ["version"] = 1, ["trigger"] = p.Trigger }.ToJsonString()), ct);
            }
        }
        if (id is null) { await tx.RollbackAsync(ct); return; }
        await audit.AppendAsync(tx, new AuditEventInput("CONTROL_EXECUTED", Actor, "RUN", id, $"Run {id} processing failed",
            Reason: reason.Length >= 3 ? reason[..Math.Min(reason.Length, 500)] : "processing failed", RunId: id, RuleVersion: options.RulesetVersion,
            AfterJson: new JsonObject { ["event"] = "RUN_FAILED", ["state"] = "FAILED" }.ToJsonString()), ct);
        await tx.CommitAsync(ct);
    }
}

/// <summary>Phase timing of run processing for diagnostics (benchmarks, tracing). No listener, no cost.</summary>
public static class RunTelemetry
{
    public static readonly ActivitySource Source = new("Proofly.Runs");
}

/// <summary>Small command helpers shared by the orchestration classes.</summary>
internal static class Sql
{
    public static NpgsqlParameter P(string name, object? value) => new(name, value ?? DBNull.Value);
    public static NpgsqlParameter Json(string name, string json) => new(name, NpgsqlDbType.Jsonb) { Value = json };

    private static NpgsqlCommand Cmd(NpgsqlTransaction tx, string sql, NpgsqlParameter[] ps)
    {
        var c = new NpgsqlCommand(sql, tx.Connection, tx);
        foreach (var p in ps)
        {
            // Parameters the statement does not use are dropped (callers pass one shared list for the id/entity/date variants).
            if (sql.Contains("@" + p.ParameterName, StringComparison.Ordinal)) c.Parameters.Add(p);
        }
        return c;
    }

    public static async Task<int> Exec(NpgsqlTransaction tx, string sql, CancellationToken ct, params NpgsqlParameter[] ps)
    {
        await using var c = Cmd(tx, sql, ps);
        return await c.ExecuteNonQueryAsync(ct);
    }

    public static async Task<T?> Scalar<T>(NpgsqlTransaction tx, string sql, CancellationToken ct, params NpgsqlParameter[] ps)
    {
        await using var c = Cmd(tx, sql, ps);
        var o = await c.ExecuteScalarAsync(ct);
        return o is null or DBNull ? default : (T)o;
    }

    /// <summary>First row mapped, or default.</summary>
    public static async Task<T?> Read<T>(NpgsqlTransaction tx, string sql, CancellationToken ct, Func<NpgsqlDataReader, T> map, params NpgsqlParameter[] ps)
    {
        await using var c = Cmd(tx, sql, ps);
        await using var r = await c.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? map(r) : default;
    }

    public static async Task<List<T>> ReadAll<T>(NpgsqlTransaction tx, string sql, CancellationToken ct, Func<NpgsqlDataReader, T> map, params NpgsqlParameter[] ps)
    {
        await using var c = Cmd(tx, sql, ps);
        await using var r = await c.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await r.ReadAsync(ct)) list.Add(map(r));
        return list;
    }
}
