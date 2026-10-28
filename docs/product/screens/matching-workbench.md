# Matching Workbench

## Status

**Proposed.** Not implemented.

## Purpose

The Matching Workbench supports investigation of unmatched, partially matched and conflicting records. It is a precision workspace for cases that deterministic automatic rules could not resolve safely.

## Primary users

- reconciliation analysts; and
- configuration specialists reviewing rule performance.

## Main layout

### Population header

Shows business date, source pair, legal entity, currency, automatic match rate, unmatched count and unmatched value. Completeness qualifications remain visible.

### Candidate columns

The workspace presents source records side by side. Users can compare identifiers, references, amounts, currencies, booking and value dates, accounts and lifecycle status.

### Match explanation

For an existing or proposed match, show:

- rule and version;
- exact and tolerant attributes;
- one-to-one or grouped cardinality;
- value difference;
- date difference;
- confidence or ambiguity indicators; and
- records excluded because they are already used incompatibly.

### Candidate analysis

Deterministic rules show candidate groups, attribute differences and record-use conflicts. Candidate presentation never bypasses configured approval thresholds.

## Primary actions

- inspect an automatic match;
- accept or reject an allowed proposed match;
- create a controlled grouped match;
- mark records as requiring external evidence;
- open an exception;
- rerun an authorised rule against a selected test population; and
- propose a rule improvement for separate review.

## Controls

Material manual matches require a reason and, when configured, second-person approval. A user must not approve a rule change or match they proposed where separation of duties requires independence.

## Important states

- unmatched;
- candidate found;
- ambiguous candidates;
- partially matched;
- matched automatically;
- matched manually;
- conflict; and
- superseded by a new run version.

## MVP boundary

The MVP should focus on exact and deterministic composite candidates, side-by-side evidence and governed manual confirmation. Broad free-form matching is out of scope.

