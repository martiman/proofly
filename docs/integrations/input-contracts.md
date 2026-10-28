# Input Data Contracts

## Status

This document defines the proposed common contract for oMVP file-based sources. Source-specific schemas remain to be agreed with the pilot customer.

A deterministic [synthetic T+1 input package](../../examples/synthetic-data/2026-10-27/README.md) illustrates every source type below with ten records per source. It deliberately includes valid and invalid scenarios and an expected-findings catalogue. The fixture supports development and testing; it is not a definitive pilot-customer schema.

## General delivery contract

Every delivery must identify:

- source name and source-system owner;
- legal entity;
- business date;
- extraction timestamp and source time zone;
- schema name and version;
- unique delivery identifier;
- correction or superseded-delivery reference, when applicable;
- record count;
- value totals by currency, when meaningful; and
- source file or object content fingerprint.

Preferred oMVP formats are UTF-8 CSV with a separate manifest or UTF-8 JSON Lines. Delimiters, quoting, decimal format, date format and compression must be configured explicitly.

## Common field rules

### Identifiers

- Preserve source identifiers exactly as received.
- Do not infer uniqueness without a configured scope.
- Canonical identifiers must retain the source and original identifier.

### Monetary values

- Use decimal strings, never binary floating-point representations.
- Include an ISO 4217 currency code with every amount or amount group.
- Preserve source precision and reject unsupported excess precision.

### Dates and timestamps

- Distinguish business date, booking date, value date and event timestamp.
- Timestamps include an offset or an explicitly configured source time zone.
- Missing time-zone information is not silently interpreted.

### Corrections

- Corrected deliveries do not overwrite an accepted original.
- A correction identifies the delivery it supersedes.
- Record-level correction identity and reason are retained when supplied.

## Transfer instructions

Required initial fields:

- `instruction_id`;
- `client_account_id`;
- `legal_entity_id`;
- `transaction_type`;
- `amount`;
- `currency`;
- `instruction_timestamp`;
- `requested_value_date`;
- `source_status`; and
- source account or destination reference required by the selected flow.

Optional fields include external reference, beneficiary reference, channel, batch identifier, cancellation reference and source update timestamp.

Control totals: instruction count and total instructed value by currency and direction.

## Direct-debit instructions and outcomes

Required initial fields:

- `instruction_id`;
- `mandate_reference`;
- `client_account_id`;
- `legal_entity_id`;
- `amount`;
- `currency`;
- `collection_date`;
- `event_type`;
- `event_timestamp`; and
- `source_status`.

Return and reversal events include original instruction reference, reason code and effective date.

Control totals: instruction and outcome count, collection value, return value and reversal value by currency.

## Internal ledger entries

Required initial fields:

- `ledger_entry_id`;
- `account_id`;
- `legal_entity_id`;
- `booking_timestamp`;
- `value_date`;
- `debit_credit_indicator`;
- `amount`;
- `currency`;
- `transaction_reference`;
- `entry_type`; and
- `source_status`.

Where the ledger uses journals, include `journal_id`, line number and balancing-account reference.

Control totals: entry count, debit total and credit total by legal entity and currency.

## Client-account balances

Required initial fields:

- `account_id`;
- `legal_entity_id`;
- `balance_date`;
- `balance_type`;
- `balance_amount`;
- `currency`; and
- `source_timestamp`.

Control totals: account count and aggregate balance by legal entity, currency and balance type.

## Banking transactions

Required initial fields:

- `external_transaction_id`;
- `external_account_id`;
- `booking_timestamp`;
- `value_date`;
- `amount`;
- `currency`;
- `debit_credit_indicator`;
- `bank_reference`;
- `transaction_status`; and
- `source_timestamp`.

Optional fields include counterparty reference, remittance information, end-to-end identifier and statement sequence.

Control totals: transaction count, debit value and credit value by account and currency.

## Safeguarding balances

Required initial fields:

- `external_account_id`;
- `legal_entity_id`;
- `balance_timestamp`;
- `balance_type`;
- `balance_amount`;
- `currency`;
- `evidence_reference`; and
- `source_timestamp`.

Eligibility is not accepted from the source as a final conclusion. Proofly applies the approved account-eligibility configuration.

## Card transaction events

Card transaction events represent individual transaction journeys and remain separate from processor settlement batches. Required initial fields should cover:

- `card_event_id`;
- `card_transaction_id`;
- `client_account_id`;
- `legal_entity_id`;
- tokenised card reference;
- `event_type`;
- `event_timestamp`;
- `amount`;
- `currency`;
- `debit_credit_indicator`;
- merchant or acceptance reference;
- settlement identifier when assigned;
- original transaction identifier for reversals, refunds and chargebacks; and
- source status.

Initial event types should include authorisation, presentment, reversal, refund and chargeback. A source-specific contract may add clearing or dispute states when the processor exposes them.

Proofly should not require full primary account numbers, card security codes or PIN data. Source adapters should use processor identifiers and tokenised card references sufficient for deterministic linking.

Control totals: event count and value by event type, currency, debit or credit direction and source status.

## Card settlements

When included in the pilot, required fields should cover:

- settlement or batch identifier;
- processor;
- settlement date;
- gross amount;
- fees;
- adjustments;
- net amount;
- currency;
- destination account; and
- source status.

Control totals: batch count, gross, fees, adjustments and net value by currency.

Card transaction events should link to settlement batches through the processor settlement identifier. Settlement batches should then link to the destination bank movement. This preserves the many-to-one relationship without representing a batch as an individual card transaction.

## Validation outcomes

Each source record becomes accepted, rejected or accepted with a non-blocking warning. Every rejected record retains its source location and stable reason code.

No downstream control may silently discard rejected records from its completeness denominator.

## Source-specific work remaining

For each pilot source, create a versioned schema containing:

- exact field names and types;
- required and conditional fields;
- uniqueness scope;
- reference-data mappings;
- decimal and date rules;
- allowed values;
- correction semantics;
- sample accepted and rejected records; and
- source-owner approval.

