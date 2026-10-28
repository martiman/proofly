-- Conventions: text ids (API-facing), NUMERIC(38,6) money + char(3) currency CHECK, timestamptz,
-- mutable tables carry version (ETag/If-Match; bumped by trigger) and updated_at.
CREATE OR REPLACE FUNCTION proofly_bump_version() RETURNS trigger AS $$
BEGIN
  NEW.version := OLD.version + 1;
  NEW.updated_at := now();
  RETURN NEW;
END $$ LANGUAGE plpgsql;

CREATE TABLE legal_entities (
  id text PRIMARY KEY,
  name text NOT NULL,
  base_currency char(3) NOT NULL CHECK (base_currency ~ '^[A-Z]{3}$'),
  active boolean NOT NULL DEFAULT true,
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE users (
  id text PRIMARY KEY,
  name text NOT NULL,
  email text NOT NULL UNIQUE,
  external_subject text UNIQUE,
  active boolean NOT NULL DEFAULT true,
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE roles (
  id text PRIMARY KEY,
  name text NOT NULL
);
CREATE TABLE permissions (
  id text PRIMARY KEY,
  description text
);
CREATE TABLE role_permissions (
  role_id text NOT NULL REFERENCES roles(id),
  permission_id text NOT NULL REFERENCES permissions(id),
  PRIMARY KEY (role_id, permission_id)
);
CREATE TABLE user_roles (
  user_id text NOT NULL REFERENCES users(id),
  role_id text NOT NULL REFERENCES roles(id),
  legal_entity_id text NOT NULL DEFAULT '*',  -- '*' = all entities in scope
  PRIMARY KEY (user_id, role_id, legal_entity_id)
);

CREATE TABLE runs (
  id text PRIMARY KEY,
  legal_entity_id text NOT NULL REFERENCES legal_entities(id),
  business_date date NOT NULL,
  run_version integer NOT NULL CHECK (run_version >= 1),
  ruleset_version text NOT NULL,
  state text NOT NULL CHECK (state IN ('AWAITING_EVIDENCE','PROCESSING','REVIEW_REQUIRED','READY_FOR_SIGN_OFF','SIGNED_OFF','SUPERSEDED','FAILED')),
  sign_off_state text NOT NULL DEFAULT 'PREPARATION_INCOMPLETE' CHECK (sign_off_state IN ('PREPARATION_INCOMPLETE','READY_FOR_REVIEW','RETURNED','APPROVAL_PENDING','SIGNED_OFF','INVALIDATED','SUPERSEDED')),
  supersedes_run_id text REFERENCES runs(id),
  started_at timestamptz NOT NULL DEFAULT now(),
  last_evidence_arrival_at timestamptz,
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (legal_entity_id, business_date, run_version)
);
CREATE INDEX ix_runs_entity_date ON runs (legal_entity_id, business_date DESC, run_version DESC);
CREATE INDEX ix_runs_state ON runs (state);

CREATE TABLE source_deliveries (
  id text PRIMARY KEY,                       -- delivery_id from the manifest
  run_id text REFERENCES runs(id),
  legal_entity_id text REFERENCES legal_entities(id),
  source_name text NOT NULL,
  business_date date NOT NULL,
  schema_version text NOT NULL,
  file_name text NOT NULL,
  fingerprint_sha256 char(64) NOT NULL CHECK (fingerprint_sha256 ~ '^[0-9a-f]{64}$'),
  declared_record_count integer CHECK (declared_record_count >= 0),
  declared_total_eur numeric(38,6),
  declared_total_czk numeric(38,6),
  expected_by timestamptz,
  received_at timestamptz,
  state text NOT NULL CHECK (state IN ('RECEIVED','LATE','MISSING','REJECTED','SUPERSEDED','QUALIFIED')),
  accepted_record_count integer NOT NULL DEFAULT 0,
  rejected_record_count integer NOT NULL DEFAULT 0,
  supersedes_delivery_id text REFERENCES source_deliveries(id),
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
-- Same bytes for the same source/date are a duplicate delivery.
CREATE UNIQUE INDEX ux_deliveries_fingerprint ON source_deliveries (source_name, business_date, fingerprint_sha256);
CREATE INDEX ix_deliveries_run ON source_deliveries (run_id, source_name);
CREATE INDEX ix_deliveries_state ON source_deliveries (state);

CREATE TABLE delivery_rejections (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  delivery_id text NOT NULL REFERENCES source_deliveries(id),
  source_row_id text,
  reason_code text NOT NULL,
  reason text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_delivery_rejections_delivery ON delivery_rejections (delivery_id);
