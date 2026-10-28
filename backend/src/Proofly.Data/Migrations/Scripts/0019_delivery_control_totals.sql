-- Per-currency control totals (#29 follow-up): replaces the fixed EUR/CZK columns so any currency can be declared.
-- A row exists for each currency that is declared and/or measured; NULL = not declared / not (yet) measured.
CREATE TABLE delivery_control_totals (
  delivery_id text NOT NULL REFERENCES source_deliveries(id),
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  declared_amount numeric(38,6),
  actual_amount numeric(38,6),
  PRIMARY KEY (delivery_id, currency)
);

INSERT INTO delivery_control_totals (delivery_id, currency, declared_amount, actual_amount)
SELECT id, 'EUR', declared_total_eur, actual_total_eur FROM source_deliveries
WHERE declared_total_eur IS NOT NULL OR actual_total_eur IS NOT NULL;

INSERT INTO delivery_control_totals (delivery_id, currency, declared_amount, actual_amount)
SELECT id, 'CZK', declared_total_czk, actual_total_czk FROM source_deliveries
WHERE declared_total_czk IS NOT NULL OR actual_total_czk IS NOT NULL;

ALTER TABLE source_deliveries
  DROP COLUMN declared_total_eur,
  DROP COLUMN declared_total_czk,
  DROP COLUMN actual_total_eur,
  DROP COLUMN actual_total_czk;
