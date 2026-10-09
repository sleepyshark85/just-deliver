# Work status

> Single source of truth for where the MVP stands. **Read this first every session; update it before
> the session ends** (lead only). Plan and slice briefs: [mvp.md](mvp.md).

## Now

- **Next slice:** S13b — deploy a release set (infra).
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
| S11 | Orchestrator (infra nodes) | done | PR #20 | 2 | First Azure slice. Round 1: 2 blocking (teardown failures hidden; silent empty passphrase); backend URL now required. Azure test found a bug in our own sandbox policy (int vs float compare) — fixed (PR #15), re-applied by the user; test green, nothing left behind. |
| S12 | Substrate from data | done | PR #22 | 2 | Round 1: 3 blocking (substrate/workload stack collision → owner kind with `@` env owner; secret-output filter untested and in the CLI; `!`). Azure gate green as the team identity (CLI 64/64, 49 min incl. Cosmos teardown; free tier on, 1000 RU/s cap, 400 RU/s db, re-run unchanged, nothing left). |
| S12a | `database` type (user decision) | done | PR #23 | 1 | APPROVE first round, 0 production C# lines (catalog, schema, samples, goldens, docs). Default requirement id is now `database`, so container and stack names changed (`…-database-623014`) — harmless now (nothing live), but a type/engine change on a deployed workload recreates its container (F54). |
| S13 | Release set (offline) | done | PR #18 | 2 | Round 1: 4 blocking — **release sources never committed** (old VS `.gitignore` rule `[Rr]elease/`; local gate passed because files existed on disk — caught by the reviewer's clean worktree); set fields could be tampered (now derived from verified definitions); BOM/invalid UTF-8; duplicated topological sort (shared now). |
| S13b | Deploy a release set (infra) | todo | | | |
| S14 | Sample app | done | PR #16 | 2 | Done ahead of S11–S13 (independent). Round 1: 2 blocking (`/health` false-healthy on a missing container; unsynchronised shared Cosmos client → leaks/flapping). Image `ghcr.io/sleepyshark85/just-deliver-sample-app@sha256:267b1385…` — **package must be made public by the user**. |
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
- **S10/S12:** template `fn::invoke`s (e.g. `getSharedKeys`) run during preview, so previewing a brand-new
  environment fails until its workspace exists — preview substrate in dependency order, or tolerate it.
- **S15:** the runtime mapping sends `runtime.appInsightsConnectionString` to the app's `APPLICATIONINSIGHTS_CONNECTION_STRING`
  (Azure Monitor SDK's own name, not in the workload's variables). The app assumes partition key `/id`.
- **Briefs:** cap production lines (~250) separately from tests; a cohesive slice's tests should not be trimmed to fit.
- **S15:** add a test workload variable reading `${resource.database.engine}` to show the engine export reaching the app (S12a review suggestion).
- **Teardown:** per-workload Cosmos containers live in the substrate account; MVP test teardown removes everything
  with `tools/azure/cleanup.sh --yes --all` at the end (no per-workload resource groups — decided in S11's brief).
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

- **2026-10-09 (team identity)** — Restarted with the `ARM_*` team identity; personal `az` session logged out. S12 Azure
  gate re-run as the team identity: green, nothing left behind. Azure test runs take ~50 min, mostly Cosmos account
  deletion (~20 min per account) — run them in the background. S12 merged (PR #22); S12a merged (PR #23), approved
  first round. Force-push is blocked for the lead: catch a slice branch up with `git merge main`, not rebase.

- **2026-10-09 (before restart)** — User decisions: workloads ask for `database`, the platform team's catalog mappings
  decide the engine (Cosmos today) → S12a; Kubernetes (cloud or on-prem) is possible later via a runtime adapter +
  catalog data, not scheduled. Session ends so the user can restart Claude Code with the team identity loaded.

- **2026-10-09 (S11 on Azure)** — First real deployment through `jd`: resource group + capped workspace, outputs
  passed, re-run zero changes, nothing left behind. User asked about Kubernetes support: answered (runtime adapter +
  catalog data + identity decision + test cluster); not scheduled unless the user asks.

- **2026-10-09 (repo public)** — The user made the repository public. GitHub Actions now runs (`verify` green) and
  `main` is protected server-side: PR only, `verify` required and up to date, no force-push, admins included,
  linear history. Merges wait for CI (`gh pr checks --watch`).

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
