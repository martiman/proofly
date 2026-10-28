# Proofly working agreements

Proofly is an on-premises Independent Financial Integrity Platform (never SaaS). Product docs live in `docs/`; start with README.md.

## Stack
- Web: `apps/web` — Vue 3, TypeScript, Ant Design Vue, Vite (ADR 0003). Build: `cd apps/web && npm run build`.
- Backend: C# / .NET LTS + PostgreSQL (ADR 0004); contract is `docs/integrations/api/openapi.yaml`. The web app must only call the API and never calculate authoritative financial values.
- Design source: `proofly.pen`. Read it only through the pencil MCP tools (`execute`, `get_app_state`), never with Read/Grep.

## Rules
- Money is `decimal` / decimal string with currency; never floating point. Do not hand-add money in UI code.
- The OpenAPI file is the contract. Change it first (via PR), then code. Material decisions require a reason and write an audit event.
- Invalid input is rejected and surfaced, never silently repaired.
- Synthetic data in `examples/synthetic-data/` and `expected_findings.csv` are the acceptance fixtures.
- Do not commit secrets or real customer data.

## Work tracking
- GitHub Issues (martiman/proofly) are the source of truth for tasks; Markdown holds specs and decisions (ADRs). See the `issue-workflow` skill.
- One issue per task; branch `issue-<n>-<slug>`; commits and PRs reference `#<n>` (`Closes #<n>` in the PR). Comment a short summary on the issue when finished.
- Creating or changing issues, labels, milestones and projects on GitHub requires the user's approval; reading is always fine.

## Verification before "done"
- Web: `npm run build` passes and, for UI work, a screenshot of the running page next to the pen frame was compared.
- Backend: `dotnet build` and `dotnet test` pass; control logic reproduces `expected_findings.csv`.
- Start dev servers with the `dev-server` skill (ports are often taken by other projects).
