namespace Proofly.Data.Tests.Exceptions;

internal static class ExceptionSeed
{
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Neutral multi-currency data: run R1 (LE-1) has 7 exceptions, run R2 (LE-2) has 1.</summary>
    public const string Sql = """
        INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-1','One','GBP'),('LE-2','Two','EUR');
        INSERT INTO users (id, name, email) VALUES ('u1','Ada','ada@x.test'),('u2','Bob','bob@x.test');
        INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state)
          VALUES ('R1','LE-1','2026-10-27',1,'rs-1','REVIEW_REQUIRED'),('R2','LE-2','2026-10-27',1,'rs-1','REVIEW_REQUIRED'),
                 ('R3','LE-1','2026-10-26',1,'rs-1','SUPERSEDED');
        INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status,
                                value_amount, value_currency, owner_user_id, due_at, blocking, source_file, source_row_id, created_at) VALUES
          ('E-001','R1','LE-1','2026-10-27','Transfer without bank evidence','matching','NO_BANK_EVIDENCE','CRITICAL','OPEN',-100.50,'GBP','u1','2026-10-27T09:00Z',true,'transfers.csv','row-1','2026-10-27T08:00Z'),
          ('E-002','R1','LE-1','2026-10-27','Ledger gap','independent_ledger','LEDGER_GAP','HIGH','OPEN',2000,'EUR',NULL,'2026-10-29T09:00Z',false,'ledger.csv','row-2','2026-10-26T08:00Z'),
          ('E-003','R1','LE-1','2026-10-26','Card settlement short','matching','SETTLEMENT_SHORT','HIGH','IN_INVESTIGATION',50000,'JPY','u2','2026-10-28T09:00Z',false,'cards.csv','row-3','2026-10-20T08:00Z'),
          ('E-004','R1','LE-1','2026-10-27','Small mismatch','matching','NO_BANK_EVIDENCE','MEDIUM','OPEN',30.25,'GBP',NULL,'2026-11-05T09:00Z',false,'transfers.csv','row-4','2026-10-27T10:00Z'),
          ('E-005','R1','LE-1','2026-10-25','Late delivery','data_completeness','LATE_DELIVERY','LOW','WAITING_FOR_DATA',NULL,NULL,'u1','2026-10-30T09:00Z',false,NULL,NULL,'2026-10-25T08:00Z'),
          ('E-006','R1','LE-1','2026-10-27','Eur mismatch','matching','NO_BANK_EVIDENCE','CRITICAL','OPEN',-1500.75,'EUR',NULL,'2026-10-28T09:00Z',true,'transfers.csv','row-6','2026-10-27T11:00Z'),
          ('E-007','R1','LE-1','2026-10-27','Closed item','matching','NO_BANK_EVIDENCE','LOW','RESOLVED',10,'GBP',NULL,'2026-10-20T09:00Z',false,'transfers.csv','row-7','2026-10-27T12:00Z'),
          ('X-001','R2','LE-2','2026-10-27','Other entity','matching','NO_BANK_EVIDENCE','HIGH','OPEN',5,'EUR',NULL,'2026-10-29T09:00Z',false,'x.csv','row-1','2026-10-27T08:00Z'),
          ('S-001','R3','LE-1','2026-10-26','Old run exception','matching','NO_BANK_EVIDENCE','HIGH','OPEN',1,'GBP',NULL,'2026-10-29T09:00Z',false,'x.csv','row-9','2026-10-26T08:00Z');
        """;
}
