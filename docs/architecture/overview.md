# Architecture Overview

## Document status

This is a **proposed architecture**, not a description of implemented software. The repository currently contains no application components. Technology choices, service boundaries and deployment topology remain open until validated through the MVP.

## Architectural objective

Proofly should provide an independent, reproducible T+1 control cycle for client money. The architecture must preserve source evidence, make financial calculations deterministic and support controlled reprocessing without erasing earlier results.

The initial scale target is 10,000-100,000 financial transactions per day and 10,000-100,000 client accounts.

## Logical flow

```text
Source systems and external evidence
  |-- Banking transactions and balances
  |-- Card transaction events and settlements
  |-- Transfer instructions
  |-- Direct-debit instructions and outcomes
  |-- Internal ledger entries and balances
  v
Ingestion and source evidence store
  v
Validation and completeness controls
  v
Canonical financial data model
  |---------------------------|
  v                           v
Independent ledger       Matching and lifecycle engine
  |                           |
  |-------------|-------------|
                v
       Safeguarding calculation
                v
      Control results and exceptions
                v
       Review, sign-off and evidence
```

## Proposed components

### 1. Source adapters

Source adapters should accept scheduled files or controlled API extracts. Each delivery should be associated with a source, business date, extraction timestamp and expected schema. Card-processor adapters should acquire transaction-event and settlement extracts as separate, linkable populations.

Adapters should not contain material control logic. Their role is to acquire evidence and preserve enough source metadata to prove where it came from.

### 2. Source evidence store

The evidence store should retain every accepted source object, its cryptographic fingerprint and ingestion metadata. Evidence should be immutable; corrections should arrive as new versions rather than overwrite previous inputs.

Access to raw financial evidence should be restricted and audited. The storage design should support the organisation's retention and deletion obligations without weakening the integrity of retained control results.

### 3. Validation and completeness service

This component should verify delivery schedules, structural validity, sequence continuity, uniqueness and source control totals. It should produce explicit completeness results before downstream controls are treated as conclusive.

### 4. Canonical financial model

Normalisation should map source-specific fields into a stable internal model while preserving a link to the original record.

Initial entities are expected to include:

- source delivery and source record;
- client account and external financial account;
- transaction instruction, card transaction and lifecycle event;
- ledger entry and balance snapshot;
- external transaction, card event and settlement record;
- match group and match decision;
- control run and control result;
- safeguarding calculation and adjustment; and
- exception, action and approval.

Money should be represented using fixed decimal precision plus an explicit currency. Business date, value date, booking timestamp and source time zone should remain distinct.

### 5. Independent ledger engine

The ledger engine should reconstruct account movements and closing balances from canonical events using versioned posting rules. It should not import the primary ledger's calculated closing balance as its own conclusion.

Outputs should include per-account movement totals, calculated balances, comparison differences and a calculation trace.

### 6. Matching and lifecycle engine

This engine should execute versioned, deterministic rules to relate instructions, ledger entries and external evidence. It should support one-to-one and controlled grouped matches, enforce record-use constraints and produce an explanation for every match decision. For cards, it should preserve the event chain and support many-to-one links from presentments to processor settlements and from settlements to external bank movements.

Lifecycle evaluation should operate over the resulting transaction timeline and identify missing, invalid, late, returned or reversed stages. Card timelines should distinguish authorisation, presentment, reversal, settlement, refund and chargeback events without treating a settlement batch as an individual card transaction.

### 7. Safeguarding engine

The safeguarding engine should combine independently supported client obligations with eligible external balance evidence. Calculations should be isolated by legal entity and currency unless an explicit, authorised rule permits otherwise.

Every included balance, exclusion and adjustment should be visible in the calculation trace.

### 8. Exception and workflow service

Failed controls should create durable exceptions with severity, value, reason, owner and due time. Workflow activity should append to history. Resolution or override should never delete the originating result.

### 9. Control API and user interface

An authenticated API should expose run status, control results, evidence lineage and exception operations. The user interface should provide daily oversight, investigation and sign-off workflows without bypassing authorisation rules.

The proposed user experience is documented in [Product Screens](../product/screens/README.md).

### 10. Audit log and evidence export

The audit capability should record security-relevant and control-relevant actions. Evidence export should assemble the inputs, configuration versions, calculations, exceptions and approvals needed to review a business date.

## Processing model

### Business-date runs

Each control run should be identified by at least:

- deployment or tenant;
- legal entity;
- business date;
- run version; and
- ruleset version.

A run should progress through explicit states such as awaiting evidence, validating, calculating, review required, completed or failed. Completion should require defined mandatory controls to reach an acceptable state and, where configured, human sign-off.

### Idempotency and reprocessing

Ingestion and calculations should be idempotent. Replaying the same source object and ruleset must not create duplicate financial events or divergent results.

Late or corrected evidence should produce a new run version. Previous results, exceptions and sign-offs should remain available, with a clear relationship between the superseded and current versions.

### Rule versioning

Schemas, mappings, matching rules, lifecycle definitions, tolerances and safeguarding policies should be versioned. A historical run must retain the exact versions it used.

## Independence boundaries

Proofly's conclusions are strongest when evidence is obtained as close as practical to its authoritative source. For example, external bank evidence should not be sourced solely from the internal ledger being verified.

Independence also requires separation between:

- original evidence and normalised data;
- configured rules and calculated results;
- machine-produced results and human overrides; and
- current state and immutable history.

## Security and control considerations

A future implementation should include:

- authenticated service and user identities;
- least-privilege role-based access;
- separation between configuration, operation and approval privileges;
- encryption in transit and at rest;
- secret management outside source code;
- tamper-evident audit history;
- data minimisation and configurable retention;
- tokenised card references in the canonical model, with no requirement to ingest full card numbers, security codes or PIN data;
- operational monitoring and alerting; and
- tested backup, recovery and controlled deployment procedures.

These are architectural requirements, not claims about the current repository.

## Initial deployment approach

The MVP should favour a modular application with clear internal boundaries and independently testable processing stages. This keeps operational complexity proportionate while preserving a path to separate high-load or high-risk components later.

The first deployment should use a relational store for canonical records, results and workflow; durable object storage for source evidence and exports; and an asynchronous job mechanism for business-date processing. Specific products should be selected only after the target environment, security constraints and operational ownership are known.

Proofly should include backend services, but they do not need to run as a central multi-tenant SaaS. Development and evaluation can run the complete stack on one machine. Production should initially run the same web, API, worker and data-service boundaries inside customer-controlled infrastructure.

See [Deployment Model](deployment-model.md) and [Repository Structure](repository-structure.md).

The current architectural decisions are recorded in:

- [ADR 0001: Customer-hosted Backend](decisions/0001-customer-hosted-backend.md); and
- [ADR 0002: Deterministic oMVP](decisions/0002-deterministic-omvp.md).

## Open decisions

- supported ingestion protocols and source authentication methods;
- canonical identifiers and account hierarchy;
- required currencies, calendars and time-zone rules;
- jurisdiction-specific safeguarding configuration;
- evidence retention and deletion policy;
- recovery objectives and availability target;
- permitted matching tolerances and approval thresholds;
- deployment isolation model; and
- performance deadline for the completed T+1 run.
