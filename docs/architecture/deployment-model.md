# Deployment Model

## Status

This document describes a **proposed** deployment model. No runtime components are currently implemented.

## Decision

Proofly should have backend services. A browser-only application or a collection of scripts running on an analyst's laptop is not sufficient for production client-money controls.

The backend does not have to be a central Proofly-operated SaaS. The recommended model is a **customer-hosted data plane** with an optional Proofly control plane.

## Why a backend is required

Production operation requires capabilities that must continue independently of one user's device:

- scheduled T+1 ingestion and processing;
- durable source evidence and run history;
- deterministic ledger, matching and safeguarding calculations;
- asynchronous jobs and controlled reprocessing;
- concurrent multi-user investigation and approval;
- role-based access and separation of duties;
- tamper-evident audit history;
- secure connection credentials;
- notifications and controlled source operations; and
- backup, recovery, monitoring and retention policy.

A user's browser should never be the authoritative place where a run, approval or financial result exists.

## Customer-hosted data plane

The data plane performs all sensitive financial processing. It can run on one machine for development or evaluation, and inside customer-controlled infrastructure for production.

### Proposed components

```text
Web application
      |
Authenticated API
      |
Control workers + source adapters
      |
Relational database + object evidence store + job queue
```

### Web application

Provides the screens defined in [Product Screens](../product/screens/README.md). It contains presentation and local interaction state, but not authoritative control calculations.

### Authenticated API

Enforces identity, role, legal-entity scope, separation of duties and approval policy. It is the only supported path for changing durable workflow state.

### Control workers

Run deterministic ingestion, completeness, ledger, matching, lifecycle and safeguarding jobs. Jobs are versioned and idempotent.

### Source adapters

Acquire controlled files or API extracts from configured financial systems. Connection credentials remain in the customer data plane and every ingestion attempt is audited.

### Data services

- A relational database stores canonical records, runs, results, exceptions, configuration versions and approvals.
- An object evidence store retains original source objects and generated evidence packs.
- A durable queue schedules ingestion, controls, reprocessing and evidence exports.

## Optional Proofly control plane

A managed control plane may later provide:

- signed software and control-template distribution;
- connector definitions and schema templates;
- organisation and environment administration;
- deployment health metadata;
- approved control and policy template updates; and
- support diagnostics explicitly approved by the customer.

Raw financial evidence, client balances, transactions, credentials and detailed investigation content should remain in the customer data plane by default. Any transfer to a managed service must be explicit, minimised, encrypted and governed by customer policy.

The customer-hosted data plane should remain able to perform daily controls during a temporary loss of connectivity to the optional control plane.

## Supported operating modes

### 1. Developer mode

All components run on one developer machine. The web application is accessed in a browser, while API, workers and data services run locally. Synthetic or de-identified data should be used by default.

Purpose:

- product development;
- automated and exploratory testing;
- connector development; and
- demonstration with non-sensitive data.

Developer mode is not a production control environment.

### 2. Evaluation appliance

The complete stack runs on one customer-controlled workstation or server using containers. It supports a limited pilot population and one or a few users.

Purpose:

- proof of value;
- controlled data profiling;
- rule validation; and
- customer security evaluation.

This mode still uses the backend components; they are simply co-located on one machine.

### 3. Customer-hosted production

Components run as durable services in the customer's private cloud, data centre or approved managed environment. Data services use production backup, recovery, monitoring and security controls.

This should be the default production model for the initial product.

### 4. Managed private deployment

Proofly operates a dedicated environment for one customer under agreed isolation, residency and access controls. This may be offered later when operating maturity supports it.

### 5. Multi-tenant managed service

A conventional shared SaaS deployment is not required for the initial product. It should be considered only after tenant isolation, regulatory requirements, data residency and customer demand are understood.

## Security boundaries

- Browser sessions never receive connector credentials.
- Source operations pass through authenticated services and a durable audit log.
- Material configuration and workflow actions require policy evaluation and, where configured, human approval.
- Source evidence is immutable or content-addressed after ingestion.
- Configuration activation and control sign-off respect separation of duties.
- Local development credentials and data are isolated from production.

## MVP recommendation

Build the first product as a modular monorepo and deploy the complete data plane locally through containers. Preserve the same API, worker and storage boundaries that production will use.

This gives the team fast local development without baking a laptop-only architecture into the product. The first customer pilot can then move the same components into customer-hosted infrastructure rather than redesigning the system.

## Open decisions

- customer identity provider and authentication protocol;
- supported container and orchestration targets;
- production database, object store and queue products;
- offline operating duration;
- software update and connector signing model;
- observability data allowed outside the customer environment; and
- recovery and availability objectives.

