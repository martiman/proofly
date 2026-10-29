# Daily Control Cycle

## Status

This document defines the **proposed oMVP operating workflow**. It is not implemented.

## Objective

For each legal entity and business date, Proofly should produce a reproducible T+1 conclusion showing whether:

- all required evidence was received;
- client balances were independently reconstructed and compared;
- transactions reached supported lifecycle outcomes;
- safeguarding obligations were covered; and
- every material break was owned or explicitly approved before sign-off.

## Actors

- **Control analyst** monitors processing and investigates exceptions.
- **Control owner** reviews material matters and signs off the completed run.
- **Configuration administrator** maintains approved source and control configuration.
- **Read-only reviewer** inspects results and evidence without changing them.

## Workflow

### 1. Open the business-date run

The system creates a run for the configured legal entity and business date. The run records its software version, ruleset version, expected sources and responsible control owner.

Initial state: `awaiting_evidence`.

### 2. Receive source evidence

Expected banking, card, transfer, direct-debit, ledger and balance deliveries are ingested. Each delivery receives:

- a stable delivery identifier;
- source and business date;
- received timestamp;
- schema version;
- content fingerprint;
- record count and source control totals; and
- immutable storage reference.

A repeated identical delivery is treated idempotently. A corrected delivery creates a new delivery version and does not overwrite the original.

Provider feeds, such as the FIS card-processor files, follow the same rules. Each feed is registered as its own expected delivery with an owner, delivery window and schema version. The source adapter maps the provider layout to the common contract and the original file is preserved unchanged. A missing or late FIS delivery qualifies the card controls in the same way as any other mandatory source.

### 3. Establish completeness

Proofly checks expected deliveries, schemas, required fields, duplicates, sequences and source totals. Each mandatory source receives a completeness result.

If mandatory evidence is missing or rejected, downstream controls may run for diagnosis, but the overall run cannot receive an unqualified pass.

### 4. Normalise records

Accepted source records are converted into the canonical model while preserving source provenance. Currency, amount precision, account identity, booking date, value date and source timestamps remain explicit.

Mapping failures create data-quality exceptions.

### 5. Run deterministic controls

The configured ruleset executes:

1. independent ledger reconstruction;
2. matching and reconciliation;
3. transaction lifecycle verification; and
4. safeguarding coverage calculation.

Every control result records its inputs, rule version, output totals and reason codes.

### 6. Create and route exceptions

Failed controls create exceptions with severity, financial value, currency, legal entity, business date, reason code, owner and due time.

The control analyst investigates each material exception using linked evidence and calculation traces. Investigation notes and status changes append to history.

### 7. Resolve or qualify breaks

An analyst may propose:

- resolved with supporting evidence;
- accepted difference with rationale;
- false positive caused by an approved control limitation; or
- waiting for additional evidence.

Where configured, a control owner must approve material resolutions, accepted differences and adjustments. The proposer must not approve their own action when separation of duties applies.

### 8. Review safeguarding position

Proofly calculates client-money obligation and eligible safeguarding balances by legal entity and currency. Adjustments are explicit and versioned.

A shortfall, missing balance or unprovable position creates a high-severity exception and blocks unqualified sign-off.

### 9. Prepare sign-off

The sign-off view shows:

- completeness of every mandatory source;
- state of every mandatory control;
- unresolved and accepted exceptions;
- safeguarding position;
- approved qualifications;
- run and ruleset versions; and
- evidence-pack identifier.

### 10. Sign off

An authorised control owner signs the run with an explicit attestation. The sign-off is immutable and linked to the exact run version.

Final state: `signed_off` or `signed_off_with_qualifications`.

### 11. Handle late or corrected evidence

Evidence arriving after sign-off never changes the signed result in place. Proofly creates a new run version, records why reprocessing started and links the previous and current versions.

If the new result materially changes the conclusion, the prior sign-off is marked superseded or invalidated according to policy and a new sign-off is required.

## Run states

```text
awaiting_evidence
  -> validating
  -> calculating
  -> review_required
  -> ready_for_sign_off
  -> signed_off | signed_off_with_qualifications

Any processing state may enter failed.
Late or corrected evidence creates a new run version.
```

## Required evidence at completion

- run manifest;
- source-delivery manifest;
- completeness results;
- control results and calculation traces;
- exception population and histories;
- safeguarding calculation;
- approvals and qualifications; and
- sign-off record.

## oMVP boundary

The oMVP supports one complete daily cycle using controlled file-based sources. Real-time blocking, payment execution, autonomous source correction, AI-assisted controls, agents and MCP are out of scope.

