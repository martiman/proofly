# Transaction Lifecycle Control

## Status

Proposed oMVP control specification.

## Objective

Verify that each supported transaction follows an allowed sequence of states and reaches a supported final outcome within its configured time window.

## Initial transaction types

- bank transfer;
- direct debit; and
- card transaction when included in the selected pilot flow.

## Inputs

- canonical transaction instruction;
- internal status and ledger events;
- external transaction, settlement, return and reversal evidence;
- matching results;
- transaction-type lifecycle definition;
- business calendar; and
- expected service windows.

## Lifecycle model

Each transaction type defines:

- allowed states;
- allowed transitions;
- mandatory evidence for each transition;
- valid terminal outcomes;
- expected maximum duration; and
- accounting effects expected for rejection, return or reversal.

## Deterministic checks

- instruction identity exists;
- observed states are allowed;
- event order is valid;
- mandatory stages have supporting evidence;
- final outcome occurs within the service window;
- return or reversal is reflected in internal accounting; and
- conflicting terminal outcomes are not accepted.

## Outputs

- ordered transaction timeline;
- current and terminal lifecycle state;
- missing or invalid stages;
- age in current state;
- expected-versus-observed accounting effects; and
- control result with reason codes.

## Initial reason codes

- `MISSING_INSTRUCTION`;
- `INVALID_STATE`;
- `INVALID_TRANSITION`;
- `MISSING_INTERNAL_POSTING`;
- `MISSING_EXTERNAL_OUTCOME`;
- `MISSING_AUTHORIZATION`;
- `MISSING_ORIGINAL_TRANSACTION`;
- `MISSING_SETTLEMENT`;
- `STALLED`;
- `LATE_OUTCOME`;
- `RETURN_NOT_POSTED`;
- `REVERSAL_NOT_POSTED`;
- `CONFLICTING_OUTCOMES`; and
- `COMPLETED`.

## Exception rules

Invalid transitions, missing final outcomes after the service window and unsupported accounting consequences create exceptions. A transaction still within its expected window remains pending and does not create a failure.

## Reprocessing

Late outcomes append to the canonical timeline in a new run version. Earlier lifecycle conclusions remain visible and linked.

## Acceptance examples

- A transfer is instructed, posted and observed at the bank: completed.
- A direct debit is returned externally but no reversing ledger entry exists: exception.
- A card presentment has no corresponding authorisation when the configured flow requires one: exception.
- A card refund references an unknown original transaction: exception.
- A transfer lacks external evidence but remains inside its expected window: pending, not failed.

