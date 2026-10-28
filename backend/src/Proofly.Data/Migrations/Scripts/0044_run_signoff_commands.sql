-- Run sign-off commands (#44). Idempotent, additive.
-- One run_signoffs row per submission cycle; the latest row is the current one. Hashes pin what the approver attests to.
ALTER TABLE run_signoffs ADD COLUMN IF NOT EXISTS required_approver_user_id text REFERENCES users(id);
ALTER TABLE run_signoffs ADD COLUMN IF NOT EXISTS submitted_at timestamptz;
ALTER TABLE run_signoffs ADD COLUMN IF NOT EXISTS submission_note text;
ALTER TABLE run_signoffs ADD COLUMN IF NOT EXISTS returned_reason text;
ALTER TABLE run_signoffs ADD COLUMN IF NOT EXISTS ruleset_version text;
ALTER TABLE run_signoffs ADD COLUMN IF NOT EXISTS submitted_result_hash char(64);
ALTER TABLE run_signoffs ADD COLUMN IF NOT EXISTS submitted_open_set_hash char(64);
ALTER TABLE run_signoffs ADD COLUMN IF NOT EXISTS submitted_exception_state_hash char(64);
ALTER TABLE run_signoffs ADD COLUMN IF NOT EXISTS signed_result_hash char(64);
ALTER TABLE run_signoffs ADD COLUMN IF NOT EXISTS signed_exception_state_hash char(64);
-- SHA-256 over attestation text hash + result hash + exception-state hash + ruleset version, fixed at approval.
ALTER TABLE run_signoffs ADD COLUMN IF NOT EXISTS attestation_hash char(64);
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_run_signoffs_hashes' AND conrelid = 'run_signoffs'::regclass) THEN
    ALTER TABLE run_signoffs ADD CONSTRAINT ck_run_signoffs_hashes CHECK (
      (submitted_result_hash IS NULL OR submitted_result_hash ~ '^[0-9a-f]{64}$')
      AND (submitted_open_set_hash IS NULL OR submitted_open_set_hash ~ '^[0-9a-f]{64}$')
      AND (submitted_exception_state_hash IS NULL OR submitted_exception_state_hash ~ '^[0-9a-f]{64}$')
      AND (signed_result_hash IS NULL OR signed_result_hash ~ '^[0-9a-f]{64}$')
      AND (signed_exception_state_hash IS NULL OR signed_exception_state_hash ~ '^[0-9a-f]{64}$')
      AND (attestation_hash IS NULL OR attestation_hash ~ '^[0-9a-f]{64}$'));
  END IF;
END $$;
