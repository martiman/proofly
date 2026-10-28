# Security acceptance (issue #52)

Status: executed 2026-10-28 against the backend in `backend/` (net10.0, PostgreSQL). Acceptance criteria of #52: source credentials never exposed, evidence access audited, session expiry works, dev and prod stores isolated, library licences reviewed. Specifications: `docs/security/data-handling.md`, `docs/security/roles-and-approvals.md`, ADR 0004 and 0005, section "Security and access" of `docs/testing/omvp-acceptance-plan.md`.

Run: `cd backend && dotnet build -warnaserror && dotnet test` (security tests: `dotnet test tests/Proofly.Security.Tests`). The tests need PostgreSQL on `localhost:55432` (`proofly_test`) and use neutral data only (entities `LE-SECA` and `LE-SECB`, GBP/EUR).

## How the tests stay valid when endpoints change

The authorization matrix, the legal-entity isolation sweep and the injection sweep are generated at run time from `docs/integrations/api/openapi.yaml` (embedded in the API) and from `Proofly.Domain.Auth.RolePermissions`. A new operation is covered automatically. Two guards force a human decision instead of passing silently:

- `Every_id_parameter_in_the_contract_is_mapped_to_a_seeded_resource_or_declared_global`: a new path parameter needs a seeded entity-B resource in `Support/SeededDb.cs` (or an entry in `GlobalIds`, for resources that no legal entity owns).
- `Only_known_contract_operations_lack_a_route`: a contract operation without a route fails the build unless it is on the known list (today only `GET /lineage/{subjectKind}/{subjectId}`).

## Checklist

Result: PASS = automated test green; PASS* = green with a residual risk listed below; OPEN = not acceptable yet.

