# Web end-to-end command flows (issue #58)

First execution of the web command flows through the real UI against a real backend (previously only mocked-fetch unit tests).

## Setup

- Scratch database `proofly_e2e` (PostgreSQL localhost:55432), seeded with `Proofly.Benchmarks --seed-dev --scale 0.02`: run `RUN-LE-BENCH-20261027-v1`, 84 exceptions, 11 of them blocking.
- API on port 5090 (`Auth__Mode=Local`, own signing key, CORS for `http://localhost:5214`), Proofly.Worker started for `run.process` and evidence packs, Vite on 5214 (`PROOFLY_API_PORT=5090`).
- Roles were switched per browser tab through `sessionStorage['proofly.devToken']`. The UI was driven with DOM events in the built-in browser; every command went through the real Vue code path, Vite proxy and API. Verification used the network log, curl and psql.
- Seed gaps compensated in the scratch DB only: no `config_rules` (inserted `MATCH-AMOUNT-TOLERANCE`); there is only one user per role, so for separation-of-duties tests a second role was granted temporarily (`local-analyst` + APPROVER, `local-security-admin` + CONFIG_ADMIN) and revoked or left in the dropped DB.
- The shared demo backend (port 5080, `proofly_web`) was not touched. The whole environment was removed afterwards (ports 5090 and 5214 free, database dropped, evidence dir removed).

## Results

| # | Flow | Result |
|---|------|--------|
| 1 | Analyst: assign, note, request evidence, propose resolution | PASS after fix D1 |
| 2 | Same analyst cannot approve | PASS |
| 3 | Approver approves / rejects, stale proposal | PASS after fix D2 |
| 4 | Safeguarding adjustment propose / approve / stale marker | PASS |
| 5 | Manual match propose / approve, record-already-used | PASS after fix D3 |
| 6 | Sign-off: blockers, submit, SoD, stale, return, approve | PASS after fix D4 |
| 7 | Reprocess with reason | PASS, open defect O1 |
| 8 | Evidence pack generate / download / chain | PASS |
| 9 | Config change, per-kind permission, effective date | PASS |
| 10 | Idempotent double submit | PASS |

### 1. Analyst exception flow
- Assign: `GET /legal-entities/LE-BENCH/assignable-users` 200, `POST /exceptions/{id}/assign` 200, owner shown. Note: `POST .../notes` 200. Request evidence: `POST .../request-evidence` 200, status `WAITING_FOR_DATA` (banner "Waiting for data").
- Propose RESOLVED with reason and evidence ref `dlv-EXC-02EED22A2C643917` through the confirm dialog: `POST .../resolution-proposals` 200, status `PENDING_APPROVAL`, `pendingApproval.proposalVersion=1`, `requiredRole=APPROVER`, `canDecide=false`.
- DB: timeline has system, assign, note, evidence request and `RESOLUTION_PROPOSED` rows.

### 2. Self-approval
- UI as analyst: pending card without Approve/Reject buttons (text "You cannot decide this proposal"). API as analyst: 403 forbidden (no `exception.decide`).
- With temporary dual role (analyst + APPROVER) the API returns 403 `separation-of-duties`: "The proposer cannot decide their own resolution proposal."; `canDecide=false` and `allowedActions` has no approve entry. The same user got 403 `separation-of-duties` for run sign-off approve ("The preparer cannot sign off their own submission.").

### 3. Approver decisions
- Approve in UI: `POST .../resolution-proposals/RP-.../decision` 200; exception `RESOLVED`, `approvals[-1].status=APPROVED`, approver `local-approver`; audit events `RESOLUTION_PROPOSED` (analyst) then `RESOLUTION_DECIDED` (approver, reason recorded). The original finding is unchanged (md5 over title, control, reason_code, severity, value, source row, finding_key identical before and after).
- Reject in UI (EXC-05F19651222D71FF): decision `REJECT` 200, exception back to `OPEN`, approval `REJECTED`.
- Stale: approver page loaded with proposal v1, analyst proposed v2 (EXC-070DCBD4234909D2), approver clicked Approve: 412 (If-Match) and the UI shows the warning "This exception changed since you opened it" with a Reload button. Direct API with a fresh ETag: old proposal id gives 409 `invalid-state` ("INVALIDATED"); new id with `proposalVersion=1` gives 409 `stale-proposal` ("reviewed version 1, current 2").

