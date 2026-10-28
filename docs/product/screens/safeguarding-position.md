# Safeguarding Position

## Status

**Proposed.** Not implemented.

## Purpose

The Safeguarding Position presents the independently supported client-money obligation against eligible safeguarding balances for a business date.

## Primary users

- safeguarding control owners;
- finance and treasury teams;
- authorised approvers; and
- audit reviewers.

## Main layout

### Position header

Shows legal entity, business date, run version, evidence readiness and approval state.

### Currency positions

For each currency, show:

- supported client-money obligation;
- eligible external account balances;
- approved adjustments;
- resulting surplus or shortfall; and
- data or policy qualifications.

Currencies and legal entities remain isolated unless a specific authorised rule states otherwise.

### Obligation breakdown

Allows drill-down by client account, product, movement type and source. The user can trace the total back to independently calculated balances. The position carries the largest lines per currency and their total count; the full list is paged and searchable by account (`listSafeguardingLines`). Totals are never calculated from the lines shown.

### Eligible-balance breakdown

Shows each external account, balance evidence, timestamp, eligibility rule and inclusion or exclusion reason.

### Adjustments

Displays proposed, approved, rejected and expired adjustments with owner, evidence, reason and approval history.

### Change analysis

Compares the current position with the previous business date and identifies material drivers from traceable calculation components.

## Primary actions

- inspect an obligation or balance component;
- request missing bank evidence;
- propose an adjustment;
- approve or reject an adjustment when authorised;
- create a high-severity exception; and
- proceed to the safeguarding sign-off step.

## Important states

- covered;
- shortfall;
- coverage cannot be established;
- external balance stale or missing;
- adjustment approval pending; and
- superseded.

## MVP boundary

The MVP requires position by legal entity and currency, traceable obligation and balance components, explicit adjustments and high-severity shortfall handling.

