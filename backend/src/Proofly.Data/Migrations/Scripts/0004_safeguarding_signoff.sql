CREATE TABLE safeguarding_positions (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  run_id text NOT NULL REFERENCES runs(id),
  legal_entity_id text NOT NULL REFERENCES legal_entities(id),
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  obligation_amount numeric(38,6) NOT NULL,
  eligible_amount numeric(38,6) NOT NULL,
  difference_amount numeric(38,6) NOT NULL,
  state text NOT NULL CHECK (state IN ('COVERED','SHORTFALL','SURPLUS','QUALIFIED','NO_OBLIGATION')),
  detail jsonb NOT NULL DEFAULT '{}'::jsonb,
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (run_id, currency)
);

CREATE TABLE safeguarding_adjustments (
  id text PRIMARY KEY,
  run_id text NOT NULL REFERENCES runs(id),
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  amount numeric(38,6) NOT NULL,
  reason text NOT NULL,
  status text NOT NULL DEFAULT 'PROPOSED' CHECK (status IN ('PROPOSED','APPROVED','REJECTED','EXPIRED')),
  proposed_by_user_id text NOT NULL REFERENCES users(id),
  expires_at timestamptz,
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_sg_adjustments_run ON safeguarding_adjustments (run_id, status);

CREATE TABLE approvals (
  id text PRIMARY KEY,
  object_type text NOT NULL CHECK (object_type IN ('EXCEPTION_RESOLUTION','SAFEGUARDING_ADJUSTMENT','MANUAL_MATCH','RUN_QUALIFICATION','RUN_SIGN_OFF','CONFIG_CHANGE','ACCESS_CHANGE')),
  object_id text NOT NULL,
  run_id text REFERENCES runs(id),
  proposal_version integer NOT NULL,
  proposer_user_id text NOT NULL REFERENCES users(id),
  approver_user_id text REFERENCES users(id),
  required_role text NOT NULL REFERENCES roles(id),
  status text NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','APPROVED','REJECTED','INVALIDATED','EXPIRED')),
  decision text CHECK (decision IN ('APPROVE','REJECT')),
  rationale text,
  evidence_refs jsonb NOT NULL DEFAULT '[]'::jsonb,
  policy_version text NOT NULL,
  proposed_at timestamptz NOT NULL DEFAULT now(),
  decided_at timestamptz,
  version integer NOT NULL DEFAULT 1,
  updated_at timestamptz NOT NULL DEFAULT now(),
  CHECK (approver_user_id IS NULL OR approver_user_id <> proposer_user_id)  -- separation of duties
);
CREATE INDEX ix_approvals_status ON approvals (status, required_role);
CREATE INDEX ix_approvals_object ON approvals (object_type, object_id);

CREATE TABLE run_signoffs (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  run_id text NOT NULL REFERENCES runs(id),
  state text NOT NULL CHECK (state IN ('PREPARATION_INCOMPLETE','READY_FOR_REVIEW','RETURNED','APPROVAL_PENDING','SIGNED_OFF','INVALIDATED','SUPERSEDED')),
  prepared_by_user_id text REFERENCES users(id),
  signed_by_user_id text REFERENCES users(id),
  attestation_text text,
  attestation_text_hash char(64),
  approval_id text REFERENCES approvals(id),
  evidence_pack_id text,
  signed_at timestamptz,
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CHECK (signed_by_user_id IS NULL OR signed_by_user_id IS DISTINCT FROM prepared_by_user_id)
);
CREATE INDEX ix_run_signoffs_run ON run_signoffs (run_id, created_at DESC);

CREATE TABLE evidence_packs (
  id text PRIMARY KEY,
  run_id text NOT NULL REFERENCES runs(id),
  sha256 char(64) NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
  storage_ref text NOT NULL,
  created_by_user_id text REFERENCES users(id),
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_evidence_packs_run ON evidence_packs (run_id);
ALTER TABLE run_signoffs ADD CONSTRAINT fk_run_signoffs_pack FOREIGN KEY (evidence_pack_id) REFERENCES evidence_packs(id);
