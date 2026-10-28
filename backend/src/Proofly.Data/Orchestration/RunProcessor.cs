using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Proofly.Controls.Common;
using Proofly.Controls.Ledger;
using Proofly.Data.Audit;
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
        var loaded = await RunInputLoader.LoadAsync(tx, run.Id, p.LegalEntityId, p.BusinessDate, ingestion.MandatorySources, ct);
        await Sql.Exec(tx, "UPDATE runs SET state='PROCESSING', ruleset_version=@rs, last_evidence_arrival_at=COALESCE(@arr, last_evidence_arrival_at) WHERE id=@id", ct,
            Sql.P("id", run.Id), Sql.P("rs", options.RulesetVersion), Sql.P("arr", loaded.LastArrival));
        await Sql.Exec(tx, "UPDATE source_deliveries SET run_id=@r WHERE legal_entity_id=@le AND business_date=@d AND run_id IS DISTINCT FROM @r", ct,
            Sql.P("r", run.Id), Sql.P("le", p.LegalEntityId), Sql.P("d", p.BusinessDate));

        // 3. Controls (pure).
        var eval = RunEvaluator.Evaluate(loaded.Inputs, options);

        // 4. Persist.
        var predecessor = run.Supersedes is null ? null : await CarryOver.LoadPredecessorAsync(tx, run.Supersedes, ct);
        var carried = 0;
        foreach (var e in eval.Exceptions)
        {
            if (await InsertExceptionAsync(tx, run.Id, run.Supersedes, p, e, predecessor, ct)) { if (predecessor?.ContainsKey(e.Finding.Key) == true) carried++; }
        }
        foreach (var g in eval.Groups) await InsertGroupAsync(tx, run.Id, p.LegalEntityId, g, ct);
        foreach (var b in eval.Balances) await InsertBalanceAsync(tx, run.Id, b, ct);
        foreach (var pos in eval.Positions)
        {
            var sp = pos.Position;
            await Sql.Exec(tx, """
                INSERT INTO safeguarding_positions (run_id, legal_entity_id, currency, obligation_amount, eligible_amount, difference_amount, state, detail)
                VALUES (@r, @le, @c, @o, @e, @d, @s, @detail) ON CONFLICT (run_id, currency) DO NOTHING
                """, ct, Sql.P("r", run.Id), Sql.P("le", p.LegalEntityId), Sql.P("c", sp.Currency), Sql.P("o", sp.Obligation.Amount), Sql.P("e", sp.EligibleTotal.Amount),
                Sql.P("d", sp.Position?.Amount ?? 0m), Sql.P("s", WireNames.ToUpperSnake(sp.State)), Sql.Json("detail", pos.DetailJson));
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
        var unresolvedBlocking = await Sql.Scalar<long>(tx, """
            SELECT count(*) FROM exceptions WHERE run_id=@r AND blocking AND status NOT IN ('RESOLVED','ACCEPTED_DIFFERENCE','FALSE_POSITIVE')
            """, ct, Sql.P("r", run.Id));
        var ready = unresolvedBlocking == 0 && eval.CompletenessState != ControlState.Failed;
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

        // 7. Audit: one event per control and one for the run with the ruleset version and the input fingerprints.
        foreach (var c in eval.Controls)
        {
            await audit.AppendAsync(tx, new AuditEventInput("CONTROL_EXECUTED", Actor, "RUN", run.Id,
                $"{WireNames.ToLowerSnake(c.Area)} control executed: {WireNames.ToUpperSnake(c.State)}, {c.FindingCount} finding(s)",
                RunId: run.Id, RuleVersion: c.RuleVersion,
                AfterJson: new JsonObject { ["control"] = WireNames.ToLowerSnake(c.Area), ["state"] = WireNames.ToUpperSnake(c.State), ["findings"] = c.FindingCount,
                    ["blocking"] = c.BlockingCount, ["records"] = c.RecordCount }.ToJsonString()), ct);
        }
        await audit.AppendAsync(tx, new AuditEventInput("CONTROL_EXECUTED", Actor, "RUN", run.Id,
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

    /// <summary>Marks a run FAILED after the job gave up. The run keeps no partial results (processing is one transaction).</summary>
    public async Task MarkFailedAsync(ProcessRunPayload p, string reason, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var id = await Sql.Scalar<string>(tx, p.RunId is null
            ? "UPDATE runs SET state='FAILED' WHERE id = (SELECT id FROM runs WHERE legal_entity_id=@le AND business_date=@d ORDER BY run_version DESC LIMIT 1) AND result_hash IS NULL AND state IN ('AWAITING_EVIDENCE','PROCESSING') RETURNING id"
            : "UPDATE runs SET state='FAILED' WHERE id=@id AND result_hash IS NULL AND state IN ('AWAITING_EVIDENCE','PROCESSING') RETURNING id", ct,
            Sql.P("id", p.RunId), Sql.P("le", p.LegalEntityId), Sql.P("d", p.BusinessDate));
        if (id is null) { await tx.RollbackAsync(ct); return; }
        await audit.AppendAsync(tx, new AuditEventInput("CONTROL_EXECUTED", Actor, "RUN", id, $"Run {id} processing failed",
            Reason: reason.Length >= 3 ? reason[..Math.Min(reason.Length, 500)] : "processing failed", RunId: id, RuleVersion: options.RulesetVersion,
            AfterJson: new JsonObject { ["event"] = "RUN_FAILED", ["state"] = "FAILED" }.ToJsonString()), ct);
        await tx.CommitAsync(ct);
    }

    private async Task<bool> InsertExceptionAsync(NpgsqlTransaction tx, string runId, string? supersedes, ProcessRunPayload p, ExceptionDraft e,
        IReadOnlyDictionary<string, CarryOver.Predecessor>? predecessor, CancellationToken ct)
    {
        var f = e.Finding;
        CarryOver.Predecessor? pred = null;
        predecessor?.TryGetValue(f.Key, out pred);
        var decision = pred is null ? null : CarryOver.Decide(pred, e);
        var inserted = await Sql.Scalar<int>(tx, """
            INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status, value_amount, value_currency,
                                    owner_user_id, due_at, blocking, source_file, source_row_id, delivery_id, resolution_kind, resolution_reason, finding_key, predecessor_exception_id)
            VALUES (@id, @r, @le, @d, @t, @c, @rc, @sev, @st, @va, @vc, @own, @due, @blk, @sf, @sr, @del, @rk, @rr, @fk, @pred)
            ON CONFLICT (id) DO NOTHING RETURNING 1
            """, ct,
            Sql.P("id", e.Id), Sql.P("r", runId), Sql.P("le", p.LegalEntityId), Sql.P("d", p.BusinessDate), Sql.P("t", e.Title), Sql.P("c", WireNames.ToLowerSnake(f.Area)),
            Sql.P("rc", f.ReasonCode), Sql.P("sev", WireNames.ToUpperSnake(f.Severity)), Sql.P("st", decision?.Status ?? "OPEN"),
            Sql.P("va", f.Value?.Amount), Sql.P("vc", f.Value?.Currency), Sql.P("own", pred?.OwnerUserId), Sql.P("due", e.DueAt), Sql.P("blk", f.Blocking),
            Sql.P("sf", e.SourceFile), Sql.P("sr", e.SourceRowId), Sql.P("del", e.DeliveryId), Sql.P("rk", decision?.ResolutionKind), Sql.P("rr", decision?.ResolutionReason),
            Sql.P("fk", f.Key), Sql.P("pred", pred?.Id));
        if (inserted == 0) return false;

        await Sql.Exec(tx, """
            INSERT INTO exception_rule_traces (exception_id, rule_id, rule_version, tolerance, expected_effect, description, calculation)
            VALUES (@id, @rid, @rv, @tol, @eff, @desc, @calc)
            """, ct, Sql.P("id", e.Id), Sql.P("rid", e.RuleId), Sql.P("rv", f.RuleVersion), Sql.P("tol", e.Tolerance), Sql.P("eff", e.ExpectedEffect),
            Sql.P("desc", f.Description), Sql.Json("calc", e.CalculationJson));
        foreach (var l in e.Links)
            await Sql.Exec(tx, "INSERT INTO exception_links (exception_id, record_table, record_id) VALUES (@e, @t, @i) ON CONFLICT DO NOTHING", ct,
                Sql.P("e", e.Id), Sql.P("t", l.Table), Sql.P("i", l.DbId));

        if (pred is not null) await CarryOver.CopyHistoryAsync(tx, pred, e.Id, decision!, ct);
        await Sql.Exec(tx, "INSERT INTO exception_timeline (exception_id, kind, actor_type, actor_id, summary) VALUES (@e, 'SYSTEM', 'SYSTEM', NULL, @s)", ct,
            Sql.P("e", e.Id), Sql.P("s", $"Raised by the {WireNames.ToLowerSnake(f.Area)} control ({e.RuleId}, rule version {f.RuleVersion})."));

        await audit.AppendAsync(tx, new AuditEventInput("EXCEPTION_CREATED", Actor, "EXCEPTION", e.Id,
            $"Exception {f.ReasonCode} raised by {WireNames.ToLowerSnake(f.Area)}", Reason: "Control finding " + f.ReasonCode, RunId: runId, RuleVersion: f.RuleVersion,
            AfterJson: new JsonObject
            {
                ["findingKey"] = f.Key, ["severity"] = WireNames.ToUpperSnake(f.Severity), ["blocking"] = f.Blocking, ["status"] = decision?.Status ?? "OPEN",
                ["value"] = f.Value is { } v ? v.AmountString + " " + v.Currency : null, ["continuesException"] = pred?.Id,
            }.ToJsonString()), ct);
        return true;
    }

    private static async Task InsertGroupAsync(NpgsqlTransaction tx, string runId, string legalEntityId, MatchGroupDraft d, CancellationToken ct)
    {
        var g = d.Group;
        var inserted = await Sql.Scalar<int>(tx, """
            INSERT INTO match_groups (id, run_id, legal_entity_id, status, difference_amount, currency, manual, rule_id, tolerance, rationale, cardinality)
            VALUES (@id, @r, @le, @s, @da, @c, false, @rule, @tol, @rat, @card) ON CONFLICT (id) DO NOTHING RETURNING 1
            """, ct, Sql.P("id", d.Id), Sql.P("r", runId), Sql.P("le", legalEntityId), Sql.P("s", WireNames.ToUpperSnake(g.Status)),
            Sql.P("da", g.ValueDifference?.Amount), Sql.P("c", g.ValueDifference?.Currency), Sql.P("rule", g.RuleId), Sql.P("tol", d.ToleranceText),
            Sql.P("rat", g.Rationale), Sql.P("card", g.Cardinality));
        if (inserted == 0) return;
        foreach (var c in d.Candidates)
            await Sql.Exec(tx, """
                INSERT INTO match_candidates (match_group_id, kind, record_table, record_id, amount, currency, score, selected, side)
                VALUES (@g, @k, @t, @i, @a, @c, NULL, @sel, @side)
                """, ct, Sql.P("g", d.Id), Sql.P("k", c.Kind), Sql.P("t", c.Table), Sql.P("i", c.DbId), Sql.P("a", c.Amount.Amount), Sql.P("c", c.Amount.Currency),
                Sql.P("sel", c.Selected), Sql.P("side", c.Side));
        foreach (var x in d.ExceptionIds)
            await Sql.Exec(tx, "INSERT INTO match_group_exceptions (match_group_id, exception_id) VALUES (@g, @e) ON CONFLICT DO NOTHING", ct, Sql.P("g", d.Id), Sql.P("e", x));
    }

    private async Task InsertBalanceAsync(NpgsqlTransaction tx, string runId, IndependentBalance b, CancellationToken ct)
    {
        static JsonObject Money(Money m) => new() { ["amount"] = m.AmountString, ["currency"] = m.Currency };
        var contributions = new JsonArray(b.Contributions.Select(c => (JsonNode)new JsonObject
        {
            ["source"] = c.Record.SourceName, ["sourceRowId"] = c.Record.SourceRowId, ["kind"] = c.Kind.ToString(), ["direction"] = c.Direction.ToString(),
            ["amount"] = Money(c.Amount), ["independentEffect"] = Money(c.IndependentEffect), ["recordedEffect"] = Money(c.RecordedEffect), ["variance"] = Money(c.Variance),
        }).ToArray());
        var exclusions = new JsonArray(b.Exclusions.Select(x => (JsonNode)new JsonObject
            { ["source"] = x.Record.SourceName, ["sourceRowId"] = x.Record.SourceRowId, ["reason"] = x.Reason }).ToArray());
        await Sql.Exec(tx, """
            INSERT INTO account_control_results (run_id, account_id, currency, opening, credits, debits, adjustments, independent, recorded, difference,
                                                 opening_supported, contributions, exclusions, rule_version)
            VALUES (@r, @a, @c, @op, @cr, @db, @adj, @ind, @rec, @diff, @sup, @con, @exc, @rv) ON CONFLICT DO NOTHING
            """, ct, Sql.P("r", runId), Sql.P("a", b.AccountId), Sql.P("c", b.Currency), Sql.P("op", b.Opening?.Amount), Sql.P("cr", b.Credits.Amount), Sql.P("db", b.Debits.Amount),
            Sql.P("adj", b.Adjustments.Amount), Sql.P("ind", b.Independent?.Amount), Sql.P("rec", b.Recorded?.Amount), Sql.P("diff", b.Difference?.Amount),
            Sql.P("sup", b.OpeningSupported), Sql.Json("con", contributions.ToJsonString()), Sql.Json("exc", exclusions.ToJsonString()), Sql.P("rv", options.Ledger.RuleVersion));
    }
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
