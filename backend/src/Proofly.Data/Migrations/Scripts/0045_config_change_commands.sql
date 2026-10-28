-- Issue #45: configuration/access change propose + four-eyes decision, versioned rulesets.
ALTER TABLE config_changes ADD COLUMN IF NOT EXISTS target_kind text NOT NULL DEFAULT 'RULE'
  CHECK (target_kind IN ('RULE','SOURCE','USER_ACCESS','APPROVAL_POLICY'));
ALTER TABLE config_changes ADD COLUMN IF NOT EXISTS target_id text NOT NULL DEFAULT '';
ALTER TABLE config_changes ADD COLUMN IF NOT EXISTS effective_from date NOT NULL DEFAULT current_date;
ALTER TABLE config_changes ADD COLUMN IF NOT EXISTS base_version integer;          -- config_rules.version at proposal (stale-target guard)
ALTER TABLE config_changes ADD COLUMN IF NOT EXISTS decided_by_user_id text REFERENCES users(id);
ALTER TABLE config_changes ADD COLUMN IF NOT EXISTS decided_at timestamptz;
ALTER TABLE config_changes ADD COLUMN IF NOT EXISTS ruleset_version text;          -- ruleset created by activating this change

UPDATE config_changes c SET target_kind = CASE r.kind WHEN 'source' THEN 'SOURCE' WHEN 'approval_policy' THEN 'APPROVAL_POLICY' ELSE 'RULE' END,
       target_id = r.id
FROM config_rules r WHERE c.config_id = r.id AND c.target_id = '';
UPDATE config_changes SET target_kind = 'USER_ACCESS', target_id = target_user_id WHERE change_kind = 'ACCESS' AND target_id = '';

ALTER TABLE config_changes ADD CONSTRAINT ck_config_changes_sod
  CHECK (decided_by_user_id IS NULL OR decided_by_user_id <> proposed_by_user_id);   -- separation of duties
CREATE UNIQUE INDEX uq_config_changes_pending_target ON config_changes (change_kind, COALESCE(config_id, target_user_id))
  WHERE status = 'PENDING_APPROVAL';

-- Every ACTIVE config change creates a ruleset version; runs.ruleset_version records the one in force at run start.
CREATE TABLE ruleset_versions (
  version text PRIMARY KEY,
  seq integer NOT NULL UNIQUE,
  activated_at timestamptz NOT NULL,
  effective_from date NOT NULL,
  change_id text REFERENCES config_changes(id),
  snapshot jsonb NOT NULL DEFAULT '[]'::jsonb
);
INSERT INTO ruleset_versions (version, seq, activated_at, effective_from, snapshot)
SELECT 'v1', 1, timestamptz '1970-01-01 00:00:00+00', date '1970-01-01',
       COALESCE((SELECT jsonb_agg(jsonb_build_object('id', id, 'kind', kind, 'name', name, 'version', config_version, 'payload', payload) ORDER BY id)
                 FROM config_rules WHERE enabled), '[]'::jsonb);
