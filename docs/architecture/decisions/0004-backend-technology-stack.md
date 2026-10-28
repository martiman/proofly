# ADR 0004: Backend technology stack

## Status

Proposed. Needs owner approval before backend implementation starts. It follows ADR 0001 (customer-hosted backend) and ADR 0003 (web stack), and the spike required by [Repository Structure](../repository-structure.md).

## Context

The backend must run on one machine and in customer infrastructure, process 10,000-100,000 transactions and accounts per day deterministically, keep immutable evidence, and enforce roles and approvals. The API contract is [`docs/integrations/api/openapi.yaml`](../../integrations/api/openapi.yaml). The expected volume is modest, so correctness, auditability and operability matter more than raw throughput.

## Proposed decision

| Concern | Choice | Reason |
|---|---|---|
| Language | TypeScript on Node.js (LTS) | One language with the web app; types generated from the OpenAPI contract are shared; hiring and customer-hosting are simple. |
| API | Fastify with schema validation generated from `openapi.yaml` | Requests and responses are validated against the contract; contract-first. |
| Database | PostgreSQL | `NUMERIC` for money, transactional integrity, row-level security, append-only audit tables, JSONB for evidence metadata. |
| Job queue | PostgreSQL-backed queue (for example pg-boss) | No extra infrastructure to run on customer sites; transactional with the data. |
| Evidence storage | S3-compatible object store (local filesystem driver for development) | Immutable, fingerprinted original files. |
| Money | Integer minor units or decimal strings; never floating point | Financial precision principle. |
| Migrations | SQL migrations in the repository, same for local and customer deployments | ADR 0001. |
| Packaging | Container images with a Compose file for `deploy/local/` | One-machine start; same boundaries as production. |
| Auth | OIDC against the customer identity provider, roles and legal-entity scope enforced in the API | `docs/security/roles-and-approvals.md`. |

## Alternatives considered

- **Kotlin or Java (Spring):** strong for financial systems and typing, but a second language and a heavier packaging story for the oMVP.
- **Python:** fast for data work, but weaker typing across the contract and for deterministic money handling.
- **Go:** simple deployment, but no shared types with the web app and fewer mature financial-decimal libraries.

## Consequences

- Controls in `packages/controls` are pure functions over typed inputs, so every result is reproducible and unit-testable against `examples/synthetic-data/` and `expected_findings.csv`.
- The first spike must measure a 100,000-transaction run and reprocessing on one machine before any throughput claim.
- If the spike fails, the PostgreSQL schema and the OpenAPI contract remain valid; only the implementation language changes.
