# Work status

> Single source of truth for where the MVP stands. **Read this first every session; update it before
> the session ends** (lead only). Plan and slice briefs: [mvp.md](mvp.md).

## Now

- **Next slice:** S05 — Matching + expansion
- **In progress:** —
- **Blocked:** —

## Slices

States: `todo` · `in-progress` · `in-review` · `changes-requested` · `done` · `blocked`

| ID | Slice | State | Branch / PR | Rounds | Notes |
|---|---|---|---|---|---|
| S00 | Repo baseline + team setup | done | `main` | | Strict build, verify gate, hook, CI, agents, standards, sandbox |
| S01 | Workload schema v2 | done | PR #2 | 1 + suggestions | APPROVE first round; non-blocking test/doc gaps fixed before merge. `id` pattern widened vs ADR 0011 (hyphens, ≤16) — recorded under C13/C14 |
| S02 | Catalog formats + loader | done | PR #3 | 2 | Round 1: 2 blocking (false cascading errors; resolver.md example contradicted seed). ~630 lines, over guideline, accepted. Follow-ups moved to carry-forward notes. |
| S03 | Environment descriptor | done | PR #4 | 2 | Round 1: 1 blocking — NJsonSchema ignores `propertyNames`, so dotted keys could shadow nested values (D19 boundary); now enforced in code. `CatalogError` renamed `LoadError`. |
| S04 | Expression evaluator | done | PR #5 | 2 | Round 1: 1 blocking (uncommented `!`). Golden hash/guid values pinned. ~690 lines — 2nd size overrun despite 'stop and report' in the brief. |
| S05 | Matching + expansion | todo | | | |
| S06 | Policies + provenance | todo | | | |
| S07 | Graph builder | todo | | | |
| S08 | CLI skeleton | todo | | | |
| S09 | Template library | todo | | | |
| S10 | Provider fixes | todo | | | |
| S11 | Orchestrator (infra nodes) | todo | | | |
| S12 | Substrate from data | todo | | | |
| S13 | Second workload | todo | | | |
| S14 | Sample app | todo | | | |
| S15 | Runtime mapping + container-app | todo | | | |
| S16 | Deploy steps | todo | | | |
| S17 | jd deploy + record | todo | | | |
| S18 | E2E run and docs | todo | | | |

## Carry-forward notes

Picked up by the slice named; remove once done.

- **S05 (catalog loader):** validate naming `allowed` is a valid regex and includes hex digits (hash is appended after filtering); duplicate-key error lacks key/line; test pinning the "no Catalog" suppression when a
  file is unreadable; YAML parsed 3x per catalog file; move `CatalogParser.ToSchemaError` next to `LoadError`.
- **S06:** catalog records hold mutable `JToken`s — clone on consumption.
- **S09:** Azure role assignments need the full role-definition id; the `role-assignment` template must build
  it from the role GUID (`roles.yaml` holds GUIDs only).
- **S11:** per-workload resources (database, App Insights) currently target `${env.resourceGroup}` (substrate);
  give workloads their own tagged resource group so `tools/azure/cleanup.sh` can remove them.
- **Validator:** NJsonSchema silently ignores some keywords (`propertyNames` confirmed). Don't rely on a schema
  keyword for a rule without a test proving it is enforced.

## Environment

| Item | Value |
|---|---|
| Subscription | `MVPLandingZone` (`ca89cbcc-e368-4e81-9e80-6686b4d9f3b9`), Pay-As-You-Go, guardrails applied |
| Credentials | `~/.just-deliver/ca89cbcc-e368-4e81-9e80-6686b4d9f3b9.env` — secret expires **2026-11-07** (renew: `tools/sandbox/sandbox.sh apply … --rotate-secret`, needs the user as Owner) |
| Region | `southeastasia` (`JD_REGION`) |
| Live Azure resources | none |

## Session log

Newest first. One entry per session: what moved, decisions taken, anything the next session must know.

- **2026-10-09** — Models switched to Sonnet 5.5 (developer) / Opus 5.5 (reviewer) on trial; ask the
  user to re-choose if PR quality is poor. Baseline pushed to GitHub. S01 done (PR #2).
  GitHub: repo is private on the free plan, so branch protection is unavailable and Actions is blocked
  by account billing — local pre-push hook blocks pushes to main; lead runs `tools/verify.sh` before
  each merge. User asked to choose: GitHub Pro + fix billing, make public, or stay local-only.
  Workflow note: developer runs in an isolated worktree; reviewer verifies in a temporary detached
  worktree; the lead commits the status update on the slice branch before merging.
  Trial so far (Sonnet dev / Opus review): S01 approved first round; S02 and S03 each needed one fix
  round for real blocking issues the reviewer caught (S03: a security-relevant validator gap) — the
  pairing is working as intended. Weakness: Sonnet ignored the size cap twice (S02, S04); briefs now
  require checking `git diff --stat main` before finishing. Escalate to the user if it recurs. Briefs should cap
  slice size more tightly (S02 overran).
- **2026-10-08** — Repo baseline and team setup (S00). Docs reorganised; sandbox subscription
  prepared and guardrails verified; strict build, `tools/verify.sh`, commit hook, CI; developer
  and reviewer agents; standards and review checklist; MVP sliced into S01–S18.
