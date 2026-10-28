-- Contract/schema reconciliation (G7-G17). One migration, idempotent (IF NOT EXISTS / guarded DO blocks), no data loss.
-- Columns that cannot be reconstructed for existing rows stay NULL; nothing is invented for them.

-- G7: 1-based row of the record in the source file (accepted rows; rejected rows already carry delivery_rejections.row_number).
-- Existing rows keep NULL: the true file row is unknown once rejected rows were skipped.
ALTER TABLE transfer_instructions  ADD COLUMN IF NOT EXISTS row_number integer CHECK (row_number >= 1);
ALTER TABLE direct_debit_events    ADD COLUMN IF NOT EXISTS row_number integer CHECK (row_number >= 1);
ALTER TABLE card_events            ADD COLUMN IF NOT EXISTS row_number integer CHECK (row_number >= 1);
ALTER TABLE card_settlements       ADD COLUMN IF NOT EXISTS row_number integer CHECK (row_number >= 1);
ALTER TABLE ledger_entries         ADD COLUMN IF NOT EXISTS row_number integer CHECK (row_number >= 1);
ALTER TABLE account_balances       ADD COLUMN IF NOT EXISTS row_number integer CHECK (row_number >= 1);
ALTER TABLE bank_transactions      ADD COLUMN IF NOT EXISTS row_number integer CHECK (row_number >= 1);
ALTER TABLE safeguarding_balances  ADD COLUMN IF NOT EXISTS row_number integer CHECK (row_number >= 1);

-- G8: match groups carry what the control recorded; candidates carry their side.
ALTER TABLE match_groups ADD COLUMN IF NOT EXISTS tolerance text;
ALTER TABLE match_groups ADD COLUMN IF NOT EXISTS rationale text;
ALTER TABLE match_groups ADD COLUMN IF NOT EXISTS cardinality text;
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_match_groups_cardinality' AND conrelid = 'match_groups'::regclass) THEN
    ALTER TABLE match_groups ADD CONSTRAINT ck_match_groups_cardinality CHECK (cardinality IS NULL OR cardinality ~ '^(0|1|N):(0|1|N)$');
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS match_group_exceptions (
  match_group_id text NOT NULL REFERENCES match_groups(id),
  exception_id text NOT NULL REFERENCES exceptions(id),
  PRIMARY KEY (match_group_id, exception_id)
);
CREATE INDEX IF NOT EXISTS ix_match_group_exceptions_exception ON match_group_exceptions (exception_id);

ALTER TABLE match_candidates ADD COLUMN IF NOT EXISTS side text CHECK (side IN ('LEFT','RIGHT'));
UPDATE match_candidates SET side = CASE WHEN kind IN ('BANK_EVIDENCE','LEDGER_ENTRY') THEN 'RIGHT' ELSE 'LEFT' END WHERE side IS NULL;

-- G9: evidence pack lifecycle. A GENERATING or FAILED pack has no digest or storage yet.
ALTER TABLE evidence_packs ALTER COLUMN sha256 DROP NOT NULL;
ALTER TABLE evidence_packs ALTER COLUMN storage_ref DROP NOT NULL;
ALTER TABLE evidence_packs ADD COLUMN IF NOT EXISTS size_bytes bigint CHECK (size_bytes >= 0);
ALTER TABLE evidence_packs ADD COLUMN IF NOT EXISTS contents jsonb NOT NULL DEFAULT '[]'::jsonb;
ALTER TABLE evidence_packs ADD COLUMN IF NOT EXISTS status text NOT NULL DEFAULT 'READY' CHECK (status IN ('GENERATING','READY','FAILED'));
ALTER TABLE evidence_packs ADD COLUMN IF NOT EXISTS failure_reason text;
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_evidence_packs_ready' AND conrelid = 'evidence_packs'::regclass) THEN
    ALTER TABLE evidence_packs ADD CONSTRAINT ck_evidence_packs_ready
      CHECK (status <> 'READY' OR (sha256 IS NOT NULL AND storage_ref IS NOT NULL));
  END IF;
