# Product Capabilities

## Status convention

All capabilities in this document are **proposed**. The repository does not yet contain a working implementation.

Future updates should use only these status labels:

- **Proposed** - product intent; not available for use.
- **In development** - implementation work exists but is not released or validated for production use.
- **Implemented** - working, tested behaviour exists and the document links to the relevant code and release evidence.

The word "should" below expresses a requirement for a future implementation, not current behaviour.

## Capability map

| Area | Status | Intended outcome |
| --- | --- | --- |
| Data completeness | Proposed | Prove that the expected evidence population for a business date was received and is structurally usable. |
| Independent ledger verification | Proposed | Reconstruct client-money movements and balances independently, then compare them with recorded balances. |
| Matching and reconciliation | Proposed | Link related internal and external records using explainable, deterministic rules. |
| Transaction lifecycle verification | Proposed | Confirm that transactions pass through valid stages and reach expected outcomes. |
| Safeguarding coverage | Proposed | Compare client-money obligations with eligible external balances by currency and legal entity. |
| Exception management | Proposed | Turn control failures into owned, prioritised and traceable work. |
| Audit and evidence | Proposed | Preserve provenance, rule versions, run history and operator actions for reproduction and review. |
| Operational reporting | Proposed | Present daily results, trends and sign-off evidence to authorised users. |

## 1. Data completeness

**Status: Proposed**

The platform should establish the control perimeter before evaluating financial correctness.

Proposed functions:

- register expected sources, delivery windows, business calendars and owners;
- ingest banking transactions, card statements, transfer instructions, direct-debit records, internal ledger entries and balance snapshots;
- preserve the original source object and calculate a content fingerprint;
- validate schema, encoding, required fields, currencies, dates and decimal precision;
- detect missing files, missing business dates, sequence gaps, duplicates and unexpected record-count or value changes;
- reconcile source-level control totals where provided; and
- block or qualify downstream conclusions when completeness is not established.

The resulting completeness status should be visible independently from reconciliation status. A matched subset must not be presented as a complete population.

## 2. Independent ledger verification

**Status: Proposed**

The platform should build an independent representation of in-scope client-money movements rather than copying the primary ledger's conclusions.

Proposed functions:

- map source events to canonical debit and credit movements;
- reconstruct opening, movement and closing balances by client account and currency;
- apply explicit posting, value-date and sign conventions;
- compare independently calculated balances with internal ledger balances;
- detect missing, duplicated, misdated, mis-signed and unbalanced entries;
- aggregate differences by account, currency, product and legal entity; and
- retain the calculation inputs, rule version and result for reproduction.

## 3. Matching and reconciliation

**Status: Proposed**

The platform should relate records across sources without losing their individual provenance.

Proposed functions:

- exact matching using stable identifiers and reference mappings;
- deterministic composite matching using amount, currency, account, date and reference attributes;
- controlled one-to-one, one-to-many and many-to-one matching;
- explicit tolerance rules for date and amount where permitted;
- duplicate-use prevention so a record cannot satisfy incompatible matches;
- match confidence and reason codes that explain the rule used;
- unmatched, partially matched and conflicting populations; and
- controlled rematching after late or corrected evidence arrives.

## 4. Transaction lifecycle verification

**Status: Proposed**

The platform should evaluate transactions as journeys, not isolated rows.

Proposed functions:

- define allowed lifecycle states and transitions by transaction type;
- correlate instructions, ledger postings, external movements, settlement outcomes, returns and reversals;
- identify invalid transitions, missing stages, stalled states and outcomes outside their expected service window;
- distinguish successful completion, rejection, return, cancellation and reversal;
- verify that accounting and external money movements reflect the final outcome; and
- maintain a timeline of observed evidence for each transaction.

Initial lifecycle types should include bank transfers, card settlements and direct debits.

## 5. Safeguarding coverage

**Status: Proposed**

The platform should compare independently supported client-money obligations with eligible safeguarding balances.

Proposed functions:

- calculate the in-scope client-money obligation by currency and legal entity;
- ingest and validate external account balance evidence;
- classify included accounts and adjustments using versioned eligibility rules;
- prevent unsupported netting across currencies or legal entities;
- calculate surplus or shortfall for the business date;
- explain every component and adjustment in the calculation; and
- raise a high-severity exception when coverage is insufficient or cannot be established.

The platform should provide evidence for authorised control owners; the legal and policy definition of safeguarding eligibility remains an organisational responsibility.

## 6. Exception management

**Status: Proposed**

The platform should make every material failure an explicit operational record.

Proposed functions:

- create exceptions from completeness, ledger, matching, lifecycle and safeguarding controls;
- classify exceptions by control, reason, value, currency, age and severity;
- assign an owner and due time using configurable routing rules;
- record investigation notes, supporting evidence and linked records;
- support resolution, accepted difference and false-positive outcomes with mandatory rationale;
- require appropriate approval for material adjustments or overrides;
- reopen an exception when new evidence invalidates its resolution; and
- provide queues, ageing and breach reporting.

Exceptions should remain linked to the run and rule version that created them.

## 7. Audit and evidence

**Status: Proposed**

The platform should make historical results reproducible and operator activity accountable.

Proposed functions:

- immutable retention of source evidence or a verifiable reference to it;
- end-to-end data lineage from source record to normalised record, match, control result and exception;
- versioned schemas, mappings, matching rules and control definitions;
- append-only history for status changes, notes, overrides and approvals;
- authenticated user identity and role-based authorisation;
- run manifests containing inputs, versions, timestamps and output totals; and
- exportable evidence packs for a selected business date or control.

Retention periods, encryption standards and access policies should be configured to meet the deploying organisation's obligations.

## 8. Operational reporting

**Status: Proposed**

Proposed functions:

- daily control dashboard by business date, legal entity and currency;
- completeness, reconciliation, lifecycle and safeguarding summaries;
- drill-down from aggregate results to source evidence;
- exception backlog, ageing and ownership views;
- run status, reprocessing history and sign-off status;
- trend reporting without rewriting historical results; and
- export of human-readable and machine-readable control evidence.

## Cross-cutting requirements

Any implementation should also provide:

- tenant and legal-entity isolation appropriate to the deployment model;
- encryption in transit and at rest;
- least-privilege access and separation of duties;
- deterministic, idempotent ingestion and processing;
- explicit handling of business dates, time zones and currencies;
- observability for job status, latency, failures and data-quality changes;
- controlled configuration deployment and rollback; and
- enough capacity to process the initial target of 10,000-100,000 daily transactions and 10,000-100,000 client accounts within the agreed T+1 window.
