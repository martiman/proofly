# Product Screens

## Status

This directory defines the **proposed** Proofly user experience. No screen described here is currently implemented.

Proofly is an evidence-first financial control system. Screens make financial evidence, deterministic calculations, decisions and permissions inspectable. The structured interface is especially important when a user needs to compare records, monitor a population, approve a material decision or reproduce a historical result.

## Experience model

Every operational screen should combine:

1. **Structured workspace** - tables, timelines, amounts, control states and explicit actions.
2. **Evidence context** - source provenance, calculation trace and rule version.

## Proposed navigation

### Navigation dock

The shell has no sidebar and no top bar. A floating dock at the bottom centre offers four destinations and search. Only the current destination shows its name. A single dot marks a destination that needs the user, without counters.

| Dock destination | Screens | Route |
| --- | --- | --- |
| Today | [Daily Control Room](daily-control-room.md), then [Run Sign-off](run-sign-off.md) when the run is ready | `/control-room`, `/runs/:runId/sign-off` |
| Exceptions | [Exceptions Queue](exceptions-queue.md) and [Exception Detail](exception-detail.md) | `/exceptions` |
| Safeguarding | [Safeguarding Position](safeguarding-position.md) | `/safeguarding` |
| Sources | [Sources](sources.md) | `/sources` |

Reached without a dock destination:

- [Transaction Explorer](transaction-explorer.md) (`/transactions`) opens from search, from an exception or from an account.
- [Configuration and Access](configuration-access.md) (`/settings`) opens from the account menu, available to authorised roles.

### Search and commands

Search opens with ⌘K and is the main way to reach a transaction, client, account, exception, source reference or action. Results are grouped by type, and actions such as assigning an owner or requesting evidence are offered for the selected item. Results respect role permissions before the search is executed.

### Contextual views

- [Matching Workbench](matching-workbench.md) opens from Transactions or Exceptions.
- [Account and Ledger View](account-ledger-view.md) opens from an account, transaction or exception.
- [Evidence and Audit Explorer](evidence-audit-explorer.md) opens from a run, record, exception or evidence pack.

Routes are proposed information architecture, not an implementation contract.

## Shared shell

The desktop-first application should provide:

- the navigation dock and search described above;
- legal entity, business date and run-version context shown as one quiet line and changeable from it, with currency chosen where it matters;
- an attention dot on dock destinations and the account menu for assigned work and approval requests;
- a visible environment and data-freshness indicator, whose placement is still to be designed (the simplified mock-ups omit it); and
- user identity, role and active permission context in the account menu.

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

The first usable release includes the four dock destinations, Transaction Explorer through search and Configuration and Access through the account menu. Matching, account-ledger and evidence exploration begin as contextual views rather than separate primary navigation items.

AI assistance, MCP access, agent workflows and external model integrations are outside the oMVP scope.

