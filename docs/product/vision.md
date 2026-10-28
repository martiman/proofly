# Product Vision

## Vision

Proofly gives organisations that hold or move client money an independent, daily answer to a fundamental question:

> Is every client's money correctly recorded, adequately covered and moving through the expected process?

The product is intended to turn fragmented operational, banking and ledger evidence into a reproducible T+1 integrity assessment. Control owners should be able to see what was checked, what passed, what failed, why it failed and who is responsible for the next action.

## Status of this document

This document describes the **proposed product direction**. No capability described here should be interpreted as implemented. The repository currently contains product documentation only.

## Problem

Financial operations often span multiple systems and evidence sources. A single client-money movement may appear as an instruction, one or more internal ledger entries, an external bank or card record, a settlement outcome and a change in safeguarding coverage. Each source can be internally consistent while the complete journey is not.

Control teams therefore need to answer questions that no individual source system can answer reliably on its own:

- Did all expected data arrive for the business date?
- Does an independently calculated client balance agree with the primary ledger?
- Can each instruction be linked to the expected internal and external movements?
- Are failed, reversed, duplicated, delayed or partially processed transactions identified?
- Are client-money obligations covered by eligible safeguarding balances?
- Can every conclusion be reproduced later from preserved evidence?

Manual reconciliations and disconnected reports can provide partial answers, but they make it difficult to demonstrate completeness, apply consistent rules and manage exceptions across the whole control cycle.

## Target users

### Primary users

- reconciliation and financial-operations analysts;
- safeguarding and client-money control owners;
- finance and treasury control teams; and
- operational-risk and compliance reviewers.

### Evidence consumers

- internal audit;
- external audit;
- senior management and accountable executives; and
- authorised regulatory or assurance reviewers.

## Product promise

For each completed business date, Proofly is intended to produce an integrity result that is:

- **complete** - expected sources and populations are accounted for;
- **independent** - material balances and lifecycle conclusions are calculated outside the systems being checked;
- **explainable** - results show the evidence, rule and calculation behind them;
- **actionable** - failures become owned and prioritised exceptions;
- **reproducible** - the same immutable inputs and rule versions produce the same result; and
- **auditable** - ingestion, processing, decisions, overrides and sign-off remain traceable.

## Initial scale and cadence

The proposed initial operating envelope is:

- 10,000-100,000 financial transactions per day;
- 10,000-100,000 client accounts;
- one complete control run per business date, normally available on T+1; and
- controlled reprocessing when late or corrected evidence arrives.

This is a design target, not a measured performance claim.

## Expected evidence

The initial product should be able to normalise and compare evidence from:

- banking transactions and bank account balances;
- card statements and settlement records;
- transfer instructions and their status history;
- direct-debit instructions, submissions, returns and outcomes;
- internal ledger entries and client balances; and
- safeguarding account balances and eligibility classifications.

## Desired outcome

A control owner begins each day with a clear view of the previous business date:

1. all expected evidence has either arrived or is visibly missing;
2. independently reconstructed client balances have been compared with recorded balances;
3. transactions have been matched across their expected lifecycle;
4. safeguarding coverage has been calculated by currency and legal entity;
5. unresolved differences have become prioritised exceptions; and
6. the completed run can be reviewed, approved and reproduced.

## Success measures for a future implementation

The following are proposed measures to validate after an implementation exists:

- percentage of expected source deliveries received and validated by the T+1 deadline;
- percentage of in-scope value and records matched automatically;
- percentage of client accounts whose independent balance agrees with the primary ledger;
- count and value of unexplained lifecycle breaks, aged by severity and duration;
- safeguarding surplus or shortfall by currency and legal entity;
- time from exception creation to assignment and resolution;
- percentage of daily control runs completed and signed off on time; and
- ability to reproduce a historical result from preserved inputs and rule versions.

Targets should be set only after representative data has been profiled and baseline performance is known.

## Boundaries

The proposed product is a verification and evidence platform. It should not:

- initiate, approve or alter financial transactions;
- become the primary ledger or accounting book of record;
- silently correct source data;
- treat an operator override as equivalent to source evidence; or
- claim real-time prevention when operating on a T+1 cycle.

## Product principles

1. **Check the checker.** Critical conclusions must not depend only on the system that produced the data under review.
2. **Make completeness explicit.** A clean reconciliation is not trustworthy if part of the expected population is absent.
3. **Preserve provenance.** Every normalised record and result should point back to its source.
4. **Prefer deterministic controls.** The same facts and rule version should produce the same outcome.
5. **Separate facts, rules and judgement.** Source evidence, configured logic and human decisions require distinct records.
6. **Design for exceptions.** Breaks, late data and corrections are normal operational events and require durable workflows.
7. **Do not trade auditability for convenience.** Corrections should append evidence and history rather than erase it.