| # | Requirement | Result | Evidence (test class in `backend/tests/Proofly.Security.Tests`) |
|---|---|---|---|
| S-01 | Secrets (connection-string password, bearer tokens, signing key) never appear in logs at any level | PASS | `SecretsTests.Secrets_never_appear_in_logs_problem_responses_audit_events_or_exports`, `A_database_failure_does_not_leak_...`, `Startup_validation_errors_do_not_echo_...`: all `ILogger` output captured at Trace, including scopes, structured state and exception text |
| S-02 | Secrets never appear in problem responses or headers (401, 403, 404, 405, 422, 500, `/readyz` with the database down) | PASS | same class; 500/503 paths are forced with an unreachable database and a valid token |
| S-03 | Secrets never appear in audit events, jobs, idempotency records or exports (evidence pack decompressed entry by entry) | PASS | `SecretsTests.Secrets_never_appear_...` |
| S-04 | Configuration and read endpoints never return credentials (no secret value, no property named password, secret, credential, connection string, signing key, token) for APPROVER, CONFIG_ADMIN and SECURITY_ADMIN, across every GET in the contract; the contract declares no such field | PASS | `SecretsTests.Configuration_and_read_endpoints_never_return_credentials`, `The_contract_declares_no_credential_properties_in_any_schema` |
| S-05 | Every evidence-pack generation request is audited with actor and request id | PASS | `EvidenceAuditTests.Every_generation_is_audited_with_actor_and_request_id` |
| S-06 | Every evidence-pack download is audited (actor, request id, outcome, sha256), repeats counted; visible through `GET /audit-events` | PASS* | `Every_download_is_audited_...`, `Download_audit_events_are_visible_through_the_audit_api_...`, `Audit_events_cannot_be_rewritten_or_deleted` (R-01) |
| S-07 | Denied evidence attempts (no permission, other entity, anonymous, garbage token) are 403/404/401, return no pack data, no download headers, record no delivery and enqueue no job | PASS* | `EvidenceAuditTests.Denied_attempts_are_403_or_404_leak_nothing_and_never_record_a_delivery` (R-01) |
| S-08 | Expired, not-yet-valid, no-`exp`, wrong audience, wrong issuer, `alg=none`, HS384/HS512, RS256 with a foreign key, wrong secret, tampered payload, unknown subject, non-Bearer scheme, token in query string: all 401. Same in Oidc mode via a fake identity provider, including HS256 keyed with the public RSA key material (algorithm confusion) | PASS* | `SessionLocalTests`, `SessionOidcTests` (R-04) |
| S-09 | Tokens and users are re-validated on every request: nothing cached; a token stops working the moment it expires; deactivating a user or revoking a role applies on the next request | PASS* | `SessionRevalidationTests` with a controllable clock (R-03) |
| S-10 | `Auth:Mode=Local` refused in Production (any casing); locally signed tokens and the fixed local users rejected in Oidc mode; Oidc without authority denies everything | PASS | `EnvironmentIsolationTests.Local_auth_mode_...`, `Production_host_does_not_start_with_local_auth`, `SessionOidcTests.Locally_signed_tokens_are_rejected_in_oidc_mode`, `SessionOidcUnconfiguredTests` |
| S-11 | Startup self-check refuses Production with development defaults: dev/test database names, loopback database without credential (trust auth), missing or absent evidence directory, leftover `Auth:Local` signing key or token logging, wildcard or localhost CORS origins; unparseable connection string; messages never echo values | PASS* | `EnvironmentIsolationTests` (R-05); the real host refuses to start in Production for Local auth, the dev database and a missing evidence directory |
| S-12 | Authorization matrix generated from the contract: for every operation and every role lacking `x-required-permission`, 403 (never 404/2xx); 401 for anonymous on every operation; VIEWER holds no mutating permission except personal views and evidence export; ANALYST cannot approve; each approval permission is held only by its documented role; SECURITY_ADMIN and CONFIG_ADMIN cannot decide sign-off | PASS | `AuthorizationMatrixTests` (several hundred generated requests) |
| S-13 | Legal-entity isolation: a caller scoped to entity A (holding every permission for A only) gets 404 for entity B resources on every contract operation with an id (reads and commands), no entity B data in any list or run-scoped response, no database change; `compare?against=<run of B>` refused; positive controls prove the seeds resolve for an unscoped caller and for A | PASS* | `EntityIsolationTests` (R-02) |
| S-14 | SQL injection and over-posting smoke: 17 hostile values in every free-text query parameter (search, sort, cursor, filters) and path id of every GET, no 5xx, no query over 4 s (no `pg_sleep`), no entity B data, tables byte-identical afterwards; a tautology search matches nothing; unknown body properties (`actorId`, `legalEntityId`, `status`, `roles`, `__proto__`) never cause 5xx and never reach the database | PASS | `InjectionTests` |
| S-15 | CORS: only configured origins (exact scheme, host, port), same for preflights and error responses; no credentials header; development defaults are replaced when origins are configured | PASS* | `HttpHardeningTests` CORS tests (R-06) |
| S-16 | Response headers: `Cache-Control: no-store` and `X-Content-Type-Options: nosniff` on every API response including errors; no `Server`, `X-Powered-By`, `X-AspNet-Version`; Kestrel configured not to send `Server`; problem bodies hide stack traces, SQL and paths | PASS* | `HttpHardeningTests` (R-07) |
| S-17 | Library licences reviewed from the real dependency graph, prerelease and non-permissive packages flagged | OPEN | `deploy/customer/THIRD-PARTY.md`: FLAG 1 (JsonSchema.Net family, Open Source Maintenance Fee EULA) needs an owner decision; FLAG 2 prerelease OpenTelemetry Prometheus exporters; FLAG 3 MPL-2.0 build tool |

## Defects found and fixed in `src`

| # | Defect | Fix |
|---|---|---|
| D-1 | `StartupSelfCheck` accepted Production with development defaults: `proofly_dev`/`proofly_test` database, loopback database with no credential (trust auth), leftover `Auth:Local:SigningKey`/`LogTokens`, wildcard or localhost CORS origins | `Proofly.Api/Observability/StartupSelfCheck.cs`: new Production-only checks, messages never contain configuration values |
| D-2 | API responses had no `Cache-Control` and no `X-Content-Type-Options` | new `Proofly.Api/Platform/Http/SecurityHeadersMiddleware.cs` (first in the platform pipeline; `no-store` unless the endpoint set its own) |
| D-3 | Kestrel would send `Server: Kestrel` | `ObservabilityModule`: `ConfigureKestrel(o => o.AddServerHeader = false)` |

