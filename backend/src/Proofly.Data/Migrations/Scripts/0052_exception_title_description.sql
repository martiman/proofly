-- Exceptions keep a short human title (headline, <= 80 characters) and a longer description, both written at creation from the finding.
-- Before this migration the title held the full technical message cut at 200 characters.
ALTER TABLE exceptions ADD COLUMN IF NOT EXISTS description text;

-- Backfill: the description is the full control message of the rule trace (the title was a truncated copy of it), the title a sentence-case reason label.
UPDATE exceptions e
   SET description = COALESCE(NULLIF(t.description, ''), e.title)
  FROM exception_rule_traces t
 WHERE t.exception_id = e.id AND e.description IS NULL;
UPDATE exceptions SET description = title WHERE description IS NULL;
UPDATE exceptions
   SET title = upper(left(replace(reason_code, '_', ' '), 1)) || lower(substr(replace(reason_code, '_', ' '), 2))
 WHERE char_length(title) > 80;

-- Nullable on purpose: readers use COALESCE(description, title) for a row written by something other than run processing.
ALTER TABLE exceptions ADD CONSTRAINT ck_exceptions_title_short CHECK (char_length(title) <= 80) NOT VALID;
