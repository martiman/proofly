-- Second contract pass: a QUALIFIED currency has no computed difference (null, not 0); positions are flagged stale when an
-- adjustment is approved or expires after the run was processed.
ALTER TABLE safeguarding_positions ALTER COLUMN difference_amount DROP NOT NULL;
ALTER TABLE safeguarding_positions ADD COLUMN stale boolean NOT NULL DEFAULT false;

-- Retried deliveries: rejection rows of an earlier attempt are kept for the audit trail but superseded by the retry.
ALTER TABLE delivery_rejections ADD COLUMN superseded_by_retry boolean NOT NULL DEFAULT false;
