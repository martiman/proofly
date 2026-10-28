# Web visual QA 1 (Control Room, Sign-off, Exceptions Queue, Exception Detail, shell)

Method: real browser at 1440x1100 against the demo backend (RUN-LE-BENCH-20261027-v1), APPROVER and VIEWER tokens, compared with pen frames r363w, EYlJX, x1lAcB, nqNxK, UrbT6.
Pen exports: /private/tmp/claude-501/-Users-mates-dev-androsoft-proofly--claude-worktrees-app-vue-ant-design-vite-39c172/f7307fad-4f1b-4d25-81ce-6dffe7c7f984/scratchpad/pen/{r363w,EYlJX,x1lAcB,nqNxK,UrbT6}.png
Browser screenshots live in the tool-results directory of the session (not saved as files). The pen frames use the original 29-exception fixture, the demo data is the 84-exception benchmark, so text differs by design (API-provided titles are long messages, not short headlines).

## Shell (ContextBar)
- Found: "Business date" label wrapped to two lines, currency list wrapped, date select truncated ("2026-1...").
- Fixed: nowrap on labels and values, fixed select widths (entity 200, date 112, run 150).
- Open: the pen shows "Currency All"; the app lists entity currencies (EUR · GBP · JPY). No currency filter exists in the context model. Sidebar matches (width, groups, badge, footer).

## Daily Control Room - minor gaps
- Fixed: ruleset string in the subtitle was a raw 100+ character id; now shows `v1+proofly-controls/1` with the full value in a tooltip (`rulesetLabel`, tested). Control result money wrapped mid-token and "Review required" pills wrapped; both now nowrap. Control table columns rebalanced.
- Open (API/contract): exception titles are full technical messages and are truncated server side at about 200 characters (e.g. "Unexplained resi"); the pen shows a short headline plus a one-line description. Needs `title` (short) and `description` fields on ExceptionSummary. Blockers list shows first 10 of 11 with a "Showing 10 of 11" row (correct behaviour). Summary bullets ("Source x is QUALIFIED.") use raw enum case from the server. Pen shows "Path to sign-off" card, not implemented (no API data).

## Run Sign-off - minor gaps
- Fixed: Blockers list was an unstyled monospace list with raw enum names; now rows with a control tag. Run state shown as "Review required" instead of `REVIEW_REQUIRED`. A disabled "Sign off" button is shown when the action is not allowed (as in the pen), with an explanatory tooltip.
- Open: Preparer and required approver show "—" (null in demo data). "Return for investigation" is only shown when the server allows RETURN. VIEWER still sees "Request reprocess" and "Generate evidence pack" (allowedActions does not include them; contract should expose them). Attestation text contains the raw ruleset id (server text). Pen sidebar of "Evidence pack" checklist and "Open matters" tiles are implemented as tags and a table instead.

## Exceptions Queue - minor gaps
- Fixed: rows were very tall (severity, status and blocking pills stacked, narrow exception column). Status moved to its own column, owner and due merged into one "Owner / due" column (unassigned highlighted), redundant entity/date removed from each row, source column wraps.
- Open: filters are a two-row block (pen has one row of chips); KPI strip shows 8 cards, per-currency lines are cramped for the first card. Loading state for the whole page is a spinner card. VIEWER sees the same page without selection checkboxes (correct).

## Exception Detail - minor gaps
- Fixed: evidence edge relations were raw (`delivered_in`), now humanised.
- Open: the pen's "Evidence: position and eligible balances" panel with coverage minus obligation equals position is not implemented (generic evidence graph table instead); no "Linked records" list of related exceptions; "What happened" and "Investigation" repeat the same sentence because the API returns description and summary identically; assign control is always rendered (disabled for roles without permission).

## Checks
- Console: no errors on any of the four pages (APPROVER and VIEWER).
- `npm run build` and `npm test` pass (52 passed).