END $$;

-- G10: adjustments keep their evidence references and an expiry DATE (the business-date comparison needs no time zone).
ALTER TABLE safeguarding_adjustments ADD COLUMN IF NOT EXISTS evidence_refs jsonb NOT NULL DEFAULT '[]'::jsonb;
ALTER TABLE safeguarding_adjustments ADD COLUMN IF NOT EXISTS expires_on date;
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'safeguarding_adjustments' AND column_name = 'expires_at') THEN
    UPDATE safeguarding_adjustments SET expires_on = (expires_at AT TIME ZONE 'UTC')::date WHERE expires_on IS NULL AND expires_at IS NOT NULL;
    ALTER TABLE safeguarding_adjustments DROP COLUMN expires_at;
  END IF;
END $$;
-- Backfill from the approval record where the proposal stored its references there.
UPDATE safeguarding_adjustments a SET evidence_refs = x.refs
FROM (SELECT DISTINCT ON (object_id) object_id, evidence_refs AS refs FROM approvals
      WHERE object_type = 'SAFEGUARDING_ADJUSTMENT' AND jsonb_typeof(evidence_refs) = 'array' AND jsonb_array_length(evidence_refs) > 0
      ORDER BY object_id, proposed_at DESC) x
WHERE x.object_id = a.id AND jsonb_array_length(a.evidence_refs) = 0;
-- A proposal needs at least one reference. NOT VALID: rows that predate the rule are not rewritten; new and updated rows are checked.
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_sg_adjustments_evidence' AND conrelid = 'safeguarding_adjustments'::regclass) THEN
    ALTER TABLE safeguarding_adjustments ADD CONSTRAINT ck_sg_adjustments_evidence
      CHECK (jsonb_typeof(evidence_refs) = 'array' AND jsonb_array_length(evidence_refs) >= 1) NOT VALID;
  END IF;
END $$;

-- G11: independent-ledger control results per run, account and currency (written by the controls orchestration).
-- opening/independent/recorded/difference are NULL when the control could not compute them (e.g. opening balance unsupported).
CREATE TABLE IF NOT EXISTS account_control_results (
  run_id text NOT NULL REFERENCES runs(id),
  account_id text NOT NULL,
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  opening numeric(38,6),
  credits numeric(38,6) NOT NULL DEFAULT 0,
  debits numeric(38,6) NOT NULL DEFAULT 0,
  adjustments numeric(38,6) NOT NULL DEFAULT 0,
  independent numeric(38,6),
  recorded numeric(38,6),
  difference numeric(38,6),
  opening_supported boolean NOT NULL DEFAULT false,
  contributions jsonb NOT NULL DEFAULT '[]'::jsonb,
  exclusions jsonb NOT NULL DEFAULT '[]'::jsonb,
  rule_version text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (run_id, account_id, currency)
);

-- G12: the rule trace the control recorded for an exception.
CREATE TABLE IF NOT EXISTS exception_rule_traces (
  exception_id text PRIMARY KEY REFERENCES exceptions(id),
  rule_id text NOT NULL,
  rule_version text NOT NULL,
  tolerance text,
  expected_effect text,
  description text,
  calculation jsonb NOT NULL DEFAULT '{}'::jsonb,   -- {"inputs":[{"name","value"}],"text":"...","result":"..."}
  created_at timestamptz NOT NULL DEFAULT now()
);

-- G14: explicit exception -> record links (the transaction and account views read these in addition to the delivery/row match).
CREATE TABLE IF NOT EXISTS exception_links (
  exception_id text NOT NULL REFERENCES exceptions(id),
  record_table text NOT NULL CHECK (record_table IN ('transfer_instructions','direct_debit_events','card_events','card_settlements',
                                                      'ledger_entries','account_balances','bank_transactions','safeguarding_balances')),
  record_id bigint NOT NULL,
  PRIMARY KEY (exception_id, record_table, record_id)
);
CREATE INDEX IF NOT EXISTS ix_exception_links_record ON exception_links (record_table, record_id);

