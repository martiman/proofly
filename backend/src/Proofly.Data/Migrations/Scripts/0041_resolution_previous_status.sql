-- The working status an exception had when a resolution was proposed; a rejected proposal restores it.
ALTER TABLE resolution_proposals ADD COLUMN IF NOT EXISTS previous_status text;
