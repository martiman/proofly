# ADR 0005: MVP product decisions

## Status

Accepted by the product owner on 2026-10-28 with the guidance: choose the simplest option that is safe for production and can grow later; the MVP is not a full B2B solution.

## Principle

Keep behaviour fixed and explicit in the MVP, keep the data model and permissions granular enough to extend later, and never weaken the control guarantees (separation of duties, immutable signed runs, audit of every material decision).

## Decisions

| Issue | Decision |
|---|---|
| #2 Approver roles | Fixed roles bundle granular permissions (`x-required-permission` stays granular). MVP roles: `ANALYST` (investigate, propose), `APPROVER` (decide resolutions, adjustments, manual matches, sign-off), `CONFIG_ADMIN` (propose and approve configuration changes, not the same person as proposer), `SECURITY_ADMIN` (users, roles, access), `VIEWER` (read only). The mock `ADMIN` role is dropped. Per-user permission grants are later work. |
| #3 Approval policy | Fixed rules, no thresholds in MVP: every resolution of a blocking exception, every safeguarding adjustment, every manual match and every run sign-off needs approval by a different user holding `APPROVER`. The proposer can never approve. The rule set has a version recorded on each approval. Value and severity thresholds become configuration later. |
| #4 Money precision | Amounts are decimal strings with currency. Storage is `NUMERIC(38,6)` and `decimal` in code; calculations never round. Display uses the ISO 4217 minor units of the currency. Input with more than 6 decimal places is rejected. A test covers 0-, 2- and 3-decimal currencies. |
| #5 Run qualification | MVP: any unresolved blocking exception forbids sign-off; there are no qualified sign-offs. Non-blocking open exceptions are listed in the attestation. The `qualifications` field stays in the contract but is always empty. Qualified sign-off is a later feature. |
| #6 Reprocess trigger | Manual only: an authorised user requests a new run version with a reason. A late or superseding delivery creates a warning on the run, not an automatic reprocess. Signed runs are never changed. |
| #7 Saved views | Personal only (owner-private, max 50 per user). The `shared` field stays in the contract but the server rejects `shared: true` in the MVP. |
| #8 Bulk assign | Maximum 200 items per call; response `207` with per-item results; a failed item does not roll back the others. |
| #9 Manual match approval | Every manual match needs approval by a different `APPROVER` (no value threshold in the MVP). The match stays `PENDING_APPROVAL` and does not clear exceptions until approved. |
| #10 Configuration activation | Approved changes (always four-eyes) take effect from the next run, never retroactively; each run records the ruleset version it used. |
| #61 EUR/GBP | The synthetic data (EUR and CZK) is the source of truth. Design frames and screenshots that show GBP are updated to EUR and CZK; no application code changes. |

## Consequences

- Update `docs/security/roles-and-approvals.md` and the OpenAPI roles/permissions to match the role list; remove `ADMIN` and make `shared: true` invalid for saved views (follow-up on #1).
- Tasks blocked by these decisions can start; thresholds, shared views, qualified sign-off and automatic reprocessing are deliberately deferred and can be added without changing the stored data.
