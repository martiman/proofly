-- Durable job queue (outbox). Claimed with SELECT ... FOR UPDATE SKIP LOCKED; see backend/README.md.
-- States: QUEUED (ready), RUNNING (leased), FAILED (last attempt failed, retry at run_after),
-- SUCCEEDED, DEAD (attempts exhausted or permanent failure).
CREATE TABLE jobs (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  type text NOT NULL,
  payload jsonb NOT NULL DEFAULT '{}'::jsonb,
  state text NOT NULL DEFAULT 'QUEUED' CHECK (state IN ('QUEUED','RUNNING','SUCCEEDED','FAILED','DEAD')),
  attempts int NOT NULL DEFAULT 0 CHECK (attempts >= 0),
  max_attempts int NOT NULL DEFAULT 5 CHECK (max_attempts >= 1),
  run_after timestamptz NOT NULL DEFAULT now(),
  locked_by text,
  locked_until timestamptz,
  last_error text,
  dedupe_key text,
  run_id text,
  delivery_id text,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT uq_jobs_dedupe UNIQUE (dedupe_key)
);
CREATE INDEX ix_jobs_ready ON jobs (run_after, id) WHERE state IN ('QUEUED','FAILED');
CREATE INDEX ix_jobs_lease ON jobs (locked_until) WHERE state = 'RUNNING';
CREATE INDEX ix_jobs_run ON jobs (run_id) WHERE run_id IS NOT NULL;
