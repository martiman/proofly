# Repository Structure

## Status

This document proposes the future repository layout. The application directories do not exist yet.

## Objectives

The repository should:

- keep product and architecture decisions close to the implementation;
- support one-machine development and customer-hosted deployment;
- keep deterministic financial controls independent from presentation and integration concerns;
- make source capabilities and permission boundaries reviewable; and
- avoid premature distribution across many independent repositories or services.

## Proposed monorepo

```text
proofly/
├── README.md
├── docs/
│   ├── product/
│   │   ├── vision.md
│   │   ├── capabilities.md
│   │   ├── mvp.md
│   │   └── screens/
│   │       ├── README.md
│   │       └── one-file-per-screen.md
│   └── architecture/
│       ├── overview.md
│       ├── deployment-model.md
│       └── repository-structure.md
├── apps/
│   ├── web/
│   ├── api/
│   └── worker/
├── packages/
│   ├── domain/
│   ├── controls/
│   ├── connectors/
│   ├── contracts/
│   ├── authorization/
│   └── ui/
├── deploy/
│   ├── local/
│   └── customer/
├── examples/
│   ├── synthetic-data/
│   └── sample-config/
└── tests/
    ├── integration/
    ├── controls/
    └── end-to-end/
```

## Documentation

### `docs/product/`

Contains product intent, scope, terminology and user experience. Each proposed screen lives in `docs/product/screens/` so product, design and engineering can review one bounded workflow at a time.

Screen documents describe purpose, users, visible evidence, actions, permissions, important states and oMVP boundaries. Visual mock-ups may later live beside these files in a `screens/assets/` directory.

### `docs/architecture/`

Contains durable technical decisions and diagrams. When implementation begins, material decisions should be recorded as short architecture decision records under `docs/architecture/decisions/`.

## Applications

### `apps/web/`

The browser-based Proofly workspace. It implements the documented screens and calls the authenticated API. It should not calculate authoritative balances or execute connector calls directly.

### `apps/api/`

The backend entry point for user operations and system processing. It handles authentication, authorisation, queries, commands, workflow transitions and approval policy.

### `apps/worker/`

Runs scheduled and asynchronous deterministic processing: ingestion, validation, ledger reconstruction, matching, lifecycle verification, safeguarding, exports and reprocessing.

These applications may initially deploy together. Separate directories preserve boundaries without requiring separate infrastructure for every component.

## Shared packages

### `packages/domain/`

Canonical financial entities, value objects, business dates, money types, lifecycle definitions and exception concepts.

### `packages/controls/`

Deterministic completeness, ledger, matching, lifecycle and safeguarding logic.

### `packages/connectors/`

Source adapters and controlled operational integrations. Each connector declares input and output contracts, credential needs and approval characteristics.

### `packages/contracts/`

Versioned API, event, job and evidence schemas shared between applications.

### `packages/authorization/`

Role, legal-entity scope, capability grants, separation-of-duties and action-approval policy.

### `packages/ui/`

Shared accessible components, financial formatting, statuses, evidence links and interaction patterns used by the web application.

## Deployment assets

### `deploy/local/`

Starts the complete stack on one machine for development and evaluation. It should use the same application boundaries and database migrations as production.

### `deploy/customer/`

Contains customer-hosted deployment templates, environment configuration contracts and operational documentation. Secrets and customer-specific values must never be committed.

## Examples and tests

Synthetic examples should be sufficient to demonstrate every control path without real customer data. Deterministic control tests should cover financial precision, idempotency, reprocessing and evidence reproduction. End-to-end tests should cover the main screen flows, permissions and approval boundaries.

## Initial implementation sequence

1. Create domain and contract packages.
2. Implement ingestion and immutable evidence handling in the worker.
3. Implement the deterministic control package.
4. Add the API and the first Daily Control Room and Exception screens.
5. Add source adapters with controlled credential handling.
6. Add customer deployment templates after the local stack is repeatable.

## Technology choices

This structure intentionally does not select programming languages or infrastructure products. Those decisions should follow a small technical spike that validates financial precision, processing volume, local packaging and customer-hosted operation.

