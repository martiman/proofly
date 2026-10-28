-- #15: fixed role -> permission bundles (ADR 0005). Mirrors Proofly.Domain.Auth.RolePermissions (a test asserts equality).
-- Idempotent. Users and their role assignments are data, not seeded here.
INSERT INTO roles (id, name) VALUES
  ('ANALYST', 'Analyst'), ('APPROVER', 'Approver'), ('CONFIG_ADMIN', 'Configuration administrator'),
  ('SECURITY_ADMIN', 'Security administrator'), ('VIEWER', 'Viewer')
ON CONFLICT (id) DO NOTHING;

INSERT INTO permissions (id) VALUES
  ('auth.self'),('run.view'),('run.reprocess'),('source.view'),('source.retry'),('source.supersede'),
  ('exception.view'),('exception.assign'),('exception.investigate'),('exception.propose'),('exception.approve'),('exception.reopen'),
  ('transaction.view'),('matching.view'),('matching.propose'),('ledger.view'),('safeguarding.view'),
  ('safeguarding.adjustment.propose'),('safeguarding.adjustment.approve'),
  ('signoff.view'),('signoff.prepare'),('signoff.decide'),('evidence.view'),('evidence.export'),('audit.view'),
  ('settings.view'),('settings.draft'),('settings.approve'),('access.manage'),('approval.view')
ON CONFLICT (id) DO NOTHING;

INSERT INTO role_permissions (role_id, permission_id) VALUES
  ('VIEWER','auth.self'),('VIEWER','run.view'),('VIEWER','source.view'),('VIEWER','exception.view'),('VIEWER','transaction.view'),
  ('VIEWER','matching.view'),('VIEWER','ledger.view'),('VIEWER','safeguarding.view'),('VIEWER','signoff.view'),
  ('VIEWER','evidence.view'),('VIEWER','evidence.export'),('VIEWER','audit.view'),('VIEWER','approval.view'),

  ('ANALYST','auth.self'),('ANALYST','run.view'),('ANALYST','source.view'),('ANALYST','source.retry'),('ANALYST','exception.view'),
  ('ANALYST','exception.assign'),('ANALYST','exception.investigate'),('ANALYST','exception.propose'),('ANALYST','exception.reopen'),
  ('ANALYST','transaction.view'),('ANALYST','matching.view'),('ANALYST','matching.propose'),('ANALYST','ledger.view'),
  ('ANALYST','safeguarding.view'),('ANALYST','safeguarding.adjustment.propose'),('ANALYST','signoff.view'),('ANALYST','signoff.prepare'),
  ('ANALYST','evidence.view'),('ANALYST','approval.view'),

  ('APPROVER','auth.self'),('APPROVER','run.view'),('APPROVER','source.view'),('APPROVER','source.retry'),('APPROVER','exception.view'),
  ('APPROVER','exception.assign'),('APPROVER','exception.investigate'),('APPROVER','exception.propose'),('APPROVER','exception.reopen'),
  ('APPROVER','transaction.view'),('APPROVER','matching.view'),('APPROVER','matching.propose'),('APPROVER','ledger.view'),
  ('APPROVER','safeguarding.view'),('APPROVER','safeguarding.adjustment.propose'),('APPROVER','signoff.view'),('APPROVER','signoff.prepare'),
  ('APPROVER','evidence.view'),('APPROVER','approval.view'),
  ('APPROVER','run.reprocess'),('APPROVER','source.supersede'),('APPROVER','exception.approve'),('APPROVER','safeguarding.adjustment.approve'),
  ('APPROVER','signoff.decide'),('APPROVER','evidence.export'),('APPROVER','audit.view'),('APPROVER','settings.view'),

  ('CONFIG_ADMIN','auth.self'),('CONFIG_ADMIN','run.view'),('CONFIG_ADMIN','settings.view'),('CONFIG_ADMIN','settings.draft'),
  ('CONFIG_ADMIN','settings.approve'),('CONFIG_ADMIN','approval.view'),

  ('SECURITY_ADMIN','auth.self'),('SECURITY_ADMIN','run.view'),('SECURITY_ADMIN','access.manage'),('SECURITY_ADMIN','settings.view'),
  ('SECURITY_ADMIN','approval.view'),('SECURITY_ADMIN','audit.view')
ON CONFLICT (role_id, permission_id) DO NOTHING;
