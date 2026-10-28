-- Idempotency store (PostgreSQL): the replayable response and a lease so a crashed in-flight request frees its key.
-- user_id holds the idempotency scope (the caller id). Columns are additive; the table is new in practice (nothing wrote to it before).
ALTER TABLE idempotency_keys ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz;
ALTER TABLE idempotency_keys ADD COLUMN IF NOT EXISTS content_type text;
ALTER TABLE idempotency_keys ADD COLUMN IF NOT EXISTS response_headers jsonb;
ALTER TABLE idempotency_keys ADD COLUMN IF NOT EXISTS response_bytes bytea;
ALTER TABLE idempotency_keys ADD COLUMN IF NOT EXISTS request_id text;
UPDATE idempotency_keys SET lease_expires_at = created_at + interval '5 minutes' WHERE lease_expires_at IS NULL;
ALTER TABLE idempotency_keys ALTER COLUMN lease_expires_at SET NOT NULL;
ALTER TABLE idempotency_keys ALTER COLUMN lease_expires_at SET DEFAULT now() + interval '5 minutes';

-- Saved exception views keep the optional sort of the SavedViewInput contract schema.
ALTER TABLE saved_views ADD COLUMN IF NOT EXISTS sort text;
