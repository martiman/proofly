using System.Net;
using System.Text.Json;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Matching;

/// <summary>
/// getMatchingWorkbench always shows pending manual proposals (PENDING_APPROVAL): a pending group that holds the focused record is the focus, the others follow,
/// and listMatchGroups puts pending and open groups before the matched ones.
/// </summary>
public sealed class WorkbenchPendingTests(MatchingFixture f) : IClassFixture<MatchingFixture>
{
    private HttpClient Viewer => f.Factory.As(f.Factory.TokenFor(Role.Viewer));
    private static async Task<JsonElement> Json(HttpResponseMessage r) { Assert.Equal(HttpStatusCode.OK, r.StatusCode); return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement; }
    private static string[] Ids(JsonElement groups) => groups.EnumerateArray().Select(g => g.GetProperty("id").GetString()!).ToArray();

    private async Task AddPendingManualGroup()
    {
        // G-007: a manual proposal over the unmatched transfer of row-2 (also in the UNMATCHED group G-002) and the bank booking B-9.
        await using var c = f.Db.CreateCommand("""
            INSERT INTO match_groups (id, run_id, legal_entity_id, status, difference_amount, currency, manual, version) VALUES ('G-007','R-M1','LE-M1','PENDING_APPROVAL',0,'EUR',true,1) ON CONFLICT DO NOTHING;
            INSERT INTO match_candidates (match_group_id, kind, record_table, record_id, amount, currency, score, selected)
            SELECT 'G-007','BANK_TRANSFER','transfer_instructions',id,250,'EUR',NULL::numeric,true FROM transfer_instructions WHERE source_row_id='row-2' AND NOT EXISTS (SELECT 1 FROM match_candidates WHERE match_group_id='G-007') UNION ALL
            SELECT 'G-007','BANK_EVIDENCE','bank_transactions',id,250,'EUR',NULL::numeric,true FROM bank_transactions WHERE external_transaction_id='B-9' AND NOT EXISTS (SELECT 1 FROM match_candidates WHERE match_group_id='G-007');
            INSERT INTO approvals (id, object_type, object_id, run_id, proposal_version, proposer_user_id, required_role, status, evidence_refs, policy_version, proposed_at)
              VALUES ('AP-7','MANUAL_MATCH','G-007','R-M1',1,'u-prop','APPROVER','PENDING','["ev-1"]','pol-1','2026-03-02T15:00:00Z') ON CONFLICT DO NOTHING;
            """);
        await c.ExecuteNonQueryAsync();
    }

    private async Task<string> TxId(string table, string prefix, string whereSql)
    {
        await using var c = f.Db.CreateCommand($"SELECT id FROM {table} WHERE {whereSql}");
        return prefix + "-" + (long)(await c.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Pending_manual_group_is_the_focus_for_its_records_and_listed_otherwise()
    {
        await AddPendingManualGroup();

        // By transaction: the record is in the unmatched group G-002 and in the pending proposal G-007; the proposal wins, the other pending group follows.
        var tx = await TxId("transfer_instructions", "TRF", "source_row_id='row-2'");
        var byTx = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench?transactionId=" + tx));
        Assert.Equal(tx, byTx.GetProperty("focusTransactionId").GetString());
        Assert.Equal(["G-007", "G-004"], Ids(byTx.GetProperty("groups")));
        Assert.Equal("PENDING_APPROVAL", byTx.GetProperty("groups")[0].GetProperty("status").GetString());
        Assert.Equal("AP-7", byTx.GetProperty("groups")[0].GetProperty("approval").GetProperty("id").GetString());

        // By exception (EX-1 belongs to row-2).
        var byEx = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench?exceptionId=EX-1"));
        Assert.Equal("G-007", Ids(byEx.GetProperty("groups"))[0]);

        // A record outside the proposals: its own group is the focus and the pending groups are still listed after it.
        var other = await TxId("transfer_instructions", "TRF", "source_row_id='row-5'");
        var byOther = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench?transactionId=" + other));
        Assert.Equal(["G-005", "G-004", "G-007"], Ids(byOther.GetProperty("groups")));

        // Default focus: the first pending proposal (id order), listed once.
        var def = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench"));
        Assert.Equal(["G-004", "G-007"], Ids(def.GetProperty("groups")));
    }

    [Fact]
    public async Task Group_list_puts_pending_and_open_groups_before_matched_ones_and_pages_in_that_order()
    {
        await AddPendingManualGroup();
        var expected = new[] { "G-004", "G-007", "G-002", "G-005", "G-003", "G-001" };
        var all = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/match-groups"));
        Assert.Equal(expected, Ids(all.GetProperty("items")));

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/match-groups?limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))));
            seen.AddRange(Ids(page.GetProperty("items")));
            cursor = page.GetProperty("page").GetProperty("nextCursor").GetString();
        } while (cursor is not null);
        Assert.Equal(expected, seen);

        // An explicit status filter still works and keeps id order within the status.
        var pending = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/match-groups?status=PENDING_APPROVAL"));
        Assert.Equal(["G-004", "G-007"], Ids(pending.GetProperty("items")));
    }
}
