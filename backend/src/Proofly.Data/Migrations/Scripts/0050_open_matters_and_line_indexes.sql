-- Bounded response bodies: the sign-off pack and the open-matters list read the open exceptions of a run in a fixed order
-- (blocking first, severity, absolute value descending, id). The key expressions are the ones used by SignOffRepository
-- (OpenMatterOrder), so the first page and keyset pages become an index range scan with LIMIT. Additive and idempotent.
CREATE INDEX IF NOT EXISTS ix_exceptions_run_open_matters ON exceptions (
  run_id,
  (CASE WHEN blocking THEN 0 ELSE 1 END),
  (CASE severity WHEN 'CRITICAL' THEN 0 WHEN 'HIGH' THEN 1 WHEN 'MEDIUM' THEN 2 ELSE 3 END),
  (-COALESCE(abs(value_amount), -1)),
  (id COLLATE "C"))
  WHERE status NOT IN ('RESOLVED','ACCEPTED_DIFFERENCE','FALSE_POSITIVE');

-- Safeguarding balance lines are paged per currency, largest absolute amount first.
CREATE INDEX IF NOT EXISTS ix_safeguarding_balances_delivery_currency_abs ON safeguarding_balances (delivery_id, currency, (abs(balance_amount)) DESC, id);
