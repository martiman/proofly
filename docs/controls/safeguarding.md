# Safeguarding Coverage Control

## Status

Proposed oMVP control specification.

## Objective

Determine whether independently supported client-money obligations are covered by eligible safeguarding balances for each legal entity, currency and business date.

## Inputs

- independently calculated client-account balances;
- approved inclusion and exclusion policy;
- external safeguarding-account balance evidence;
- account eligibility classification;
- approved adjustments; and
- completeness results for obligation and balance sources.

## Preconditions

- legal entity and currency are explicit;
- obligation population has a completeness state;
- external balance evidence identifies account, currency, timestamp and source;
- eligibility rules are approved and effective; and
- adjustments contain evidence, owner and approval.

## Calculation

For each legal entity and currency:

```text
eligible coverage
  = sum of eligible external balances
  + approved positive adjustments
  - approved negative adjustments

position
  = eligible coverage
  - client-money obligation
```

A positive position is a surplus. A negative position is a shortfall. Unsupported cross-currency or cross-entity netting is prohibited.

## Outputs

- client-money obligation and components;
- eligible balance and account components;
- included and excluded adjustments;
- surplus or shortfall;
- evidence timestamp and freshness;
- policy and calculation versions; and
- control result.

## Initial reason codes

- `COVERED`;
- `SHORTFALL`;
- `OBLIGATION_INCOMPLETE`;
- `EXTERNAL_BALANCE_MISSING`;
- `EXTERNAL_BALANCE_STALE`;
- `ACCOUNT_NOT_ELIGIBLE`;
- `ADJUSTMENT_UNAPPROVED`;
- `CURRENCY_MISMATCH`; and
- `COVERAGE_CANNOT_BE_ESTABLISHED`.

## Exception rules

A shortfall or inability to establish coverage creates a high-severity exception and blocks unqualified sign-off. Stale evidence severity is determined by the approved balance-freshness policy.

## Adjustments

Adjustments never modify source evidence. They are separate versioned records with amount, currency, legal entity, reason, evidence, proposer, approver and effective business date.

## Reprocessing

New balance evidence or an approved adjustment creates a new calculation in a new run version. Previous safeguarding positions and sign-offs remain available.

## Acceptance examples

- Eligible balance exceeds complete obligation: surplus.
- Obligation is complete but eligible balance is lower: shortfall.
- Bank balance is missing: coverage cannot be established, not zero coverage.
- Balances in two currencies cannot offset one another without an explicit approved policy, which is outside the oMVP default.

