-- Deliveries are per legal entity: two entities may deliver the same source/date (even identical content).
DROP INDEX ux_deliveries_fingerprint;
CREATE UNIQUE INDEX ux_deliveries_fingerprint ON source_deliveries (COALESCE(legal_entity_id, ''), source_name, business_date, fingerprint_sha256)
  WHERE state <> 'REJECTED';

-- Legacy Excel 97-2003 workbooks are accepted by the Excel adapter.
ALTER TABLE source_deliveries DROP CONSTRAINT IF EXISTS source_deliveries_source_format_check;
ALTER TABLE source_deliveries ADD CONSTRAINT source_deliveries_source_format_check CHECK (source_format IN ('CSV','XLSX','XLS','XML'));
