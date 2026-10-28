# Transaction Explorer

## Status

**Proposed.** Not implemented.

## Purpose

Transaction Explorer provides a single evidence-backed view of a financial transaction across instructions, internal accounting and external movement.

## Primary users

- control analysts;
- payment and operations specialists; and
- audit reviewers.

## Entry and search

Users can search by transaction identifier, client, account, external reference, amount, currency, business date or exception. Search results should distinguish exact identifiers from broader attribute matches.

## Main layout

### Transaction header

Shows the canonical transaction identity, amount, currency, client, business date, current lifecycle conclusion and related exception state.

### Lifecycle timeline

Displays observed stages such as instruction, approval, submission, internal posting, external booking, settlement, return, reversal or cancellation. Missing expected stages are visible rather than omitted.

### Record comparison

Shows important attributes from each source side by side, including identifiers, amount, currency, dates, account, status and reference. Differences are highlighted with their matching tolerance or rule.

### Financial impact

Shows the transaction's effect on independently calculated and recorded client balances, plus any safeguarding impact.

### Evidence and provenance

Links each normalised event to the original delivery, record location, source timestamp and content fingerprint.

## Primary actions

- open a contributing source record;
- inspect match decisions;
- open the affected client account;
- create or open an exception;
- compare with a related transaction; and
- inspect the rule and evidence behind each lifecycle conclusion.

## Important states

- completed normally;
- pending within expected time;
- stalled or late;
- rejected or returned;
- reversed;
- conflicting evidence;
- incomplete population; and
- unknown because required evidence is missing.

## MVP boundary

The MVP should support selected transfer and direct-debit journeys with a timeline, side-by-side source comparison and links to ledger differences and exceptions.