## Residual risks

| # | Risk | Rationale and recommendation |
|---|---|---|
| R-01 | Denied evidence and export attempts (403/404) are not audited; only generation and downloads (delivered or refused for integrity) are. Failed attempts are visible only in request logs | `data-handling.md` requires auditing access to raw evidence and exports; attempts that are refused are a useful security signal. Decide whether the pilot needs `EVIDENCE_ACCESS_DENIED` events (needs a contract/audit event type change) |
| R-02 | Resources not owned by a legal entity (settings changes, saved views) are global by design; `GET /lineage/...` has no route yet | Revisit when lineage is implemented: the matrix and isolation tests pick it up, but `KnownUnimplemented` in `AuthorizationMatrixTests` and `NoSeedNeeded` in `EntityIsolationTests` must then be emptied and a lineage seed added |
| R-03 | A session is exactly the token lifetime: there is no revocation list or refresh handling. A token whose IdP session was ended stays valid until `exp` (OIDC clock skew 30 s, local 5 s). User deactivation and role changes do take effect on the next request | Keep IdP token lifetimes short (minutes); document the value in the customer install notes |
| R-04 | The Oidc validator does not pin `ValidAlgorithms` (the Local validator does). Algorithm confusion is blocked today because the signing keys from discovery are asymmetric (tested), but a pinned list (RS256/ES256) would be defence in depth | Low; pin when the supported IdPs are known |
| R-05 | Dev/prod isolation is checked by configuration heuristics (database name suffix, loopback without credential, Local settings). It cannot prove physical separation of stores, credentials and backups | Customer install checklist: separate database server or database, separate credentials, separate evidence directory. Encryption at rest and TLS to PostgreSQL are infrastructure, not tested here |
| R-06 | When `Cors:AllowedOrigins` is not configured the development origins (`localhost:5173`) are the default, also in Production (self-check only refuses them when configured explicitly). The API uses bearer tokens, not cookies, so a foreign origin cannot ride a session | Make `Cors:AllowedOrigins` mandatory in Production in a later change (it breaks the minimal "Host=db" production fixture in the existing self-check tests, hence not done here) |
| R-07 | TLS, HSTS and the real `Server` header are not testable with the in-memory test server; the Kestrel option is asserted instead. TLS terminates at the customer's reverse proxy | Verify `curl -I` against the installed pilot service; add HSTS at the proxy |
| R-08 | No source-connector credential store exists yet (file adapters only), so "source credentials are not exposed" holds because no such field or endpoint exists. The secret-management product is still undecided (`data-handling.md`) | Re-run S-01 to S-04 with a seeded connector secret when connectors with credentials arrive |
| R-09 | `Auth:Local:LogTokens=true` logs local bearer tokens by design (evaluation only). Production refuses the setting; no other mode logs tokens | Accept; documented in the self-check |
| R-10 | `/readyz` is anonymous and names the failing check and the exception type (for example `NpgsqlException`), never a connection string or message | Accept for on-premises; restrict at the proxy if required |
| R-11 | The test suite shares one PostgreSQL server with other test projects; the security project runs its classes sequentially and caps pool sizes to stay below `max_connections` | Raise `max_connections` on the shared test server if more projects are added |

## Licence summary

See `deploy/customer/THIRD-PARTY.md`. No GPL, AGPL, LGPL, SSPL, BUSL or unknown licence in either graph. Decisions needed: FLAG 1 (shipped; MIT source but binaries under the Open Source Maintenance Fee EULA), FLAG 2 (two prerelease exporters, accepted for the pilot), FLAG 3 (MPL-2.0 build tool, not shipped).
