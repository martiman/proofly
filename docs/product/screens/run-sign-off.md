# Run Sign-off

## Status

**Proposed.** Not implemented.

## Purpose

Run Sign-off provides an accountable decision point for completing a business-date control run. It should present evidence and unresolved risk clearly enough that an authorised control owner can attest to the result.

## Primary users

- control owners;
- safeguarding approvers; and
- authorised assurance reviewers.

## Main layout

### Sign-off header

Shows legal entity, business date, run and ruleset versions, preparer, required approver and current sign-off state.

### Mandatory control checklist

Lists evidence completeness, independent ledger, matching, lifecycle, safeguarding and required approval controls separately. Each item links to its result and cannot be manually marked passed without the underlying control state.

### Open matters

Shows unresolved exceptions, accepted differences, pending actions and data qualifications. Materiality and value remain visible.

The pack carries the first 50 open matters (blocking first, then severity, then value), the total count and the counts per control area; the screen shows these as a summary and loads the full list page by page (`listRunOpenMatters`, filter blocking or severity). A run can have more than a hundred thousand open exceptions, so the screen never expects the pack to hold all of them.

### Changes since preparation

Highlights new evidence, reprocessing, configuration changes, reopened exceptions or changed calculations since the sign-off was prepared.

### Attestation

The approver sees the exact statement, scope, qualifications and evidence-pack identifier before signing. Authentication and any required second factor should be enforced by deployment policy.

## Primary actions

- inspect a mandatory control;
- return the run for further investigation;
- approve an allowed qualification;
- generate the evidence pack;
- sign off; and
- compare with a later superseding run.

## Important states

- preparation incomplete;
- ready for review;
- returned;
- approval pending;
- signed off;
- sign-off invalidated by later evidence; and
- superseded by a later run version.

## MVP boundary

The MVP requires mandatory control status, unresolved matters, evidence-pack generation and authenticated sign-off with immutable history.

