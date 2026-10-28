# Account and Ledger View

## Status

**Proposed.** Not implemented.

## Purpose

The Account and Ledger View explains how Proofly independently calculated a client-account balance and why it agrees or differs from the primary ledger.

## Primary users

- reconciliation and finance analysts;
- safeguarding control owners; and
- audit reviewers.

## Main layout

### Account header

Shows client account, currency, legal entity, product, business date, independent balance, recorded balance and difference.

### Balance bridge

Presents:

- independently supported opening balance;
- total supported debits;
- total supported credits;
- explicit adjustments;
- independently calculated closing balance;
- recorded ledger closing balance; and
- resulting difference.

Every value links to its contributing records. The bridge must not silently mix currencies or legal entities.

### Movement table

Lists canonical movements with dates, references, transaction links, debit or credit direction, value and support status. Users can filter to missing, duplicated, misdated, mis-signed or unsupported entries.

### Difference explanation

Shows which records contribute to the difference and whether it is caused by missing evidence, ledger posting, timing, duplication or an unresolved mapping.

### Historical comparison

Shows prior closing balances, differences and relevant exceptions without rewriting previously signed-off runs.

## Primary actions

- open a movement or related transaction;
- inspect source provenance;
- open or link an exception;
- compare run versions;
- export the calculation trace; and
- inspect records contributing to a difference.

## Important states

- agreed;
- difference within an explicitly configured tolerance;
- material difference;
- incomplete evidence;
- opening balance unsupported; and
- calculation failed.

## MVP boundary

The MVP requires one-account, one-currency roll-forward, contributing movement drill-down and comparison with the internal recorded balance.

