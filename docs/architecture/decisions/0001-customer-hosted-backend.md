# ADR 0001: Customer-hosted Backend

## Status

Proposed for oMVP validation.

## Context

Proofly must schedule T+1 processing, retain immutable evidence, support reprocessing, enforce role separation and preserve sign-off history. A browser-only application or analyst-owned scripts cannot provide these properties reliably.

Customers may not accept raw client-money evidence in a shared multi-tenant service.

## Decision

Proofly will use backend services, but the initial production deployment will run inside customer-controlled infrastructure.

The same logical components will support:

- one-machine development;
- a single-machine evaluation appliance; and
- durable customer-hosted production.

The backend contains an authenticated API, deterministic workers, source adapters, relational data storage, object evidence storage and a durable job queue.

## Consequences

### Positive

- sensitive evidence remains under customer control;
- processing continues without an analyst's browser session;
- the oMVP can start locally without creating a laptop-only architecture;
- production and local environments share boundaries and migrations; and
- multi-user approval and audit history remain possible.

### Costs

- packaging and upgrades must support customer environments;
- operational monitoring and recovery responsibilities must be explicit;
- installation is more involved than a browser-only service; and
- connector networking differs between customers.

## Not decided

- desktop shell versus browser-only local UI;
- database, object store and queue products;
- supported orchestration targets;
- offline operating duration; and
- managed private deployment offering.

