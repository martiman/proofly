namespace Proofly.Data.Tests.Sources;

/// <summary>Neutral multi-currency fixture (GBP, EUR, JPY). Independent of examples/synthetic-data.</summary>
internal static class SourceSeed
{
    public const string Sql = """
        INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-A','Entity A','GBP'), ('LE-B','Entity B','EUR');
        INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state)
          VALUES ('RUN-A','LE-A','2026-03-02',1,'r1','REVIEW_REQUIRED'), ('RUN-B','LE-B','2026-03-02',1,'r1','REVIEW_REQUIRED');

        INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256,
            declared_record_count, expected_by, received_at, state, accepted_record_count, rejected_record_count, total_row_count, ingested_at, sequence_number)
        VALUES
          ('D1','RUN-A','LE-A','transfer_instructions','2026-03-02','1.0','trf.csv', repeat('a',64), 5, '2026-03-03T06:00:00Z','2026-03-03T05:00:00Z','QUALIFIED',3,2,5,now(),7),
          ('D2','RUN-A','LE-A','banking_transactions','2026-03-02','1.0','bank.csv', repeat('b',64), 1, NULL,'2026-03-03T05:30:00Z','REJECTED',0,0,NULL,NULL,NULL),
          ('D3','RUN-B','LE-B','transfer_instructions','2026-03-02','1.0','trf-b.csv', repeat('c',64), 1, '2026-03-03T06:00:00Z','2026-03-03T05:10:00Z','QUALIFIED',1,0,1,now(),NULL);
        UPDATE source_deliveries SET supersedes_delivery_id = 'D1' WHERE id = 'D2';

        INSERT INTO delivery_control_totals (delivery_id, currency, declared_amount, actual_amount) VALUES
          ('D1','GBP',150.50,150.50), ('D1','JPY',3000,3000), ('D1','EUR',20.123456,NULL), ('D3','EUR',9.99,9.99);

        INSERT INTO transfer_instructions (delivery_id, row_number, source_row_id, instruction_id, client_account_id, legal_entity_id, transaction_type, amount, currency, instruction_timestamp, source_status)
        VALUES ('D1',1,'s1','i1','c1','LE-A','PAYMENT',100.00,'GBP','2026-03-02T10:00:00Z','SETTLED'),
               ('D1',2,'s2','i2','c1','LE-A','PAYMENT',50.50,'GBP','2026-03-02T11:00:00Z','SETTLED'),
               ('D1',3,'s3','i3','c2','LE-A','PAYMENT',3000,'JPY','2026-03-02T12:00:00Z','SETTLED'),
               ('D3',1,'b1','j1','c9','LE-B','PAYMENT',9.99,'EUR','2026-03-02T12:00:00Z','SETTLED');

        INSERT INTO delivery_rejections (delivery_id, source_row_id, reason_code, reason, row_number, field, raw_excerpt) VALUES
          ('D1','s4','INVALID_VALUE','amount is not a decimal',4,'amount','s4,i4,c1,LE-A,PAYMENT,abc,GBP'),
          ('D1',NULL,'REQUIRED_FIELD_MISSING','client_account_id is empty',5,'client_account_id','s5,i5,,LE-A'),
          ('D2',NULL,'DUPLICATE_DELIVERY','Same content already delivered as D1.',NULL,NULL,NULL);

        INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, due_at, delivery_id, source_row_id)
        VALUES ('F-1','RUN-A','LE-A','2026-03-02','Row issue','data_completeness','X','LOW','2026-03-04T00:00:00Z','D1','s1');
        """;
}
