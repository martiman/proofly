# Matching and Reconciliation Control

## Status

Proposed oMVP control specification.

## Objective

Relate transaction instructions, internal ledger entries and external financial movements using reproducible rules while preventing incompatible record reuse.

## Inputs

- canonical instructions;
- canonical ledger entries;
- external transactions, card events or settlement records;
- stable identifiers and reference mappings;
- amount, currency, account and date attributes;
- active rule order and tolerances; and
- prior match decisions for the current run version.

## Preconditions

- contributing source populations have explicit completeness states;
- every record has a stable canonical identifier;
- amounts use fixed precision and explicit currency; and
- matching rules are approved and versioned.

## Rule execution

Rules execute in a deterministic configured order:

1. exact stable-identifier matches;
2. exact composite matches;
3. permitted date-tolerance matches;
4. permitted amount-tolerance matches; and
5. controlled one-to-many or many-to-one groups.

A lower-priority rule cannot reuse a record already committed to an incompatible higher-priority match.

## Match requirements

Each match records:

- participating record identifiers;
- cardinality;
- rule and version;
- compared attributes;
- amount and date differences;
- decision timestamp; and
- automatic or authorised manual origin.

## Outputs

- match groups;
- unmatched and partially matched populations;
- conflicting candidates;
- matched and unmatched counts and values; and
- reason codes and rule trace.

## Initial reason codes

- `EXACT_IDENTIFIER_MATCH`;
- `EXACT_COMPOSITE_MATCH`;
- `DATE_TOLERANCE_MATCH`;
- `AMOUNT_TOLERANCE_MATCH`;
- `GROUPED_MATCH`;
- `NO_CANDIDATE`;
- `AMBIGUOUS_CANDIDATES`;
- `VALUE_MISMATCH`;
- `CURRENCY_MISMATCH`;
- `RECORD_ALREADY_USED`; and
- `PARTIAL_MATCH`.

## Manual decisions

The oMVP may allow an authorised analyst to confirm a deterministic candidate group. The user must record a reason, and material manual matches may require approval. Manual confirmation cannot override currency isolation or record-use constraints.

## Exception rules

Unmatched, partial and conflicting populations create exceptions based on materiality, age and transaction type. A missing source population qualifies the result rather than being represented as zero unmatched records.

## Reprocessing

Rematching occurs inside a new run version after late evidence or a changed ruleset. Previous match groups remain reproducible.

## Acceptance examples

- The same stable reference appears once in each source: exact match.
- Two external transactions could match one instruction equally: ambiguous, no automatic match.
- One settlement covers several instructions with equal currency and total value: grouped match when the configured rule permits it.
- Several card presentments link to one processor settlement, which then links to one bank credit: controlled many-to-one matches at each stage.

