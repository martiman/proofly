# Minimum Viable Product

## Status

This document defines a **proposed MVP**. Nothing in this scope is currently implemented. The MVP should be considered delivered only when the acceptance criteria below are demonstrated with representative data and linked to a released version.

## MVP objective

Prove that Proofly can complete one explainable, reproducible T+1 control cycle for client money at the intended initial scale.

For a selected legal entity, set of accounts and business date, the MVP should:

1. establish that all required evidence was received;
2. independently reconstruct client-account balances;
3. match transaction instructions, ledger entries and external movements;
4. verify the expected transaction lifecycle;
5. calculate safeguarding coverage by currency;
6. create actionable exceptions for every material break; and
7. produce a reviewable evidence pack.

## Target operating envelope

The MVP should be tested with representative volumes within:

- 10,000-100,000 financial transactions per day; and
- 10,000-100,000 client accounts.

The required completion time within the T+1 window should be agreed before performance acceptance testing. No throughput claim should be made until that test has passed.

## In-scope evidence

The MVP should support controlled file-based ingestion for:

- transfer instructions and status history;
- direct-debit instructions and outcomes;
- internal ledger entries;
- internal client-account closing balances;
- banking transactions and closing balances; and
- card statements or settlement records where they are part of the selected pilot flow.

Each input should have a documented schema, source owner, business-date rule and expected delivery window.

## In-scope capabilities

### 1. Run and source management

- create a control run for a legal entity and business date;
- register the inputs and ruleset used by the run;
- retain original evidence with fingerprints and ingestion metadata;
- show missing, received, rejected and superseded deliveries; and
- support deterministic reprocessing as a new run version.

### 2. Completeness controls

- validate required files, required fields and supported data types;
- detect duplicate files and records;
- compare record counts and value totals with source control totals where available;
- detect sequence or business-date gaps where the source provides the necessary identifiers; and
- prevent an incomplete population from receiving an unqualified pass.

### 3. Independent ledger verification

- translate supported events into canonical debit and credit movements;
- calculate closing balances from an agreed opening position and in-period movements;
- compare calculated balances with the internal ledger by client account and currency; and
- explain differences down to the contributing records.

### 4. Matching

- exact identifier matching;
- deterministic composite matching using configured attributes;
- one-to-one and controlled one-to-many matches;
- prevention of incompatible reuse of matched records; and
- explainable unmatched and partially matched results.

### 5. Lifecycle verification

- configured state models for the selected transfer and direct-debit flows;
- correlation of instruction, internal posting and external outcome evidence;
- detection of missing, invalid, late, returned and reversed stages; and
- a chronological evidence view for an individual transaction.

### 6. Safeguarding coverage

- calculate the supported client-money obligation by legal entity and currency;
- ingest eligible external account balances;
- record explicit, approved adjustments;
- calculate surplus or shortfall without unsupported cross-currency netting; and
- create a high-severity exception for a shortfall or an unprovable position.

### 7. Exception workflow

- create an exception with type, reason, value, severity and source links;
- assign an owner and due time;
- add investigation notes and evidence references;
- resolve, accept or reject with rationale and approval where required;
- retain complete status history; and
- show open exceptions by age, severity and owner.

### 8. Daily review and evidence

- show the status of completeness, ledger, matching, lifecycle and safeguarding controls;
- drill from summary results to records and source evidence;
- record control-owner sign-off;
- preserve superseded run versions and their sign-offs; and
- export a business-date evidence pack containing the run manifest, control totals, results, exceptions and approvals.

## Required non-functional behaviour

- financial amounts use fixed decimal precision and explicit currencies;
- processing is deterministic and idempotent;
- all records retain source provenance;
- rules and mappings are versioned;
- access is authenticated and role-based;
- sensitive evidence is encrypted in transit and at rest;
- material actions and configuration changes are audited;
- failures are observable and recoverable without silent data loss; and
- representative maximum-volume processing completes within the agreed T+1 deadline.

## Roles for the MVP

- **Control analyst** - reviews results, investigates and resolves assigned exceptions.
- **Control owner** - approves material resolutions and signs off a business-date run.
- **Configuration administrator** - manages versioned schemas, mappings and rules but cannot approve their own control outcomes.
- **Read-only reviewer** - inspects results and evidence without changing them.

Separation-of-duties rules should prevent one user from making and approving the same material adjustment.

## Acceptance criteria

The MVP is accepted only when all of the following are demonstrated:

1. A representative business date can be ingested without manual database changes.
2. Missing or duplicated evidence is detected and visibly qualifies the run result.
3. Independent closing balances are reproduced and differences can be traced to source records.
4. Supported transactions are matched across instruction, ledger and external evidence with recorded reason codes.
5. Invalid, missing, delayed, returned and reversed lifecycle outcomes in the test set are identified.
6. Safeguarding surplus or shortfall is calculated by legal entity and currency from traceable components.
7. Control failures create exceptions with ownership, severity and history.
8. Reprocessing the same inputs and rules produces identical financial results without duplicate records.
9. Corrected evidence creates a new, linked run version without erasing the previous result.
10. An authorised reviewer can reproduce and sign off the run using the exported evidence.
11. Role and separation-of-duties checks are demonstrated.
12. The agreed representative maximum volume completes within the agreed T+1 processing deadline.

## Out of scope for the MVP

- payment initiation, approval or blocking;
- replacement of the primary accounting ledger;
- autonomous correction of source records;
- real-time transaction monitoring;
- unrestricted user-defined scripting in production;
- unsupported cross-currency or cross-entity netting;
- predictive risk scoring; and
- broad customisation beyond the selected pilot flows and control rules.

## Suggested delivery sequence

1. **Evidence foundation** - source registry, immutable ingestion, validation, run manifests and canonical records.
2. **Financial verification** - independent ledger calculation, exact matching and account-level differences.
3. **Journey controls** - composite matching and lifecycle verification for selected flows.
4. **Coverage and workflow** - safeguarding calculation, exception queues, approvals and sign-off.
5. **Operational proof** - evidence export, recovery testing, access-control validation and representative-volume testing.

## Exit decision

After the MVP, the product should proceed only if it shows that independent calculations materially improve confidence, exceptions are explainable enough for daily operations, evidence can be reproduced, and the full run can complete reliably within the required T+1 window.
