# Daily Control Room

## Status

**Proposed.** Not implemented.

## Purpose

The Daily Control Room is the operational home for a business date. It answers three questions immediately:

1. Is the evidence population complete?
2. Are client money and transaction outcomes supported?
3. What requires human attention before sign-off?

## Primary users

- control analysts;
- safeguarding and finance control owners; and
- operational managers.

## Main layout

### Daily summary

A deterministic summary shows what changed since the previous completed business date. Each material change links to a control result or exception.

### Run header

Shows legal entity, business date, run version, ruleset version, processing state, last evidence arrival and sign-off state.

### Evidence readiness

Lists expected sources with received, late, missing, rejected or superseded states. A run cannot present an unqualified result while mandatory evidence is incomplete.

### Control areas

Separate cards show:

- independent ledger verification;
- matching and reconciliation;
- transaction lifecycle;
- safeguarding coverage; and
- exception and approval readiness.

Each card shows record count, value, control state and change from the previous business date. No composite health score should obscure a failed mandatory control.

### Attention queue

Shows material exceptions, overdue work, pending approvals and controls blocked by missing evidence. Users can filter by severity, owner, currency and control area.

### Safeguarding summary

Shows obligation, eligible balance, adjustments and surplus or shortfall by currency and legal entity, with a link to the full calculation.

## Primary actions

- investigate a control result;
- open or assign an exception;
- request missing evidence through an approved app;
- start an authorised reprocessing run;
- compare with a previous business date; and
- proceed to sign-off when mandatory conditions are met.

## Important states

- awaiting evidence;
- processing;
- review required;
- ready for sign-off;
- signed off;
- superseded; and
- failed or partially available.

## MVP boundary

The MVP should support one legal entity at a time, a selected business date, source readiness, control summaries, material exceptions, safeguarding position and a path to sign-off.

