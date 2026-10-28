# Proofly Web Application

## Status

Vue 3 + TypeScript + Ant Design Vue + Vite scaffold ([ADR 0003](../../docs/architecture/decisions/0003-web-technology-stack.md)). It currently contains the application shell and navigation for the seven oMVP sections; each screen is a placeholder.

```bash
npm install
npm run dev       # development server (WEB_PORT, PROOFLY_API_PORT, see .env.example)
npm run build     # type-check and production build
npm test          # Vitest unit tests (mocked fetch)
npm run gen:api   # regenerate src/api/generated/schema.d.ts from docs/integrations/api/openapi.yaml
```

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

## API client (issues #54, #55)

Types are generated from the contract with `openapi-typescript` (MIT) into `src/api/generated/schema.d.ts`; the file is committed and CI fails if `npm run gen:api` changes it. Never edit it; change `openapi.yaml` first.

```
src/api/generated/   schema.d.ts (generated)
src/api/client/      fetch wrapper (openapi-fetch, MIT): auth, X-Request-Id, ApiError, ETag cache, 304, paging helpers, formatMoney
src/api/services/    one function per operationId (57), grouped by area: runs, sources, exceptions, transactions, matching, safeguarding, signoff, governance, evidence
src/api/composables/ useResource / useAsync / usePaged / useCommand (states match StatePlaceholder)
```
The legacy mock (`src/api/service.ts`, `types.ts`, `money.ts`) stays until the view migrations (#56-#58); new code must not import it.

- Base URL: `VITE_API_BASE_URL` (default `/api/v1`, proxied by Vite to the API).
- Token: `TokenProvider` (`client/auth.ts`). Dev: `VITE_DEV_TOKEN` or `setDevToken(token)` (sessionStorage); active only when `import.meta.env.DEV`. Production OIDC is a later task: install a provider and an `onUnauthorized` redirect with `configureApi({ tokenProvider, onUnauthorized })`. No secrets in the repo.
- Errors: every non-2xx throws `ApiError {status, type, title, detail, requestId, errors[], slug}`; helpers `isNotFound`, `isStale` (412, stale-signoff, stale-proposal), `isSeparationOfDuties`, `isValidation`, `fieldErrors`, ...
- Concurrency: GETs capture the ETag per resource (`/exceptions/F-019`); commands send it as `If-Match` unless `ifMatch` is given; repeat GETs revalidate with `If-None-Match` (304 served from cache). List items with `etag` are remembered.
- Commands get a fresh `Idempotency-Key`; pass the same `idempotencyKey` to retry a user action, or use `useCommand`, which does it.
- Money: decimal string + currency end to end. `formatMoney(amount, currency)` is display only; the browser never adds or compares amounts.

```ts
import { getControlRoom, listExceptions, assignException } from '../api/services'   // no '@' alias is configured
import { useResource, usePaged } from '../api/composables'
const room = useResource(() => runId.value, (id, signal) => getControlRoom(id, { signal }))
```

### Running against the real API

```bash
scripts/dev-backend.sh                 # Postgres check, migrate proofly_web, seed demo data, start API (prints port + token file)
cd apps/web
PROOFLY_API_PORT=5080 WEB_PORT=5199 VITE_DEV_TOKEN=$(awk '$1=="ANALYST"{print $3}' ../../.dev/tokens.txt) npm run dev
npm run test:live                      # with PROOFLY_LIVE_BASE_URL / PROOFLY_LIVE_TOKEN set: contract smoke test against the real API
```
Details: [docs/dev-setup.md](../../docs/dev-setup.md).
