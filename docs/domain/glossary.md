# Domain Glossary

## Status

This glossary defines proposed oMVP terminology. Implementations and screen copy should use these terms consistently.

## Time and processing

### Business date

The operational date to which a control run and its expected evidence belong. It is not necessarily the calendar date on which processing occurs.

### Booking timestamp

The timestamp at which a source system recorded an entry.

### Value date

The date on which a financial movement economically takes effect.

### Control run

One execution of configured completeness and financial controls for a legal entity and business date.

### Run version

An immutable version of a control run. Late or corrected evidence creates a new version rather than changing an earlier result.

### Ruleset version

The exact versioned collection of mappings, matching rules, lifecycle definitions, tolerances, materiality and safeguarding settings used by a run.

## Evidence and sources

### Source

A configured producer of financial or operational evidence, such as a bank, processor, payment system or internal ledger.

### Source delivery

One received file or controlled API extract associated with a source, business date and schema version.

### Source record

An immutable record as received in a source delivery.

### Content fingerprint

A cryptographic digest used to identify the exact content of a delivery or evidence object.

### Canonical record

A normalised Proofly representation of a source record that retains a link to its original evidence.

### Provenance

The trace from a canonical record, calculation or conclusion back to its source delivery and source record.

### Evidence pack

A reproducible export containing the run manifest, source manifest, control results, exceptions, approvals and sign-off for a defined scope.

## Accounts and money

### Client account

The account or sub-ledger position used to record money attributable to one client within a legal entity and currency.

### External financial account

A bank, card-settlement or other external account represented by independently obtained transaction or balance evidence.

### Ledger entry

An internal accounting debit or credit recorded against one or more accounts.

### Balance snapshot

A recorded account balance for a stated timestamp or business date.

### Independently calculated balance

The balance reconstructed by Proofly from an independently supported opening balance and canonical movements using versioned posting rules.

### Recorded balance

The closing balance reported by the primary internal ledger or source system.

### Balance difference

The independently calculated balance minus the recorded balance, expressed in one explicit currency.

## Transactions and matching

### Transaction instruction

A request to initiate, collect, transfer, settle, cancel or reverse money.

### Transaction event

An observed lifecycle event such as submission, ledger posting, external booking, settlement, rejection, return or reversal.

### External transaction

A financial movement observed in independent external evidence.

### Match

A versioned decision that specified records represent the expected parts of the same financial event or settlement group.

### Match group

The set of records participating in one one-to-one, one-to-many or many-to-one match.

### Unmatched record

An in-scope record for which no valid match was established under the active ruleset.

### Transaction lifecycle

The configured sequence of allowed states and events from instruction to final outcome.

## Controls and exceptions

### Control

A deterministic, versioned test or calculation that produces a reproducible result from defined evidence.

### Control result

The outcome of applying one control version to a defined population in a run.

### Reason code

A stable machine-readable classification explaining a control result or exception.

### Materiality

A configured threshold or policy used to determine severity, approval and sign-off consequences. Materiality never changes the underlying difference.

### Exception

A durable operational record created when evidence, processing or a control result requires investigation or approval.

### Accepted difference

A difference that remains present but has been explicitly reviewed and accepted by authorised users with a recorded rationale.

### Resolution

The documented conclusion of an exception investigation, including supporting evidence and any required approval.

### Qualification

An explicitly disclosed limitation or unresolved matter attached to a sign-off.

## Safeguarding

### Client-money obligation

The amount that the organisation is required to safeguard for clients for a legal entity, currency and business date under its approved policy.

### Eligible safeguarding balance

An independently evidenced external balance that meets the configured eligibility policy for inclusion in coverage.

### Adjustment

A separately identified, evidenced and approved amount included in or excluded from a safeguarding calculation.

### Surplus

Eligible safeguarding balance minus client-money obligation when the result is positive.

### Shortfall

Client-money obligation minus eligible safeguarding balance when the obligation is greater.

### Coverage cannot be established

A state used when required evidence or approved policy is insufficient to calculate a reliable surplus or shortfall.

## Governance

### Control analyst

A user who monitors runs, investigates exceptions and proposes resolutions.

### Control owner

An accountable user who approves material outcomes and signs off a control run.

### Configuration administrator

A user who proposes and maintains versioned source and control configuration.

### Sign-off

An authenticated, immutable attestation by an authorised control owner over one exact run version.

### Separation of duties

A policy preventing one user from both proposing and approving the same material action.

