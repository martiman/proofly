-- Run orchestration (#39). Idempotent, additive.

-- Determinism: byte-identical result hash over the canonical control outcome, and the exact input set the run used.
ALTER TABLE runs ADD COLUMN IF NOT EXISTS result_hash char(64);
ALTER TABLE runs ADD COLUMN IF NOT EXISTS input_manifest jsonb;   -- [{"deliveryId","sourceName","fingerprint","state"}], ordered
ALTER TABLE runs ADD COLUMN IF NOT EXISTS processed_at timestamptz;
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_runs_result_hash' AND conrelid = 'runs'::regclass) THEN
    ALTER TABLE runs ADD CONSTRAINT ck_runs_result_hash CHECK (result_hash IS NULL OR result_hash ~ '^[0-9a-f]{64}$');
  END IF;
END $$;

-- Exception identity across run versions: the control's stable finding key, and the exception it continues.
ALTER TABLE exceptions ADD COLUMN IF NOT EXISTS finding_key text;
ALTER TABLE exceptions ADD COLUMN IF NOT EXISTS predecessor_exception_id text REFERENCES exceptions(id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_exceptions_run_finding ON exceptions (run_id, finding_key) WHERE finding_key IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_exceptions_entity_finding ON exceptions (legal_entity_id, business_date, finding_key) WHERE finding_key IS NOT NULL;

-- Per-control outcome of a run (never edited after the run is processed).
CREATE TABLE IF NOT EXISTS run_control_results (
  run_id text NOT NULL REFERENCES runs(id),
  control text NOT NULL CHECK (control IN ('data_completeness','matching','independent_ledger','transaction_lifecycle','safeguarding')),
  state text NOT NULL CHECK (state IN ('PASSED','REVIEW_REQUIRED','FAILED','BLOCKED','QUALIFIED')),
  rule_version text NOT NULL,
  record_count integer NOT NULL CHECK (record_count >= 0),
  finding_count integer NOT NULL CHECK (finding_count >= 0),
  blocking_finding_count integer NOT NULL CHECK (blocking_finding_count >= 0),
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (run_id, control)
);
