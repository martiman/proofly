# Proofly Web Application

## Status

This directory is reserved for the future Proofly user application. It currently contains no working implementation.

## Responsibility

The web application will implement the proposed operational screens and user workflows. It will present data obtained through the authenticated API, but it will not perform authoritative financial calculations or connect directly to financial source systems.

The initial application should cover:

- Daily Control Room;
- Sources;
- Transactions and Matching;
- Exceptions;
- Safeguarding;
- Sign-off and Evidence; and
- Configuration and Access.

## Design and product sources

- Product behaviour and screen states: [`docs/product/screens/`](../../docs/product/screens/README.md)
- User workflows: [`docs/product/workflows/`](../../docs/product/workflows/README.md)
- Visual design source: [`proofly.pen`](../../proofly.pen)
- API and backend boundaries: [`docs/architecture/overview.md`](../../docs/architecture/overview.md)

## Implementation boundary

Do not initialise a framework until the first implementation technology decision is recorded. When implementation begins, this directory should contain the browser application, its component tests and its application-specific assets. Shared UI primitives may later move to `packages/ui/` when they are genuinely reused.
