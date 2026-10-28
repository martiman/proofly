namespace Proofly.Data.Tests.Matching;

/// <summary>Neutral multi-currency (GBP, EUR, JPY) fixture: run R-M1 (LE-M1) has five groups, R-M2 (LE-M2) one, R-EMPTY none.</summary>
internal static class MatchingSeed
{
    public const string Sql = """
INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-M1','One','GBP'),('LE-M2','Two','EUR');
INSERT INTO users (id, name, email) VALUES ('u-prop','Pat Proposer','pat@x.test'),('u-appr','Alex Approver','alex@x.test');
INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state) VALUES
  ('R-M1','LE-M1','2026-03-02',1,'rs-1','REVIEW_REQUIRED'),('R-M2','LE-M2','2026-03-02',1,'rs-1','REVIEW_REQUIRED'),('R-EMPTY','LE-M1','2026-03-03',1,'rs-1','REVIEW_REQUIRED');
INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, state) VALUES
  ('D-M1','R-M1','LE-M1','payments','2026-03-02','1','p.csv',repeat('a',64),'RECEIVED');
INSERT INTO transfer_instructions (delivery_id, source_row_id, instruction_id, client_account_id, legal_entity_id, transaction_type, amount, currency, instruction_timestamp, source_status, external_reference) VALUES
  ('D-M1','row-1','I-1','A-1','LE-M1','TRANSFER',100.50,'GBP','2026-03-02T09:00:00Z','SETTLED','REF-1'),
  ('D-M1','row-2','I-2','A-1','LE-M1','TRANSFER',250.000000,'EUR','2026-03-02T10:00:00Z','SETTLED',NULL),
  ('D-M1','row-3','I-3','A-1','LE-M1','TRANSFER',50000,'JPY','2026-03-02T11:00:00Z','SETTLED','REF-3'),
  ('D-M1','row-4','I-4','A-1','LE-M1','TRANSFER',75,'EUR','2026-03-02T12:00:00Z','SETTLED','REF-4'),
  ('D-M1','row-5','I-5','A-1','LE-M1','TRANSFER',10.25,'GBP','2026-03-02T13:00:00Z','SETTLED','REF-5');
INSERT INTO bank_transactions (delivery_id, external_transaction_id, external_account_id, booking_timestamp, value_date, amount, currency, debit_credit_indicator, bank_reference, transaction_status) VALUES
  ('D-M1','B-1','X-1','2026-03-02T09:05:00Z','2026-03-02',100.50,'GBP','CREDIT','REF-1','BOOKED'),
  ('D-M1','B-3','X-1','2026-03-02T11:05:00Z','2026-03-02',49997.5,'JPY','CREDIT',NULL,'BOOKED');
INSERT INTO ledger_entries (delivery_id, ledger_entry_id, account_id, legal_entity_id, booking_timestamp, value_date, debit_credit_indicator, amount, currency, transaction_reference, entry_type, source_status) VALUES
  ('D-M1','L-4','A-1','LE-M1','2026-03-02T12:30:00Z','2026-03-02','CREDIT',74.99,'EUR','REF-4','POSTING','POSTED');
INSERT INTO config_rules (id, kind, name, config_version, payload) VALUES ('rule-ref','rule','Reference match','2.1','{"tolerance":"0.01 GBP"}');
INSERT INTO match_groups (id, run_id, legal_entity_id, status, difference_amount, currency, manual, rule_id, version) VALUES
  ('G-001','R-M1','LE-M1','MATCHED',0,'GBP',false,'rule-ref',1),
  ('G-002','R-M1','LE-M1','UNMATCHED',NULL,NULL,false,NULL,1),
  ('G-003','R-M1','LE-M1','VALUE_MISMATCH',2.500000,'JPY',false,'rule-ref',3),
  ('G-004','R-M1','LE-M1','PENDING_APPROVAL',0.01,'EUR',true,NULL,1),
  ('G-005','R-M1','LE-M1','UNMATCHED',NULL,NULL,false,NULL,1),
  ('G-006','R-M2','LE-M2','UNMATCHED',NULL,NULL,false,NULL,1);
INSERT INTO match_candidates (match_group_id, kind, record_table, record_id, amount, currency, score, selected)
SELECT 'G-001','BANK_TRANSFER','transfer_instructions',id,100.50,'GBP',1,true FROM transfer_instructions WHERE source_row_id='row-1' UNION ALL
SELECT 'G-001','BANK_EVIDENCE','bank_transactions',id,100.50,'GBP',1,true FROM bank_transactions WHERE external_transaction_id='B-1' UNION ALL
SELECT 'G-002','BANK_TRANSFER','transfer_instructions',id,250,'EUR',NULL,false FROM transfer_instructions WHERE source_row_id='row-2' UNION ALL
SELECT 'G-003','BANK_TRANSFER','transfer_instructions',id,50000,'JPY',0.9,true FROM transfer_instructions WHERE source_row_id='row-3' UNION ALL
SELECT 'G-003','BANK_EVIDENCE','bank_transactions',id,49997.5,'JPY',0.9,true FROM bank_transactions WHERE external_transaction_id='B-3' UNION ALL
SELECT 'G-004','BANK_TRANSFER','transfer_instructions',id,75,'EUR',NULL,true FROM transfer_instructions WHERE source_row_id='row-4' UNION ALL
SELECT 'G-004','LEDGER_ENTRY','ledger_entries',id,74.99,'EUR',NULL,true FROM ledger_entries WHERE ledger_entry_id='L-4' UNION ALL
SELECT 'G-005','BANK_TRANSFER','transfer_instructions',id,10.25,'GBP',NULL,false FROM transfer_instructions WHERE source_row_id='row-5';
INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, due_at, delivery_id, source_row_id) VALUES
  ('EX-1','R-M1','LE-M1','2026-03-02','Unmatched','matching','UNMATCHED','HIGH',now(),'D-M1','row-2'),
  ('EX-3','R-M1','LE-M1','2026-03-02','Value','matching','VALUE_MISMATCH','MEDIUM',now(),'D-M1','B-3'),
  ('EX-X','R-M1','LE-M1','2026-03-02','Elsewhere','matching','UNMATCHED','LOW',now(),'D-M1','no-such-row');
INSERT INTO approvals (id, object_type, object_id, run_id, proposal_version, proposer_user_id, required_role, status, evidence_refs, policy_version, proposed_at)
  VALUES ('AP-1','MANUAL_MATCH','G-004','R-M1',1,'u-prop','APPROVER','PENDING','["ev-1"]','pol-1','2026-03-02T14:00:00Z');
""";
}
