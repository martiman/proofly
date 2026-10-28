-- Ingestion framework (#29). Evidence is content-addressed on disk by fingerprint_sha256; nothing else is stored there.
ALTER TABLE delivery_rejections
  ADD COLUMN row_number integer,
  ADD COLUMN field text,
  ADD COLUMN raw_excerpt text;

ALTER TABLE source_deliveries
  ADD COLUMN source_format text NOT NULL DEFAULT 'CSV' CHECK (source_format IN ('CSV','XLSX','XML')),
  ADD COLUMN sequence_number integer,
  ADD COLUMN ingested_at timestamptz,          -- null while RECEIVED-but-not-yet-ingested; makes ingestion idempotent
  ADD COLUMN total_row_count integer,          -- every data row parsed from the file (accepted + rejected)
  ADD COLUMN actual_total_eur numeric(38,6),   -- sum of control amounts over all rows (declared totals cover rejected rows too)
  ADD COLUMN actual_total_czk numeric(38,6);

-- A REJECTED delivery (e.g. a duplicate of an accepted one) is recorded but must not block the original, so the
-- uniqueness only applies to deliveries that are not rejected.
DROP INDEX ux_deliveries_fingerprint;
CREATE UNIQUE INDEX ux_deliveries_fingerprint ON source_deliveries (source_name, business_date, fingerprint_sha256)
  WHERE state <> 'REJECTED';
