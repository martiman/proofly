# Exception Detail

## Status

**Proposed.** Not implemented.

## Purpose

Exception Detail is the investigation and resolution workspace for one control failure. It must make the problem, impact, evidence, ownership and decision history understandable without reconstructing the case outside Proofly.

## Primary users

- assigned control analysts;
- subject-matter reviewers; and
- control owners approving material outcomes.

## Main layout

### Case header

Shows exception identifier, control area, configured severity, status, financial value, currency, legal entity, business date, owner and due time.

### Case summary

Summarises the originating control, affected records, financial impact, missing evidence and current investigation state. Every material statement links to a source record or deterministic control result.

### Evidence graph

Shows the connected instruction, ledger entries, external transactions, lifecycle events, balance impacts, control results and prior exceptions. Users can open the original normalised record and source provenance.

### Calculation and rule trace

Shows the exact control, rule version, inputs, tolerances and calculation that produced the exception.

### Investigation timeline

Contains assignments, notes, evidence requests, app actions, status changes, proposed resolutions, approvals and re-open events. History is append-only.

### Resolution panel

Supports resolved, accepted difference and false-positive proposals with mandatory reason, evidence and approval policy. A resolution never deletes the original control result.

## Primary actions

- add an investigation note;
- request or attach evidence;
- record an investigation hypothesis;
- propose a resolution;
- approve or reject a resolution; and
- reopen when later evidence invalidates the conclusion.

## Important states

The screen must make waiting-for-data, conflicting evidence, pending external action, pending approval and superseded-run states visible.

## MVP boundary

The MVP requires case metadata, source links, deterministic trace, notes, ownership, status history and governed resolution.

