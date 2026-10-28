CREATE TABLE config_rules (
  id text PRIMARY KEY,
  kind text NOT NULL,            -- rule | source | approval_policy | setting
  name text NOT NULL,
  config_version text NOT NULL,
  enabled boolean NOT NULL DEFAULT true,
  payload jsonb NOT NULL DEFAULT '{}'::jsonb,
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_config_rules_kind ON config_rules (kind);

CREATE TABLE config_changes (
  id text PRIMARY KEY,
  config_id text NOT NULL REFERENCES config_rules(id),
  proposed_by_user_id text NOT NULL REFERENCES users(id),
  reason text NOT NULL,
  before jsonb,
  after jsonb NOT NULL,
  status text NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','APPROVED','REJECTED','INVALIDATED','EXPIRED')),
  approval_id text REFERENCES approvals(id),
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_config_changes_config ON config_changes (config_id, created_at DESC);

CREATE TABLE saved_views (
  id text PRIMARY KEY,
  user_id text NOT NULL REFERENCES users(id),
  scope text NOT NULL,           -- e.g. exceptions, transactions
  name text NOT NULL,
  filters jsonb NOT NULL DEFAULT '{}'::jsonb,
  shared boolean NOT NULL DEFAULT false,
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (user_id, scope, name)
);

CREATE TABLE idempotency_keys (
  key text NOT NULL,
  user_id text NOT NULL,
  request_hash char(64) NOT NULL,
  state text NOT NULL DEFAULT 'IN_PROGRESS' CHECK (state IN ('IN_PROGRESS','COMPLETED')),
  response_status integer,
  response_body jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL DEFAULT now() + interval '24 hours',
  PRIMARY KEY (user_id, key)
);
CREATE INDEX ix_idempotency_expires ON idempotency_keys (expires_at);

-- Bump version/updated_at on every UPDATE of mutable tables.
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['legal_entities','users','runs','source_deliveries','exceptions','resolution_proposals',
    'match_groups','safeguarding_positions','safeguarding_adjustments','approvals','run_signoffs','config_rules',
    'config_changes','saved_views']
  LOOP
    EXECUTE format('CREATE TRIGGER trg_%I_version BEFORE UPDATE ON %I FOR EACH ROW EXECUTE FUNCTION proofly_bump_version()', t, t);
  END LOOP;
END $$;
