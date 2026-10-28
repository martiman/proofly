# Product Screens

## Status

This directory defines the **proposed** Proofly user experience. No screen described here is currently implemented.

Proofly is an evidence-first financial control system. Screens make financial evidence, deterministic calculations, decisions and permissions inspectable. The structured interface is especially important when a user needs to compare records, monitor a population, approve a material decision or reproduce a historical result.

## Experience model

Every operational screen should combine:

1. **Structured workspace** - tables, timelines, amounts, control states and explicit actions.
2. **Evidence context** - source provenance, calculation trace and rule version.

## Proposed navigation

### oMVP sections

- [Daily Control Room](daily-control-room.md) - `/control-room`
- [Exceptions Queue](exceptions-queue.md) and [Exception Detail](exception-detail.md) - `/exceptions`
- [Transaction Explorer](transaction-explorer.md) - `/transactions`
- [Safeguarding Position](safeguarding-position.md) - `/safeguarding`
- [Run Sign-off](run-sign-off.md) - `/runs/:runId/sign-off`
- [Sources](sources.md) - `/sources`
- [Configuration and Access](configuration-access.md) - `/settings`

### Contextual views

- [Matching Workbench](matching-workbench.md) opens from Transactions or Exceptions.
- [Account and Ledger View](account-ledger-view.md) opens from an account, transaction or exception.
- [Evidence and Audit Explorer](evidence-audit-explorer.md) opens from a run, record, exception or evidence pack.

Routes are proposed information architecture, not an implementation contract.

## Shared shell

The desktop-first application should provide:

- primary navigation grouped into Daily Work and Administration;
- legal entity, business date, currency and run-version context selectors;
- global search for transaction, client, account, exception and source reference;
- notifications for assigned work and approval requests;
- a visible environment and data-freshness indicator; and
- user identity, role and active permission context.

Mobile layouts may support status review and approvals later, but detailed financial investigation should be designed for a desktop viewport first.

## Shared interaction rules

- Never collapse completeness, correctness and sign-off into a single unexplained score.
- Monetary values always show currency and preserve source precision.
- Every material number links to its calculation and contributing records.
- Proposed actions are visually distinct from completed actions.
- Overrides, accepted differences and approvals require a reason and durable audit history.
- Destructive or externally visible actions require explicit confirmation or pre-authorised policy.
- Historical run versions remain accessible after reprocessing.
- Empty, loading, partial, late-data, failed and permission-denied states are designed explicitly.

## Initial MVP screen set

The first usable release includes the seven oMVP sections listed above. Matching, account-ledger and evidence exploration begin as contextual views rather than separate primary navigation items.

AI assistance, MCP access, agent workflows and external model integrations are outside the oMVP scope.