### 4. Safeguarding adjustment
- Analyst modal (EUR, -1250.50, reason, evidence `STATEMENT-EUR-001`, expiry 2026-11-15): propose 200, row `PROPOSED`. Approver Approve in modal: 200, row `APPROVED`, history lines for both users.
- Position read model: `positions[EUR].stale=true` and the UI shows the stale marker; message "Adjustment approved. Reprocess the run to include it in the result." `approvedAdjustments` stays 0.00 until reprocess (see O1).

### 5. Manual match
- Analyst picked BNK-1057 (JPY 170,235) and TRF-19 (JPY 153,680) from the unmatched population: `POST /runs/{run}/match-groups` 200, group `MM-E8CB8194AD8E` `PENDING_APPROVAL`. First attempt with two left-side records (TRF-6 + LED-6) correctly surfaced the server validation "rightTransactionIds: The array must have at least 1 item(s)".
- Approver: after fix D3 the pending group is opened from the group list, Approve match 200: group `MATCHED`, the three exceptions of BNK-1057 `RESOLVED`.
- Re-proposing a used record: 409 `record-already-used` "Records already used: BNK-1057 in group MM-... (MATCHED); TRF-19 ...". Cross-currency pairs: 422 with a field pointer.

### 6. Sign-off
- With 11 blocking exceptions: Sign off disabled; `blockerDetails` link each exception (`/exceptions/EXC-...`); approve via API 409 `invalid-state` ("must be submitted first").
- All 11 blockers were resolved through the API (analyst proposes, approver approves). Analyst "Submit for sign-off" in the UI: `APPROVAL_PENDING`, preparer set. Analyst API approve: 403 forbidden; dual-role analyst: 403 `separation-of-duties`.
- Stale: after submission an exception changed state; the approver page (loaded before) clicked Sign off with attestation: 409 `stale-signoff`, the UI shows "Sign-off is stale: ..." with Reload; after Reload the run is `PREPARATION_INCOMPLETE`, history shows "Sign-off attempt refused as stale". Also when `changesSincePreparation` is non-empty the server drops APPROVE from `allowedActions`.
- Return: approver "Return to preparer" with reason: state `RETURNED`, preparer can submit again. Final: re-submit by analyst, approver Sign off with checkbox attestation and reason: `run_signoffs` row `SIGNED_OFF` (preparer local-analyst, approver local-approver), audit `RUN_SIGNED_OFF`.

### 7. Reprocess
- Approver "Request reprocess" with reason on the signed-off v1: 202, `RUN-...-v2` `PROCESSING` until the worker ran, then `READY_FOR_SIGN_OFF`/`READY_FOR_REVIEW`; v1 `SUPERSEDED`; audit `RUN_REPROCESS_REQUESTED`, `RUN_STARTED`. v2 needs a new sign-off (`allowedActions=[SUBMIT]`). 16 earlier resolutions were continued to v2 (`predecessor_exception_id`), the 3 pending proposals came back as `OPEN`.

### 8. Evidence pack
- "Generate evidence pack" 202, pack `EP-5f61...` polled until `GENERATED`. Download button: `GET .../evidence-pack/download` 200, blob 227,205 bytes `application/zip`, SHA-256 computed in the browser equals the value shown and the API/DB value (`ff6b1841...d847`). Audit chain row: "OK, 255 event(s) checked, chain intact"; API `auditChain.valid=true`.