-- G15: config change status uses the contract names; access changes have no config_rules row.
ALTER TABLE config_changes DROP CONSTRAINT IF EXISTS config_changes_status_check;
UPDATE config_changes SET status = CASE status WHEN 'PENDING' THEN 'PENDING_APPROVAL' ELSE 'RETIRED' END
 WHERE status IN ('PENDING','INVALIDATED','EXPIRED');
ALTER TABLE config_changes ADD CONSTRAINT config_changes_status_check
  CHECK (status IN ('DRAFT','PENDING_APPROVAL','APPROVED','ACTIVE','REJECTED','RETIRED'));
ALTER TABLE config_changes ALTER COLUMN status SET DEFAULT 'PENDING_APPROVAL';
ALTER TABLE config_changes ADD COLUMN IF NOT EXISTS change_kind text NOT NULL DEFAULT 'CONFIG' CHECK (change_kind IN ('CONFIG','ACCESS'));
ALTER TABLE config_changes ADD COLUMN IF NOT EXISTS target_user_id text REFERENCES users(id);
ALTER TABLE config_changes ALTER COLUMN config_id DROP NOT NULL;
ALTER TABLE config_changes DROP CONSTRAINT IF EXISTS ck_config_changes_target;
ALTER TABLE config_changes ADD CONSTRAINT ck_config_changes_target
  CHECK ((change_kind = 'CONFIG' AND config_id IS NOT NULL AND target_user_id IS NULL)
      OR (change_kind = 'ACCESS' AND config_id IS NULL AND target_user_id IS NOT NULL));

-- G16: currencies per legal entity; always contains the base currency (trigger keeps it so).
ALTER TABLE legal_entities ADD COLUMN IF NOT EXISTS currencies text[] NOT NULL DEFAULT '{}';
CREATE OR REPLACE FUNCTION proofly_entity_currencies() RETURNS trigger AS $$
BEGIN
  IF NOT (NEW.base_currency::text = ANY(NEW.currencies)) THEN
    NEW.currencies := array_append(NEW.currencies, NEW.base_currency::text);
  END IF;
  RETURN NEW;
END $$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS trg_legal_entities_currencies ON legal_entities;
CREATE TRIGGER trg_legal_entities_currencies BEFORE INSERT OR UPDATE ON legal_entities
  FOR EACH ROW EXECUTE FUNCTION proofly_entity_currencies();
UPDATE legal_entities le SET currencies = ARRAY(
  SELECT DISTINCT c FROM (SELECT unnest(le.currencies) AS c
                          UNION SELECT le.base_currency::text
                          UNION SELECT sp.currency::text FROM safeguarding_positions sp WHERE sp.legal_entity_id = le.id) u
  ORDER BY c)
WHERE cardinality(le.currencies) = 0;

-- G17: control names on exceptions are the lower snake ControlArea wire names.
-- Enforced for new and updated rows; validated for existing rows only when none violates it.
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_exceptions_control' AND conrelid = 'exceptions'::regclass) THEN
    ALTER TABLE exceptions ADD CONSTRAINT ck_exceptions_control
      CHECK (control IN ('data_completeness','matching','independent_ledger','transaction_lifecycle','safeguarding')) NOT VALID;
    IF NOT EXISTS (SELECT 1 FROM exceptions WHERE control NOT IN ('data_completeness','matching','independent_ledger','transaction_lifecycle','safeguarding')) THEN
      ALTER TABLE exceptions VALIDATE CONSTRAINT ck_exceptions_control;
    END IF;
  END IF;
END $$;
COMMENT ON COLUMN exceptions.control IS 'ControlArea wire name: data_completeness | matching | independent_ledger | transaction_lifecycle | safeguarding';
COMMENT ON COLUMN approvals.object_id IS 'EXCEPTION_RESOLUTION: the resolution proposal id (resolution_proposals.id); every other object type: the id of the object itself (adjustment, match group, run, config change)';
