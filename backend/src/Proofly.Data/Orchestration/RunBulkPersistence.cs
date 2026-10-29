using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Proofly.Controls.Ledger;
using Proofly.Data.Audit;
using Proofly.Domain;
using Proofly.Domain.Enums;

namespace Proofly.Data.Orchestration;

/// <summary>
/// Set-based persistence of a run's results (performance spike #48: the row-by-row version spent more than 90% of the processing time
/// in network round trips). Each table is written with one <c>INSERT ... SELECT FROM unnest(arrays)</c> per chunk, in the caller's
/// transaction, with the same column values, the same <c>ON CONFLICT DO NOTHING</c> idempotency and the same order of children
/// (a child row is written only for a parent that was actually inserted).
/// </summary>
internal static class RunBulkPersistence
{
    private const int Chunk = 1000;

    private static NpgsqlParameter Arr(string name, NpgsqlDbType t, Array v) => new(name, NpgsqlDbType.Array | t) { Value = v };
    private static NpgsqlParameter Txt(string name, IEnumerable<string?> v) => Arr(name, NpgsqlDbType.Text, v.ToArray());

    private static async Task<List<string>> ReturnIds(NpgsqlTransaction tx, string sql, CancellationToken ct, params NpgsqlParameter[] ps)
    {
        await using var c = new NpgsqlCommand(sql, tx.Connection, tx);
        foreach (var p in ps) c.Parameters.Add(p);
        var ids = new List<string>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ids.Add(r.GetString(0));
        return ids;
    }

    private static async Task Run(NpgsqlTransaction tx, string sql, CancellationToken ct, params NpgsqlParameter[] ps)
    {
        await using var c = new NpgsqlCommand(sql, tx.Connection, tx);
        foreach (var p in ps) c.Parameters.Add(p);
        await c.ExecuteNonQueryAsync(ct);
    }

    public sealed record ExceptionItem(ExceptionDraft Draft, CarryOver.Predecessor? Pred, CarryOver.Decision? Decision);

