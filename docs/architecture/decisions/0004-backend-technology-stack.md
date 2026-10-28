# ADR 0004: Backend technology stack

## Status

Accepted (stack chosen by the product owner). Supersedes the earlier TypeScript/Node proposal. Builds on ADR 0001 (customer-hosted backend) and ADR 0003 (web stack). The throughput spike described in [Repository Structure](../repository-structure.md) is still required.

## Context

Proofly is delivered on-premises into customer infrastructure, never as SaaS. The backend must:

- install and upgrade simply, with few moving parts and no internet dependency;
- ingest customer files in `.csv`, `.xlsx`, legacy `.xls` and `.xml` (with XSD validation where the customer provides schemas);
- keep financial precision and deterministic, reproducible results;
- process 10,000-100,000 transactions and accounts per day; and
- implement the contract in [`docs/integrations/api/openapi.yaml`](../../integrations/api/openapi.yaml).

## Decision

| Concern | Choice | Reason |
|---|---|---|
| Language and runtime | C# on current .NET LTS | Built-in `decimal` for money; strong typing; mature file-format and XML libraries. |
| Packaging | Self-contained, single-file publish for `linux-x64` and `win-x64` | No runtime to install on the customer host; one artefact per release. |
| API | ASP.NET Core, contract-first from `openapi.yaml`; request and response validation against the contract; ProblemDetails (RFC 9457) errors | Matches the API conventions. |
| Database | PostgreSQL (the only external service) | `NUMERIC` for money, transactions, append-only audit tables, familiar to customer DBAs. |
| Data access | EF Core with Npgsql for ordinary entities; plain SQL (Dapper or EF raw SQL) for control logic and bulk loads (`COPY`) | Set-based controls stay reviewable as SQL; CRUD stays quick. |
| Migrations | Versioned migrations applied by the application at start-up or by `proofly migrate` | Same migrations locally and at customers (ADR 0001). |
| Background jobs | Durable queue stored in PostgreSQL (for example Hangfire with PostgreSQL storage, or a small in-house outbox) | No broker to install; transactional with the data. |
| File ingestion | CsvHelper (CSV); ExcelDataReader (`.xls`, `.xlsx`, CSV); ClosedXML or Open XML SDK (writing `.xlsx` exports); `XmlReader` with XSD validation (XML) | One adapter per source with a documented schema. Rejections become visible exceptions, never silent corrections. |
| Evidence storage | Fingerprinted (SHA-256) files on a configured directory or mounted share; S3-compatible storage later if a customer needs it | Simplest to back up on-premises. |
| Authentication | OIDC or SAML through the customer's identity provider; local users only for evaluation | `docs/security/roles-and-approvals.md`. |
| Observability | Health endpoints, structured logs (Serilog), OpenTelemetry metrics | Operable by customer teams with their own tooling. |
| Delivery | Windows Service or systemd unit from the same build; Docker Compose for evaluation (`deploy/local/`); Helm chart later if needed | Fits customer-hosted environments. |
| Tests | xUnit; control tests run against `examples/synthetic-data/` and must reproduce `expected_findings.csv` | Reproducibility and regression protection. |

## Rules

- Money is `decimal` in code and `NUMERIC` in the database. Floating point is never used for amounts.
- Control logic lives in a separate project with no dependency on the web layer, so results are pure and reproducible from evidence plus a ruleset version.
- Every material decision writes an audit event in the same transaction as the change.
- File adapters validate against the documented contract and reject, not repair, invalid input.

## Alternatives considered

- **Go:** simplest binary, but weak legacy `.xls` support and no built-in XSD validation.
- **Kotlin/Java:** strong libraries, but needs an approved JVM on each customer host.
- **Python:** very good for file handling, but heavier install, weaker typing and a float risk with pandas.
- **TypeScript/Node:** shared language with the web app, but runtime and dependency management cost on-premises and weaker money handling.

## Consequences

- Repository layout: `apps/api` and `apps/worker` become .NET projects, and `packages/domain`, `packages/controls`, `packages/connectors`, `packages/contracts` become class libraries. This refines [Repository Structure](../repository-structure.md).
- The web app generates its TypeScript types from `openapi.yaml`, so the contract is the only coupling between frontend and backend.
- Verify the licences of every library (for example Hangfire, ClosedXML) against the on-premises distribution model before first release.
- The spike must measure a 100,000-transaction run and a reprocess on one machine before any throughput claim.
