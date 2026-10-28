# Proofly

Proofly is an **Independent Financial Integrity Platform** for financial institutions and other organisations that hold or move client money.

Its purpose is to provide daily, T+1 evidence that:

- client money is recorded completely and accurately;
- client balances are independently supported by source transactions and an independently reconstructed ledger;
- money that should be safeguarded is covered by eligible external balances; and
- each financial transaction has progressed through its intended lifecycle.

Proofly is conceived as an independent control layer. It consumes evidence from operational and financial systems, performs its own checks and calculations, and produces explainable results without becoming the system that executes payments or maintains the primary books and records.

> **Project status:** documentation and product definition only. The repository does not yet contain a working implementation. Every capability described here is **proposed** unless a future document explicitly marks it as implemented and links to verifiable code or a released artefact.

## Initial operating scope

The initial design target is an organisation processing approximately:

- 10,000-100,000 financial transactions per day; and
- 10,000-100,000 client accounts.

Expected evidence sources include:

- banking transactions;
- card statements;
- transfer instructions;
- direct-debit instructions and outcomes;
- internal ledger entries; and
- account and safeguarding balances.

## Proposed control areas

Proofly is intended to cover eight connected control areas:

1. **Data completeness** - establish whether all expected files, records, sequences and business dates have arrived.
2. **Independent ledger verification** - reconstruct movements and balances separately from the primary ledger.
3. **Matching and reconciliation** - link internal instructions and ledger entries to external evidence.
4. **Transaction lifecycle verification** - confirm that each transaction reaches a valid outcome within an expected time.
5. **Safeguarding coverage** - compare client-money obligations with eligible safeguarding balances.
6. **Exception management** - prioritise, assign, investigate and resolve control failures.
7. **Audit evidence** - retain inputs, rules, decisions, adjustments and operator actions in a traceable form.
8. **Operational reporting** - provide daily control results, trends and evidence suitable for review and sign-off.

See [Product Capabilities](docs/product/capabilities.md) for the proposed capability model.

## Product documentation

- [Product Vision](docs/product/vision.md)
- [Product Capabilities](docs/product/capabilities.md)
- [MVP Definition](docs/product/mvp.md)
- [Product Screens](docs/product/screens/README.md)
- [Daily Control Cycle](docs/product/workflows/daily-control-cycle.md)
- [Domain Glossary](docs/domain/glossary.md)
- [Architecture Overview](docs/architecture/overview.md)
- [Deployment Model](docs/architecture/deployment-model.md)
- [Repository Structure](docs/architecture/repository-structure.md)
- [Input Data Contracts](docs/integrations/input-contracts.md)
- [Synthetic T+1 Input Dataset](examples/synthetic-data/2026-10-27/README.md)
- [Roles and Approvals](docs/security/roles-and-approvals.md)
- [Data Handling](docs/security/data-handling.md)
- [oMVP Acceptance Plan](docs/testing/omvp-acceptance-plan.md)

## Control specifications

- [Data Completeness](docs/controls/data-completeness.md)
- [Independent Ledger](docs/controls/independent-ledger.md)
- [Matching and Reconciliation](docs/controls/matching.md)
- [Transaction Lifecycle](docs/controls/transaction-lifecycle.md)
- [Safeguarding Coverage](docs/controls/safeguarding.md)

## Principles

- **Independent by design.** Do not rely solely on conclusions produced by the systems being checked.
- **Evidence before assertion.** Every material result should be traceable to source evidence, a versioned rule and a reproducible calculation.
- **Exceptions are first-class records.** Failures should be owned, explained and resolved, not hidden inside reports.
- **Financial precision.** Preserve currencies, value dates, transaction identity and decimal precision throughout processing.
- **Safe reprocessing.** Repeated ingestion and calculation should be deterministic and idempotent.
- **Human accountability.** Automation assists control owners; it does not obscure approvals, overrides or judgement.

## What Proofly is not intended to be

Proofly is not intended to execute payments, replace a bank or card processor, serve as the primary accounting ledger, or make autonomous decisions about moving client money. The first release is also not intended to provide real-time transaction blocking; its operating objective is a complete and explainable T+1 control cycle.

## Contributing

The product is at an early definition stage. Proposed changes should preserve the distinction between product intent and implemented behaviour. Documentation must not describe a capability as available until it is supported by working, tested code.
