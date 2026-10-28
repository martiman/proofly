# Data Completeness Control

## Status

Proposed oMVP control specification.

## Objective

Establish that every mandatory source delivery and expected record population for a business date is present, valid and suitable for downstream conclusions.

## Inputs

- active source registry and expected-delivery schedule;
- business calendar and legal entity;
- source deliveries and content fingerprints;
- schema versions and required-field definitions;
- source control totals, when supplied;
- sequence rules, when the source provides sequences; and
- historical delivery statistics used only for visible anomaly thresholds.

## Preconditions

- the expected source configuration is approved and effective for the business date;
- each delivery has a stable source, business date and received timestamp; and
- the original evidence object is retained or referenced immutably.

## Deterministic checks

1. Every mandatory source has an accepted delivery for the business date.
2. The content fingerprint is not an unintended duplicate.
3. The declared schema version is supported.
4. Every required field is present and parseable.
5. Currency, amount precision, dates and identifiers satisfy domain constraints.
6. Record identifiers are unique within the configured scope.
7. Expected sequences contain no unexplained gaps or overlap.
8. Record counts and value totals agree with supplied control totals.
9. Corrected deliveries are versioned and identify the delivery they supersede.

## Outputs

- result per delivery;
- result per source and business date;
- record count and value totals;
- duplicate and sequence findings;
- accepted source-delivery version; and
- overall completeness qualification for the run.

## Result states

- `complete`;
- `complete_with_warning`;
- `incomplete`;
- `rejected`; and
- `cannot_assess`.

## Initial reason codes

- `SOURCE_MISSING`;
- `SOURCE_LATE`;
- `SCHEMA_UNSUPPORTED`;
- `REQUIRED_FIELD_MISSING`;
- `INVALID_VALUE`;
- `DUPLICATE_DELIVERY`;
- `DUPLICATE_RECORD`;
- `SEQUENCE_GAP`;
- `CONTROL_COUNT_MISMATCH`;
- `CONTROL_VALUE_MISMATCH`; and
- `CORRECTION_LINK_MISSING`.

## Exception rules

A mandatory missing or rejected source creates a high-severity exception and blocks unqualified sign-off. Record-level validation failures are grouped by source, reason code and business date unless policy requires individual cases.

## Reprocessing

Replaying identical evidence and configuration produces the same result without duplicate records. Corrected evidence creates a new run version and preserves all earlier completeness results.

## Acceptance examples

- A mandatory bank statement is absent: run is incomplete and sign-off is blocked.
- The same file is delivered twice: one delivery is accepted and the duplicate is recorded without duplicating transactions.
- A control total differs from the parsed value total: the delivery is rejected or qualified according to source policy.

