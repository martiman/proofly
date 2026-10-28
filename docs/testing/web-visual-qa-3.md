# Web visual QA 3 (real browser)

Environment: Vite on :5213 against the demo API (run RUN-LE-BENCH-20261027-v1), viewport 1440x1100, tokens VIEWER/APPROVER/CONFIG_ADMIN/SECURITY_ADMIN/ANALYST (switched through sessionStorage `proofly.devToken`). Screenshots are not committed; they live under
/Users/mates/.claude/projects/-Users-mates-dev-androsoft-proofly--claude-worktrees-app-vue-ant-design-vite-39c172/f7307fad-4f1b-4d25-81ce-6dffe7c7f984/tool-results/ (mcp-Claude_Browser-blob-1793227753335-gy4zt3.jpg before, mcp-Claude_Browser-blob-1793227805679-wymt79.jpg after for Safeguarding; ...1793227897279-ql4cll.jpg Sources; ...1793227955359-jtb5dt.jpg Configuration; ...1793228008711-qm8zjf.jpg and ...1793228046269-4fgiww.jpg Evidence before/after; pen frames: mcp-pencil-blob-*.png in the same folder).

## Safeguarding Position (/safeguarding, frame db0Nk)
Deviations found / status
- Obligation and eligible tables mixed all currencies in one list with per-currency "View all lines" rows. FIXED: a selected currency (click a position card, "Selected" label as in the pen) drives both breakdown tables, headers "Obligation breakdown · EUR", "showing 10 of 800", one View all lines link.
- Eligible table overflowed horizontally (6 columns, scrollbar). FIXED: Timestamp folded under Balance, Inclusion+Reason merged into Treatment (pill + truncated reason with tooltip); no overflow.
- No eyebrow. FIXED (SAFEGUARDING · entity · date).
- Propose modal defaulted to first currency; FIXED to selected currency. Validation (amount, reason, evidence) verified in browser.
- Drawer (View all lines) with search and paging verified; loading and null-result states verified in code/network.
- OPEN: pen has "Compare with previous day" and "Request bank evidence" actions, an Independent/Recorded/Difference/Status obligation table and a total row; the API only returns amount per line (contract change needed for recorded/difference/status). Raw qualification strings (data completeness is Qualified, ACCOUNT_NOT_ELIGIBLE: ...) are shown unhumanised. Drawer truncates the exclusion reason (tooltip only).
- Console: no errors.

## Sources (/sources, frame xEUJW)
- FIXED: 404 console error on load (records requested with deliveryId literal `{deliveryId}` before a delivery was selected).
- FIXED: Findings list rendered ~150 ids; now 5 plus "showing 5 of N". FIXED: eyebrow with entity and date; kv rows top-aligned so pills and notes wrap cleanly.
- OPEN (contract): pen has ingestion channel/adapter/manual upload cards, pipeline strip, Owner and Downstream columns, per-check "Value totals" text and actions (Export manifest, Request corrected delivery, Compare versions); none exist in the API. Times show UTC (pen GMT).
- Modals (retry/supersede) not exercised: destructive.

## Configuration & Access (/settings, frame lUUo6)
- Demo data has no rules, sources, policies; empty tables showed AntD "No data" and a blank Freshness row. FIXED with specific empty text and "Not configured"/"None configured".
- FIXED: scope `*` shown as "All entities".
- FIXED: propose modal now validates target id, YYYY-MM-DD date and reason locally (JSON validation already worked; verified).
- OPEN: pen Users table has Allowed actions and Status columns, Pending change/Active versions cards, versioned history (RC-/RS- ids with approver/effective); API users lack allowed actions; pending-change card and decide modal not exercised (no pending data; mutations forbidden here). Sidebar for CONFIG_ADMIN greys out items (shell, not owned).

## Evidence & Audit Explorer (/evidence, frame vzX0v)
- Activity table was 2400px tall (wrapping time/actor/subject). FIXED: nowrap columns, human name or "System", ellipsis subject, internal scroll (560px) with sticky header.
- Pack card showed a huge 403 illustration for roles without evidence.export. FIXED to a one-line restricted message.
- Forbidden state (ANALYST) verified: restricted message; search and filter chips now hidden in that state. VIEWER/SECURITY_ADMIN see the list.
- OPEN: pen's 7-stage lineage strip (delivery, record, calculation, result, exception, sign-off, pack) is a table/edge list here and only for exception/record subjects; pen actions (Verify fingerprint) absent; StatePlaceholder 'forbidden' (shared) uses an oversized illustration; ruleset string in the Run cell wraps awkwardly.

## Verification
`npm run build` and `npm test` pass (57 tests; new: ConfigurationAccess/__tests__/proposeValidation.test.ts, Sources/__tests__/findingsNote.test.ts).
