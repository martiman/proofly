-- #16: per-chain sequence, actor name, summary and rule version for the audit hash chain.
-- audit_events stays append-only (0006 triggers); only audit_chain_head is mutable, and only inside the writer's transaction.
ALTER TABLE audit_events
  ADD COLUMN chain_id text NOT NULL DEFAULT '*',
  ADD COLUMN chain_seq bigint NOT NULL DEFAULT 0,
  ADD COLUMN actor_name text,
  ADD COLUMN summary text NOT NULL DEFAULT '',
  ADD COLUMN rule_version text;
CREATE UNIQUE INDEX ux_audit_chain_seq ON audit_events (chain_id, chain_seq);
CREATE INDEX ix_audit_keyset ON audit_events (occurred_at DESC, id DESC);

-- One row per chain (chain_id = legal entity id, or '*' for the global chain). The writer locks this row
-- (SELECT ... FOR UPDATE) for the rest of its transaction, which serialises appenders to one chain.
CREATE TABLE audit_chain_head (
  chain_id text PRIMARY KEY,
  last_seq bigint NOT NULL,
  last_hash char(64)
);
