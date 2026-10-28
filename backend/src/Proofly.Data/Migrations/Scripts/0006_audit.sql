-- Append-only. Hash chain (previous_hash/hash) is written by the audit writer (#16).
CREATE TABLE audit_events (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  occurred_at timestamptz NOT NULL DEFAULT now(),
  event_type text NOT NULL,
  actor_type text NOT NULL CHECK (actor_type IN ('USER','SYSTEM','SERVICE_ACCOUNT')),
  actor_id text,
  subject_kind text NOT NULL,
  subject_id text NOT NULL,
  run_id text,
  reason text,
  before jsonb,
  after jsonb,
  request_id text,
  previous_hash char(64),
  hash char(64) NOT NULL CHECK (hash ~ '^[0-9a-f]{64}$')
);
CREATE INDEX ix_audit_subject ON audit_events (subject_kind, subject_id, occurred_at);
CREATE INDEX ix_audit_run ON audit_events (run_id, occurred_at);
CREATE INDEX ix_audit_occurred ON audit_events (occurred_at);

CREATE FUNCTION proofly_audit_immutable() RETURNS trigger AS $$
BEGIN
  RAISE EXCEPTION 'audit_events is append-only (% not allowed)', TG_OP USING ERRCODE = 'restrict_violation';
END $$ LANGUAGE plpgsql;
CREATE TRIGGER trg_audit_no_update_delete BEFORE UPDATE OR DELETE ON audit_events
  FOR EACH ROW EXECUTE FUNCTION proofly_audit_immutable();
CREATE TRIGGER trg_audit_no_truncate BEFORE TRUNCATE ON audit_events
  FOR EACH STATEMENT EXECUTE FUNCTION proofly_audit_immutable();
