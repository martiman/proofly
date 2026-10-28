---
name: issue-workflow
description: Work on a Proofly task through GitHub Issues — read the issue, branch, implement, verify, comment the result, open a PR that closes it. Use when asked to take, work on, triage or report on an issue, or to turn findings into issues.
---

# Issue workflow (repo martiman/proofly)

## Take an issue
1. `gh issue view <n> --repo martiman/proofly --comments` — read the body, acceptance criteria, linked spec/ADR/OpenAPI operations and design frames. If a `type/decision` issue blocks it, stop and say so.
2. Branch: `issue-<n>-<short-slug>` (inside the current worktree, do not touch other worktrees).
3. Implement only what the acceptance criteria require. Stay inside the files/area named in the issue.
4. Verify as in CLAUDE.md "Verification before done". Do not claim done on a green build alone for UI work.
5. Commit with `#<n>` in the message; open a PR with `Closes #<n>` and the acceptance criteria as a checked list.
6. `gh issue comment <n>` with a 3–6 line summary: what changed, how it was verified, what is left or differs from the spec.

## Create issues (needs user approval first)
Show the user the full list of proposed issues (title, labels, milestone, acceptance criteria) and wait for a yes. Then `gh issue create`. Use the templates in `.github/ISSUE_TEMPLATE/`. Labels: `area/{web,api,worker,controls,ingestion,security,docs}`, `type/{feature,bug,spike,decision}`. Milestones: M1 Foundation, M2 Read-only API, M3 Controls on synthetic data, M4 Approvals and sign-off, M5 Pilot. Link dependencies in the body (`Blocked by #n`). Never duplicate an existing issue: search first with `gh issue list --search`.

## Report
For status questions use `gh issue list --repo martiman/proofly --state open --json number,title,labels,milestone,assignees`, grouped by milestone. Projects: use `gh project` only when the user names a project board; changing a board needs approval.
