-- Performance spike (#48): indexes the 100,000-transaction measurements showed to be missing. Additive and idempotent.

-- The transaction and account views attach exceptions to a record by (run, delivery, source row id). Without this index each of the
-- ~270,000 records of a 100k-transaction run scanned every exception of the run (30 s for one page at 10% of the target volume).
CREATE INDEX IF NOT EXISTS ix_exceptions_run_delivery_row ON exceptions (run_id, delivery_id, source_row_id);

-- Exceptions raised on an account itself (source_row_id = account id) for the account views.
CREATE INDEX IF NOT EXISTS ix_exceptions_run_source_row ON exceptions (run_id, source_row_id);

-- The default exception list order (-severity,dueAt,id) sorted all exceptions of the run for every page. The expression is the one in
-- ExceptionSortSpec ("severity" key, descending), so the first page and keyset pages become an index range scan with LIMIT.
CREATE INDEX IF NOT EXISTS ix_exceptions_run_default_order ON exceptions
  (run_id, (CASE severity WHEN 'CRITICAL' THEN 4 WHEN 'HIGH' THEN 3 WHEN 'MEDIUM' THEN 2 ELSE 1 END) DESC, due_at, (id COLLATE "C"));
