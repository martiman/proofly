# Exceptions Queue

## Status

**Proposed.** Not implemented.

## Purpose

The Exceptions Queue turns control failures into owned, prioritised work. It should support rapid triage without hiding the financial value or evidence behind an exception.

## Primary users

- control analysts;
- team leads; and
- control owners.

## Main layout

### Queue summary

Shows open exception count and value by severity, control area, age and ownership. Counts and values remain separate because a small number of high-value breaks can be more important than a large low-value population.

### Filter and saved-view bar

Supports business date, legal entity, control area, reason, severity, status, owner, age, currency and value range. Users can save role-appropriate views without changing the underlying exception population.

### Exceptions table

Recommended columns:

- severity and status;
- reason and control area;
- business date;
- record count and financial value;
- currency and legal entity;
- owner and due time;
- age and service-level state; and
- latest evidence or activity.

Bulk actions should be limited to assignment, tagging and evidence requests. Material resolution should occur from the exception detail view.

### Queue analysis

Deterministic grouping highlights repeated control reasons, related source deliveries and common affected accounts without changing configured severity or ownership.

## Primary actions

- assign or reassign work;
- open an exception investigation;
- request evidence;
- link likely related exceptions;
- export the filtered queue; and
- inspect grouped exception populations.

## Important states

- unassigned;
- assigned;
- investigating;
- waiting for evidence;
- approval required;
- resolved;
- accepted difference;
- rejected resolution; and
- reopened.

## MVP boundary

The MVP requires filterable queues, assignment, age, value, severity and direct navigation to the originating evidence and control run.