### 9. Config change
- CONFIG_ADMIN proposed `MATCH-AMOUNT-TOLERANCE` 0.00 GBP to 0.50 GBP through the Propose dialog: `PENDING_APPROVAL`. SECURITY_ADMIN approve: 403 "Deciding a configuration change needs settings.approve" (only CONFIG_ADMIN holds it; `USER_ACCESS` changes need `access.manage` per the endpoint code, not exercised end to end). CONFIG_ADMIN self-approve: 403 `separation-of-duties`; the Approve button is disabled (`canDecide=false`).
- With a second CONFIG_ADMIN (security admin granted the role), approve in the inbox UI: change `ACTIVE`, rule version v2, audit events `CONFIG_CHANGE_PROPOSED` / `CONFIG_CHANGE_DECIDED`.
- Effective date: that change had `effectiveFrom=2026-10-29` while the server's UTC date was still 2026-10-28, so runs v3 and v4 correctly kept ruleset v1 (tolerance 0.00 GBP). A second change with `effectiveFrom=2026-10-28` approved, reprocess: run v5 on ruleset v3, `match_groups.tolerance = 0.75 GBP`; earlier runs untouched.

### 10. Idempotency
- Three synchronous clicks on "Add note": 3 POSTs left the browser (no client-side in-flight guard within the same tick), 1x 200 and 2x 409, exactly one note row. Same `Idempotency-Key` replayed with curl: 200 twice, one new note row.

## Defects

### Fixed (web)
- D1 ExceptionDetail: owner options came from `GET /settings` (403 for analysts), so Assign was unusable for the role that needs it. Now uses `listAssignableUsers(exception.legalEntityId)`.
- D2 ExceptionDetail: decision used the approval id (`AP-...`) as `proposalId`, so every approve/reject returned 404 "Not found". Now `proposalIdOf()` returns `objectId` (`RP-...`); unit test added in `ExceptionDetail/__tests__/logic.test.ts`.
- D3 MatchingWorkbench: the workbench payload for a focused record never contains pending manual groups, so a group opened from the list showed the original unmatched group and the approver could never reach Approve/Reject match. The group clicked in the list is now pinned and re-read after a command.
- D4 RunSignOff: "Request reprocess" and "Generate evidence pack" were shown to all roles (analyst got 403). Now gated by `run.reprocess` / `evidence.export` from `/me`.

### Open
- O1 (backend) Approved safeguarding adjustments are bound to the run id and are not carried to the reprocessed run: `RunInputLoader` reads `safeguarding_adjustments WHERE run_id = @r`, so v2 shows `approvedAdjustments=0.00` and the UI message "Reprocess the run to include it" is not true. Needs a decision (move or copy open and approved adjustments to the new run).
- O2 (backend) `GET /runs/{run}/matching/workbench` resolves a single group per focus, so pending manual proposals are invisible there; worked around in the web (D3). Better to include the manual group.
- O3 (seed) No `approval_policy`/`rule` config rows in the dev seed: the exception card shows "Approval policy: none" while an APPROVER approval is required (it says `adr-0005` once pending), and the Control parameters tab is empty.
- O4 (web, not verified) EvidenceAudit "Regenerate evidence pack" is probably also shown without `evidence.export` (not checked as analyst; file is owned by the visual QA work).
- O5 (web, cosmetic) Sign-off page shows the info message "Sign-off is not available to you for this run in its current state." next to every success toast; no in-flight lock for rapid clicks (server dedupes with 409, harmless).
- O6 (contract nit) Money strings mix scales (`0.00` vs `1081977.250000`) in the safeguarding payload.
- O7 (web) Evidence options in the propose-resolution form are evidence-graph node ids (`dlv-EXC-...`), not evidence records; fine for the fixture, worth revisiting.
- Environment note: the Browser pane tab is shared; another agent navigated the first tab to port 5213 mid-run, so dedicated tabs were created.
