# ADR 0003: Web application technology stack

## Status

Accepted.

## Context

`apps/web/` was reserved until the first implementation technology decision was recorded.

## Decision

The web application uses Vue 3 (TypeScript), Ant Design Vue as the component library, Vue Router, and Vite as build tooling.

## Consequences

- Ant Design Vue tables, forms and layout fit the dense, evidence-first screens in `docs/product/screens/`.
- The web application only presents data from the authenticated API; it performs no authoritative financial calculations.
- Backend and worker technology remain undecided.