    /// <summary>Inserts the exceptions and everything that hangs off them (rule trace, record links, timeline, carried history, audit). Returns the inserted ids.</summary>
    public static async Task<HashSet<string>> InsertExceptionsAsync(NpgsqlTransaction tx, IAuditWriter audit, AuditActor actor, string runId, ProcessRunPayload p,
        IReadOnlyList<ExceptionItem> items, CancellationToken ct)
    {
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in items.Chunk(Chunk))
        {
            var ids = await ReturnIds(tx, """
                INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, description, control, reason_code, severity, status, value_amount, value_currency,
                                        owner_user_id, due_at, blocking, source_file, source_row_id, delivery_id, resolution_kind, resolution_reason, finding_key, predecessor_exception_id)
                SELECT t.id, @r, @le, @d, t.title, t.descr, t.control, t.rc, t.sev, t.st, t.va, t.vc, t.own, t.due, t.blk, t.sf, t.sr, t.del, t.rk, t.rr, t.fk, t.pred
                FROM unnest(@id, @title, @descr, @control, @rc, @sev, @st, @va, @vc, @own, @due, @blk, @sf, @sr, @del, @rk, @rr, @fk, @pred)
                     WITH ORDINALITY AS t(id, title, descr, control, rc, sev, st, va, vc, own, due, blk, sf, sr, del, rk, rr, fk, pred, ord)
                ORDER BY t.ord
                ON CONFLICT (id) DO NOTHING RETURNING id
                """, ct,
                new NpgsqlParameter("r", runId), new NpgsqlParameter("le", p.LegalEntityId), new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = p.BusinessDate },
                Txt("id", chunk.Select(x => x.Draft.Id)), Txt("title", chunk.Select(x => x.Draft.Title)), Txt("descr", chunk.Select(x => x.Draft.Description)),
                Txt("control", chunk.Select(x => WireNames.ToLowerSnake(x.Draft.Finding.Area))), Txt("rc", chunk.Select(x => x.Draft.Finding.ReasonCode)),
                Txt("sev", chunk.Select(x => WireNames.ToUpperSnake(x.Draft.Finding.Severity))), Txt("st", chunk.Select(x => x.Decision?.Status ?? "OPEN")),
                Arr("va", NpgsqlDbType.Numeric, chunk.Select(x => x.Draft.Finding.Value?.Amount).ToArray()),
                Txt("vc", chunk.Select(x => x.Draft.Finding.Value?.Currency)), Txt("own", chunk.Select(x => x.Pred?.OwnerUserId)),
                Arr("due", NpgsqlDbType.TimestampTz, chunk.Select(x => x.Draft.DueAt.ToUniversalTime()).ToArray()),
                Arr("blk", NpgsqlDbType.Boolean, chunk.Select(x => x.Draft.Finding.Blocking).ToArray()),
                Txt("sf", chunk.Select(x => x.Draft.SourceFile)), Txt("sr", chunk.Select(x => x.Draft.SourceRowId)), Txt("del", chunk.Select(x => x.Draft.DeliveryId)),
                Txt("rk", chunk.Select(x => x.Decision?.ResolutionKind)), Txt("rr", chunk.Select(x => x.Decision?.ResolutionReason)),
                Txt("fk", chunk.Select(x => x.Draft.Finding.Key)), Txt("pred", chunk.Select(x => x.Pred?.Id)));
            foreach (var id in ids) inserted.Add(id);
        }
        var fresh = items.Where(x => inserted.Contains(x.Draft.Id)).ToList();

        foreach (var chunk in fresh.Chunk(Chunk))
        {
            await Run(tx, """
                INSERT INTO exception_rule_traces (exception_id, rule_id, rule_version, tolerance, expected_effect, description, calculation)
                SELECT t.id, t.rid, t.rv, t.tol, t.eff, t.descr, t.calc::jsonb
                FROM unnest(@id, @rid, @rv, @tol, @eff, @descr, @calc) WITH ORDINALITY AS t(id, rid, rv, tol, eff, descr, calc, ord) ORDER BY t.ord
                """, ct, Txt("id", chunk.Select(x => x.Draft.Id)), Txt("rid", chunk.Select(x => x.Draft.RuleId)), Txt("rv", chunk.Select(x => x.Draft.Finding.RuleVersion)),
                Txt("tol", chunk.Select(x => x.Draft.Tolerance)), Txt("eff", chunk.Select(x => x.Draft.ExpectedEffect)), Txt("descr", chunk.Select(x => x.Draft.Finding.Description)),
                Txt("calc", chunk.Select(x => x.Draft.CalculationJson)));
        }

        var links = fresh.SelectMany(x => x.Draft.Links.Select(l => (Id: x.Draft.Id, l.Table, l.DbId))).ToList();
        foreach (var chunk in links.Chunk(Chunk * 5))
        {
            await Run(tx, """
                INSERT INTO exception_links (exception_id, record_table, record_id)
                SELECT t.e, t.tbl, t.rid FROM unnest(@e, @t, @i) WITH ORDINALITY AS t(e, tbl, rid, ord) ORDER BY t.ord
                ON CONFLICT DO NOTHING
                """, ct, Txt("e", chunk.Select(x => x.Id)), Txt("t", chunk.Select(x => x.Table)), Arr("i", NpgsqlDbType.Bigint, chunk.Select(x => x.DbId).ToArray()));
        }

        // History carried from the version this one supersedes: the copies come first, then the "continues" note, then the "raised by" entry,
        // exactly the per-exception order of the row-by-row version.
        var carried = fresh.Where(x => x.Pred is not null).ToList();
        foreach (var chunk in carried.Chunk(Chunk))
        {
            var olds = Txt("o", chunk.Select(x => x.Pred!.Id));
            var news = Txt("n", chunk.Select(x => x.Draft.Id));
            await Run(tx, "INSERT INTO exception_notes (exception_id, author_user_id, body, created_at) SELECT m.new_id, n.author_user_id, n.body, n.created_at FROM unnest(@o, @n) WITH ORDINALITY AS m(old_id, new_id, ord) JOIN exception_notes n ON n.exception_id = m.old_id ORDER BY m.ord, n.id", ct, olds, news);
            await Run(tx, "INSERT INTO exception_evidence_refs (exception_id, kind, ref, created_at) SELECT m.new_id, n.kind, n.ref, n.created_at FROM unnest(@o, @n) WITH ORDINALITY AS m(old_id, new_id, ord) JOIN exception_evidence_refs n ON n.exception_id = m.old_id ORDER BY m.ord, n.id", ct,
                Txt("o", chunk.Select(x => x.Pred!.Id)), Txt("n", chunk.Select(x => x.Draft.Id)));
            await Run(tx, "INSERT INTO exception_timeline (exception_id, kind, actor_type, actor_id, summary, occurred_at) SELECT m.new_id, n.kind, n.actor_type, n.actor_id, n.summary, n.occurred_at FROM unnest(@o, @n) WITH ORDINALITY AS m(old_id, new_id, ord) JOIN exception_timeline n ON n.exception_id = m.old_id ORDER BY m.ord, n.id", ct,
                Txt("o", chunk.Select(x => x.Pred!.Id)), Txt("n", chunk.Select(x => x.Draft.Id)));
            await Run(tx, "INSERT INTO exception_timeline (exception_id, kind, actor_type, actor_id, summary) SELECT t.e, 'SYSTEM', 'SYSTEM', NULL, t.s FROM unnest(@e, @s) WITH ORDINALITY AS t(e, s, ord) ORDER BY t.ord", ct,
                Txt("e", chunk.Select(x => x.Draft.Id)), Txt("s", chunk.Select(x => x.Decision!.Note)));
        }
        foreach (var chunk in fresh.Chunk(Chunk))
        {
            await Run(tx, "INSERT INTO exception_timeline (exception_id, kind, actor_type, actor_id, summary) SELECT t.e, 'SYSTEM', 'SYSTEM', NULL, t.s FROM unnest(@e, @s) WITH ORDINALITY AS t(e, s, ord) ORDER BY t.ord", ct,
                Txt("e", chunk.Select(x => x.Draft.Id)),
                Txt("s", chunk.Select(x => $"Raised by the {WireNames.ToLowerSnake(x.Draft.Finding.Area)} control ({x.Draft.RuleId}, rule version {x.Draft.Finding.RuleVersion}).")));
        }

        var events = fresh.Select(x =>
        {
            var f = x.Draft.Finding;
            return new AuditEventInput("EXCEPTION_CREATED", actor, "EXCEPTION", x.Draft.Id,
                $"Exception {f.ReasonCode} raised by {WireNames.ToLowerSnake(f.Area)}", Reason: "Control finding " + f.ReasonCode, RunId: runId, RuleVersion: f.RuleVersion,
                AfterJson: new JsonObject
                {
                    ["findingKey"] = f.Key, ["severity"] = WireNames.ToUpperSnake(f.Severity), ["blocking"] = f.Blocking, ["status"] = x.Decision?.Status ?? "OPEN",
                    ["value"] = f.Value is { } v ? v.AmountString + " " + v.Currency : null, ["continuesException"] = x.Pred?.Id,
                }.ToJsonString());
        }).ToList();
        await audit.AppendManyAsync(tx, events, ct);
        return inserted;
    }

    public static async Task InsertGroupsAsync(NpgsqlTransaction tx, string runId, string legalEntityId, IReadOnlyList<MatchGroupDraft> groups, CancellationToken ct)
    {
        foreach (var chunk in groups.Chunk(Chunk))
        {
            var ids = await ReturnIds(tx, """
                INSERT INTO match_groups (id, run_id, legal_entity_id, status, difference_amount, currency, manual, rule_id, tolerance, rationale, cardinality)
                SELECT t.id, @r, @le, t.st, t.da, t.c, false, t.rule, t.tol, t.rat, t.card
                FROM unnest(@id, @st, @da, @c, @rule, @tol, @rat, @card) WITH ORDINALITY AS t(id, st, da, c, rule, tol, rat, card, ord) ORDER BY t.ord
                ON CONFLICT (id) DO NOTHING RETURNING id
                """, ct, new NpgsqlParameter("r", runId), new NpgsqlParameter("le", legalEntityId),
                Txt("id", chunk.Select(d => d.Id)), Txt("st", chunk.Select(d => WireNames.ToUpperSnake(d.Group.Status))),
                Arr("da", NpgsqlDbType.Numeric, chunk.Select(d => d.Group.ValueDifference?.Amount).ToArray()), Txt("c", chunk.Select(d => d.Group.ValueDifference?.Currency)),
                Txt("rule", chunk.Select(d => d.Group.RuleId)), Txt("tol", chunk.Select(d => d.ToleranceText)), Txt("rat", chunk.Select(d => d.Group.Rationale)),
                Txt("card", chunk.Select(d => d.Group.Cardinality)));
            var fresh = chunk.Where(d => ids.Contains(d.Id, StringComparer.Ordinal)).ToList(); // ids is at most 1000 long
            var cands = fresh.SelectMany(d => d.Candidates.Select(c => (Group: d.Id, C: c))).ToList();
            await Run(tx, """
                INSERT INTO match_candidates (match_group_id, kind, record_table, record_id, amount, currency, score, selected, side)
                SELECT t.g, t.k, t.tbl, t.rid, t.a, t.c, NULL, t.sel, t.side
                FROM unnest(@g, @k, @t, @i, @a, @c, @sel, @side) WITH ORDINALITY AS t(g, k, tbl, rid, a, c, sel, side, ord) ORDER BY t.ord
                """, ct, Txt("g", cands.Select(x => x.Group)), Txt("k", cands.Select(x => x.C.Kind)), Txt("t", cands.Select(x => x.C.Table)),
                Arr("i", NpgsqlDbType.Bigint, cands.Select(x => x.C.DbId).ToArray()), Arr("a", NpgsqlDbType.Numeric, cands.Select(x => x.C.Amount.Amount).ToArray()),
                Txt("c", cands.Select(x => x.C.Amount.Currency)), Arr("sel", NpgsqlDbType.Boolean, cands.Select(x => x.C.Selected).ToArray()), Txt("side", cands.Select(x => x.C.Side)));
            var ex = fresh.SelectMany(d => d.ExceptionIds.Select(e => (Group: d.Id, Exception: e))).ToList();
            if (ex.Count > 0)
            {
                await Run(tx, "INSERT INTO match_group_exceptions (match_group_id, exception_id) SELECT t.g, t.e FROM unnest(@g, @e) AS t(g, e) ON CONFLICT DO NOTHING", ct,
                    Txt("g", ex.Select(x => x.Group)), Txt("e", ex.Select(x => x.Exception)));
            }
        }
    }

    public static async Task InsertBalancesAsync(NpgsqlTransaction tx, string runId, string ruleVersion, IReadOnlyList<IndependentBalance> balances, CancellationToken ct)
    {
        static JsonObject M(Money m) => new() { ["amount"] = m.AmountString, ["currency"] = m.Currency };
        foreach (var chunk in balances.Chunk(Chunk / 2))
        {
            string Contributions(IndependentBalance b) => new JsonArray(b.Contributions.Select(c => (JsonNode)new JsonObject
            {
                ["source"] = c.Record.SourceName, ["sourceRowId"] = c.Record.SourceRowId, ["kind"] = c.Kind.ToString(), ["direction"] = c.Direction.ToString(),
                ["amount"] = M(c.Amount), ["independentEffect"] = M(c.IndependentEffect), ["recordedEffect"] = M(c.RecordedEffect), ["variance"] = M(c.Variance),
            }).ToArray()).ToJsonString();
            string Exclusions(IndependentBalance b) => new JsonArray(b.Exclusions.Select(x => (JsonNode)new JsonObject
                { ["source"] = x.Record.SourceName, ["sourceRowId"] = x.Record.SourceRowId, ["reason"] = x.Reason }).ToArray()).ToJsonString();
            await Run(tx, """
                INSERT INTO account_control_results (run_id, account_id, currency, opening, credits, debits, adjustments, independent, recorded, difference,
                                                     opening_supported, contributions, exclusions, rule_version)
                SELECT @r, t.a, t.c, t.op, t.cr, t.db, t.adj, t.ind, t.rec, t.diff, t.sup, t.con::jsonb, t.exc::jsonb, @rv
                FROM unnest(@a, @c, @op, @cr, @db, @adj, @ind, @rec, @diff, @sup, @con, @exc)
                     WITH ORDINALITY AS t(a, c, op, cr, db, adj, ind, rec, diff, sup, con, exc, ord) ORDER BY t.ord
                ON CONFLICT DO NOTHING
                """, ct, new NpgsqlParameter("r", runId), new NpgsqlParameter("rv", ruleVersion),
                Txt("a", chunk.Select(b => b.AccountId)), Txt("c", chunk.Select(b => b.Currency)),
                Arr("op", NpgsqlDbType.Numeric, chunk.Select(b => b.Opening?.Amount).ToArray()), Arr("cr", NpgsqlDbType.Numeric, chunk.Select(b => (decimal?)b.Credits.Amount).ToArray()),
                Arr("db", NpgsqlDbType.Numeric, chunk.Select(b => (decimal?)b.Debits.Amount).ToArray()), Arr("adj", NpgsqlDbType.Numeric, chunk.Select(b => (decimal?)b.Adjustments.Amount).ToArray()),
                Arr("ind", NpgsqlDbType.Numeric, chunk.Select(b => b.Independent?.Amount).ToArray()), Arr("rec", NpgsqlDbType.Numeric, chunk.Select(b => b.Recorded?.Amount).ToArray()),
                Arr("diff", NpgsqlDbType.Numeric, chunk.Select(b => b.Difference?.Amount).ToArray()), Arr("sup", NpgsqlDbType.Boolean, chunk.Select(b => b.OpeningSupported).ToArray()),
                Txt("con", chunk.Select(Contributions)), Txt("exc", chunk.Select(Exclusions)));
        }
    }
}
