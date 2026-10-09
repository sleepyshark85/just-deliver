# Work status

> Single source of truth for where the MVP stands. **Read this first every session; update it before
> the session ends** (lead only). Plan and slice briefs: [mvp.md](mvp.md).

## Now

- **Next slice:** S11 — Orchestrator (infra nodes) — first slice that creates Azure resources
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
| S04a | Catalog loader hardening | done | PR #6 | 2 | Review follow-ups from S02–S04. Round 1: 1 blocking — new duplicate-key enrichment crashed on scanner errors (regression); fixed with tests. |
| S05 | Matching + expansion | done | PR #9 | 1 + suggestions | APPROVE first round. ~456 lines (≈223 prod) — developer stopped at the cap and reported, as asked; lead accepted. |
| S06 | Policies + provenance | done | PR #10 | 2 | Round 1: 4 blocking (expansion errors dropped; duplicate added-node names; overlapping paths resolved by length instead of conflict; `!`). Loader now rejects policies fitting neither scope. ~330 prod lines after review fixes. |
| S07 | Graph builder | done | PR #11 | 2 | Round 1: 4 blocking (stack names invalid for Pulumi and collision-prone; time-zone-dependent date parsing broke hash determinism; untested pending-function grant path; `!`). Stacks now `id` with `/`→`.`, `@`→`_`; node names enforced in code. |
| S08 | CLI skeleton | done | PR #12 | 2 + suggestions | Round 1: 2 blocking (directory/unreadable inputs crashed with raw exceptions; multi-line schema errors without file). Workload loading moved to `jd.resolver.workload`; preview shows `(from env.…)`. **M1 complete.** |
| S09 | Template library | done | PR #13 | 2 | Round 1: 4 blocking (container names could collide across workloads → cross-workload grant; workspace shared key in clear state; adapter→resolver dependency not in standards — amended; `!`). Cosmos account now hard-capped (`totalThroughputLimit`). `samples/provisioner` removed. |
| S10 | Provider fixes | done | PR #14 | 2 | Round 1: 3 blocking (outputs lookup could report a deployed stack as undeployed; preview made "not deployed" ambiguous; dead `Version`). Refresh events no longer counted as changes. Real offline Pulumi tests in the gate. |
| S11 | Orchestrator (infra nodes) | todo | | | |
| S12 | Substrate from data | todo | | | |
| S13 | Release set (offline) | todo | | | |
| S13b | Deploy a release set (infra) | todo | | | |
| S14 | Sample app | todo | | | |
| S15 | Runtime mapping + container-app | todo | | | |
| S16 | Deploy steps | todo | | | |
| S17 | Release record + qualification | todo | | | |
| S18 | Second environment + approvals | todo | | | |
| S19 | Promotion | todo | | | |
| S20 | E2E run and docs | todo | | | |

## Carry-forward notes

Picked up by the slice named; remove once done.

- **Loader (minor):** scanner failures report only "Exception during deserialization (line N)"; append the inner
  exception message.
- **S11/S13:** introduce a single `Resolver.Resolve(...)` facade when the second production caller appears (CLI
  and orchestrator); the stage chain is currently wired in the CLI and the golden test.
- **S12:** the substrate passes `totalThroughputLimit: 1000` and `enableFreeTier: true`; its Azure test asserts the
  deployed account's `capacity.totalThroughputLimit = 1000` and that both environment databases (400 RU/s each) fit.
- **S10/S12:** template `fn::invoke`s (e.g. `getSharedKeys`) run during preview, so previewing a brand-new
  environment fails until its workspace exists — preview substrate in dependency order, or tolerate it.
- **S11:** YamlDotNet is now 16.x (Pulumi.Automation pins it); Pulumi config values are strings — serialise
  structured values (maps) as JSON.
- **S11:** `GetOutputsAsync` takes a whole `DeploymentPackage` but ignores its required `DeploymentParameters`; decide
  the narrower signature when writing the orchestrator.
- **Briefs:** cap production lines (~250) separately from tests; a cohesive slice's tests should not be trimmed to fit.
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

- **2026-10-09 (user direction)** — Pulumi stays (no Bicep backend); App Service possible later, Container
  Apps now; environments have one owner (team or platform), a team may own several, stage/production are
  platform-owned shared. Recorded in ADRs 0002, 0013, 0008 (status unchanged, folded in on review).

- **2026-10-09 (Viedoc)** — Investigated a real regulated customer (Viedoc / Project Daybreak):
  `docs/use-cases/viedoc-daybreak.md`. Raw sources stay local (gitignored). Added open questions C53, E52,
  E53, F54, F55, F56, B57, H58. MVP revised: release sets (S13, S13b), qualification record (S17), second
  environment + approvals (S18), promotion (S19), E2E is now S20. Decisions for the user: Bicep backend
  (vs 0002/0003), App Service runtime (vs 0013), deployment set as the unit of release.

- **2026-10-09 (later)** — S02, S03, S04 and S04a merged (PRs #3–#6). Each needed exactly one fix round for
  real issues (false cascading errors, an ignored schema keyword weakening `grantable`, uncommented `!`, a
  YAML-error crash regression). Golden values pin name-hash and guid outputs. Paused before S05 at the
  user's request. Process: lead deletes branches only after a confirmed MERGED state (PR #4 was briefly
  closed by cleanup after a failed merge; recovered).

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
