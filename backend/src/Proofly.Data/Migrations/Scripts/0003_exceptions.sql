CREATE TABLE exceptions (
  id text PRIMARY KEY,
  run_id text NOT NULL REFERENCES runs(id),
  legal_entity_id text NOT NULL REFERENCES legal_entities(id),
  business_date date NOT NULL,
  title text NOT NULL,
  control text NOT NULL,
  reason_code text NOT NULL,
  severity text NOT NULL CHECK (severity IN ('CRITICAL','HIGH','MEDIUM','LOW')),
  status text NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN','IN_INVESTIGATION','WAITING_FOR_DATA','PENDING_APPROVAL','RESOLVED','ACCEPTED_DIFFERENCE','FALSE_POSITIVE')),
  value_amount numeric(38,6),
  value_currency char(3) CHECK (value_currency ~ '^[A-Z]{3}$'),
  owner_user_id text REFERENCES users(id),
  due_at timestamptz NOT NULL,
  blocking boolean NOT NULL DEFAULT false,
  source_file text,
  source_row_id text,
  delivery_id text REFERENCES source_deliveries(id),
  resolution_kind text CHECK (resolution_kind IN ('RESOLVED','ACCEPTED_DIFFERENCE','FALSE_POSITIVE')),
  resolution_reason text,
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CHECK ((value_amount IS NULL) = (value_currency IS NULL))
);
CREATE INDEX ix_exceptions_run_status ON exceptions (run_id, status);
CREATE INDEX ix_exceptions_severity ON exceptions (severity, status);
CREATE INDEX ix_exceptions_owner ON exceptions (owner_user_id, status);
CREATE INDEX ix_exceptions_currency ON exceptions (value_currency, status);
CREATE INDEX ix_exceptions_entity_date ON exceptions (legal_entity_id, business_date);

CREATE TABLE exception_notes (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  exception_id text NOT NULL REFERENCES exceptions(id),
  author_user_id text NOT NULL REFERENCES users(id),
  body text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_exception_notes_exception ON exception_notes (exception_id, created_at);

CREATE TABLE exception_evidence_refs (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  exception_id text NOT NULL REFERENCES exceptions(id),
  kind text NOT NULL,
  ref text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_exception_evidence_exception ON exception_evidence_refs (exception_id);

CREATE TABLE exception_timeline (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  exception_id text NOT NULL REFERENCES exceptions(id),
  kind text NOT NULL,
  actor_type text NOT NULL CHECK (actor_type IN ('USER','SYSTEM','SERVICE_ACCOUNT')),
  actor_id text,
  summary text NOT NULL,
  occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_exception_timeline_exception ON exception_timeline (exception_id, occurred_at);

CREATE TABLE exception_assignments (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  exception_id text NOT NULL REFERENCES exceptions(id),
  assignee_user_id text REFERENCES users(id),
  assigned_by_user_id text NOT NULL REFERENCES users(id),
  reason text NOT NULL,
  assigned_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_exception_assignments_exception ON exception_assignments (exception_id, assigned_at);

CREATE TABLE resolution_proposals (
  id text PRIMARY KEY,
  exception_id text NOT NULL REFERENCES exceptions(id),
  kind text NOT NULL CHECK (kind IN ('RESOLVED','ACCEPTED_DIFFERENCE','FALSE_POSITIVE')),
  rationale text NOT NULL,
  proposed_by_user_id text NOT NULL REFERENCES users(id),
  status text NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','APPROVED','REJECTED','INVALIDATED','EXPIRED')),
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_resolution_proposals_exception ON resolution_proposals (exception_id);

CREATE TABLE match_groups (
  id text PRIMARY KEY,
  run_id text NOT NULL REFERENCES runs(id),
  legal_entity_id text NOT NULL REFERENCES legal_entities(id),
  status text NOT NULL CHECK (status IN ('MATCHED','UNMATCHED','VALUE_MISMATCH','NOT_APPLICABLE','PENDING_APPROVAL')),
  difference_amount numeric(38,6),
  currency char(3) CHECK (currency ~ '^[A-Z]{3}$'),
  manual boolean NOT NULL DEFAULT false,
  rule_id text,
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CHECK ((difference_amount IS NULL) = (currency IS NULL))
);
CREATE INDEX ix_match_groups_run_status ON match_groups (run_id, status);

CREATE TABLE match_candidates (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  match_group_id text NOT NULL REFERENCES match_groups(id),
  kind text NOT NULL CHECK (kind IN ('BANK_TRANSFER','DIRECT_DEBIT','CARD','BANK_EVIDENCE','LEDGER_ENTRY')),
  record_table text NOT NULL,
  record_id bigint NOT NULL,
  amount numeric(38,6) NOT NULL,
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  score numeric(5,4),
  selected boolean NOT NULL DEFAULT false
);
CREATE INDEX ix_match_candidates_group ON match_candidates (match_group_id);
CREATE INDEX ix_match_candidates_record ON match_candidates (record_table, record_id);
