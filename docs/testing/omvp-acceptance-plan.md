# oMVP Acceptance Plan

## Status

Proposed acceptance framework. Numeric deadlines and thresholds marked TBD must be agreed before pilot execution.

## Acceptance objective

Demonstrate that Proofly can complete one reproducible T+1 client-money control cycle with representative data, governed exception handling and evidence-backed sign-off.

## Test population

The acceptance dataset should include:

- 10,000-100,000 financial transactions for one business date;
- 10,000-100,000 client accounts;
- at least two currencies;
- transfer and direct-debit lifecycles;
- internal ledger entries and balances;
- banking transactions and safeguarding balances; and
- known injected failures with expected results.

Synthetic or properly authorised de-identified data should be used before any production pilot.

## Functional scenarios

### Evidence and completeness

- all expected sources arrive and pass validation;
- one mandatory source is missing;
- a duplicate delivery is received;
- record and value control totals disagree;
- a corrected delivery supersedes an earlier version; and
- malformed records remain visible in the completeness denominator.

### Independent ledger

- all account balances agree;
- one movement is missing;
- one movement is duplicated;
- one sign is incorrect;
- opening balance is unsupported; and
- recorded closing balance is absent.

### Matching and lifecycle

- exact one-to-one match;
- valid grouped match;
- ambiguous candidates remain unmatched;
- returned direct debit has a correct reversal;
- returned direct debit lacks a reversal;
- transaction remains pending inside its service window; and
- transaction exceeds its service window.

### Safeguarding

- covered position with surplus;
- shortfall;
- missing external balance;
- stale external balance;
- unapproved adjustment is excluded; and
- currency positions remain isolated.

### Workflow and governance

- exception is created, assigned, investigated and resolved;
- material resolution requires independent approval;
- proposer cannot approve their own restricted action;
- unauthorised user cannot view or change another legal entity;
- run cannot sign off with an unresolved blocking control; and
- signed run remains immutable after corrected evidence creates a new version.

## Determinism and reproducibility

- Reprocessing identical evidence with the same ruleset produces identical financial results.
- Duplicate ingestion does not duplicate canonical records.
- Every material total links to contributing records.
- A historical evidence pack identifies exact source fingerprints and rule versions.
- Restoring from backup preserves evidence references and run history.

## Performance

Agree before pilot:

| Measure | Target |
| --- | --- |
| Maximum representative daily records | 100,000 transactions |
| Maximum representative client accounts | 100,000 accounts |
| Source-ingestion deadline | TBD |
| Complete-run deadline within T+1 | TBD |
| Interactive screen response for standard queries | TBD |
| Evidence-pack generation time | TBD |
| Concurrent pilot users | TBD |

No performance claim should be published until the representative test passes in the target deployment environment.

## Reliability and recovery

- interrupted ingestion resumes or restarts without duplicate records;
- failed control jobs can be retried idempotently;
- service restart does not lose queued work or approvals;
- database and evidence-store restore is demonstrated; and
- failure states are visible without inspecting internal storage.

## Security and access

- authentication and session expiry work as configured;
- role and legal-entity scope are enforced by the API;
- source credentials are not exposed to browser users or logs;
- raw-evidence access and exports are audited;
- configuration and sign-off separation of duties is enforced; and
- development and production data stores are isolated.

## Usability

Representative analysts should complete without database access:

1. identify a failed daily control;
2. find the contributing evidence;
3. assign and investigate an exception;
4. propose a resolution;
5. obtain required approval; and
6. prepare the run for sign-off.

Observed usability failures become product issues before pilot acceptance.

## Exit criteria

The oMVP is accepted when:

- all blocking functional scenarios pass;
- expected injected failures are detected correctly;
- financial calculations are reproducible;
- agreed performance targets pass;
- recovery and role controls are demonstrated;
- a control owner can sign off using only Proofly evidence; and
- open limitations are documented and explicitly accepted.

