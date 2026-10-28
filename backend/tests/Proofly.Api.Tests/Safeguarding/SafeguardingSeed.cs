using Npgsql;

namespace Proofly.Api.Tests.Safeguarding;

/// <summary>Neutral multi-currency (GBP, EUR, JPY) seed for the safeguarding read model.</summary>
internal static class SafeguardingSeed
{
    public const string Sql = """
INSERT INTO legal_entities (id,name,base_currency) VALUES ('LE-1','Entity One','GBP'),('LE-2','Entity Two','EUR');
INSERT INTO users (id,name,email) VALUES ('u-ann','Ann','ann@x.test'),('u-bob','Bob','bob@x.test');
INSERT INTO roles (id,name) VALUES ('APPROVER','Approver') ON CONFLICT DO NOTHING;
INSERT INTO runs (id,legal_entity_id,business_date,run_version,ruleset_version,state,sign_off_state) VALUES
 ('R-PREV','LE-1','2026-10-26',1,'rs-1','SIGNED_OFF','SIGNED_OFF'),
 ('R-1','LE-1','2026-10-27',1,'rs-1','REVIEW_REQUIRED','APPROVAL_PENDING'),
 ('R-2','LE-2','2026-10-27',1,'rs-1','REVIEW_REQUIRED','PREPARATION_INCOMPLETE'),
 ('R-EMPTY','LE-1','2026-10-28',1,'rs-1','AWAITING_EVIDENCE','PREPARATION_INCOMPLETE');
INSERT INTO source_deliveries (id,run_id,legal_entity_id,source_name,business_date,schema_version,file_name,fingerprint_sha256,state) VALUES
 ('D-1','R-1','LE-1','safeguarding_balances','2026-10-27','1','sg.csv',repeat('a',64),'RECEIVED');
INSERT INTO safeguarding_balances (delivery_id,source_row_id,external_account_id,legal_entity_id,balance_timestamp,balance_type,balance_amount,currency,evidence_reference) VALUES
 ('D-1','row-1','EXT-GBP-1','LE-1','2026-10-27T20:00:00Z','CLOSING',900.500000,'GBP','stmt-1'),
 ('D-1','row-2','EXT-GBP-2','LE-1','2026-10-26T01:00:00Z','CLOSING',50.000000,'GBP',NULL),
 ('D-1','row-3','EXT-EUR-1','LE-1','2026-10-27T20:00:00Z','CLOSING',600.000000,'EUR','stmt-3'),
 ('D-1','row-4','EXT-JPY-1','LE-1','2026-10-27T20:00:00Z','CLOSING',100000.000000,'JPY',NULL),
 ('D-1','row-5','EXT-GBP-3','LE-1','2026-10-27T20:00:00Z','CLOSING',1.000000,'GBP',NULL);
INSERT INTO safeguarding_positions (run_id,legal_entity_id,currency,obligation_amount,eligible_amount,difference_amount,state,detail) VALUES
 ('R-PREV','LE-1','GBP',1000,950,-50,'SHORTFALL','{"approvedAdjustments":"0"}'),
 ('R-PREV','LE-1','EUR',500,500,0,'COVERED','{"approvedAdjustments":"0"}'),
 ('R-1','LE-1','GBP',1000.25,900.5,-99.75,'SHORTFALL',
  '{"approvedAdjustments":"0","qualifications":[],"obligationLines":[{"accountId":"CA-1","amount":"600.25","movementType":"CLIENT_FUNDS"},{"accountId":"CA-2","amount":"400","movementType":"CLIENT_FUNDS"}],"balanceLines":[{"sourceRowId":"row-1","included":true,"reason":"ELIGIBLE","findingIds":[]},{"sourceRowId":"row-2","included":false,"reason":"STALE_BALANCE","findingIds":["F-1"]}]}'),
 ('R-1','LE-1','EUR',500,600,100,'SURPLUS',
  '{"approvedAdjustments":"0","qualifications":["STALE_INPUT"],"obligationLines":[{"accountId":"CA-3","amount":"500"}],"balanceLines":[{"sourceRowId":"row-3","included":true,"reason":"ELIGIBLE","findingIds":[]}]}'),
 ('R-1','LE-1','JPY',0,100000,100000,'NO_OBLIGATION','{}'),
 ('R-2','LE-2','EUR',10,10,0,'COVERED','{}');
INSERT INTO exceptions (id,run_id,legal_entity_id,business_date,title,control,reason_code,severity,value_amount,value_currency,due_at) VALUES
 ('EX-2','R-1','LE-1','2026-10-27','Shortfall','safeguarding','SHORTFALL','HIGH',99.75,'GBP',now()),
 ('EX-1','R-1','LE-1','2026-10-27','Stale','safeguarding','STALE','HIGH',50,'GBP',now()),
 ('EX-X','R-1','LE-1','2026-10-27','Other','matching','X','LOW',1,'GBP',now());
INSERT INTO safeguarding_adjustments (id,run_id,currency,amount,reason,status,proposed_by_user_id,evidence_refs,expires_on,created_at) VALUES
 ('ADJ-1','R-1','GBP',25.5,'Timing difference','APPROVED','u-ann','["ev-1","ev-2"]','2026-11-30','2026-10-27T08:00:00Z'),
 ('ADJ-2','R-1','EUR',-10,'Correction','PROPOSED','u-ann','["ev-3"]',NULL,'2026-10-27T09:00:00Z'),
 ('ADJ-3','R-1','JPY',1000,'Rejected one','REJECTED','u-bob','["ev-9"]',NULL,'2026-10-27T10:00:00Z'),
 ('ADJ-X','R-2','EUR',1,'Other entity','PROPOSED','u-bob','["ev-9"]',NULL,'2026-10-27T11:00:00Z');
INSERT INTO approvals (id,object_type,object_id,run_id,proposal_version,proposer_user_id,approver_user_id,required_role,status,decision,rationale,evidence_refs,policy_version,decided_at) VALUES
 ('AP-1','SAFEGUARDING_ADJUSTMENT','ADJ-1','R-1',1,'u-ann','u-bob','APPROVER','APPROVED','APPROVE','ok','["ev-1","ev-2"]','pol-1',now()),
 ('AP-2','SAFEGUARDING_ADJUSTMENT','ADJ-2','R-1',1,'u-ann',NULL,'APPROVER','PENDING',NULL,NULL,'["ev-3"]','pol-1',NULL);
INSERT INTO audit_events (event_type,actor_type,actor_id,subject_kind,subject_id,run_id,reason,chain_id,chain_seq,hash) VALUES
 ('ADJUSTMENT_PROPOSED','USER','u-ann','ADJUSTMENT','ADJ-1','R-1','Timing difference','LE-1',1,repeat('b',64)),
 ('ADJUSTMENT_APPROVED','USER','u-bob','ADJUSTMENT','ADJ-1','R-1','ok','LE-1',2,repeat('c',64));
""";

    public static async Task ApplyAsync(NpgsqlDataSource db)
    {
        await using var c = db.CreateCommand(Sql);
        await c.ExecuteNonQueryAsync();
    }
}
