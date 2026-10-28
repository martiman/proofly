-- Structured manifest and audit-chain verification of an evidence pack (contract batch 3, EvidencePack.manifest / auditChain).
-- Both are written when the pack becomes READY, from the same values that went into the zip's manifest.json and
-- audit/chain_verification.json. NULL while GENERATING and for packs generated before this migration.
ALTER TABLE evidence_packs ADD COLUMN IF NOT EXISTS manifest jsonb;
ALTER TABLE evidence_packs ADD COLUMN IF NOT EXISTS audit_chain jsonb;
