# Work status

> Single source of truth for where the MVP stands. **Read this first every session; update it before
> the session ends** (lead only). Plan and slice briefs: [mvp.md](mvp.md).

## Now

- **Next slice:** S16b2 — deploy steps in the orchestrator + HTTP probe adapter (design approved; see the S16b2
  carry-forward note for the full plan). Then S16c.
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
| S13b | Deploy a release set (infra) | done | PR #24 | 2 | `jd release deploy`: resolve + template-check every workload first, then deploy in set order, stop at the first failing workload. Round 1: 1 blocking (one-pass error reporting untested); lead also required a shared argument reader (size), deduped template errors, and an Azure run guard (`JD_AZURE_TESTS=1` + per-host `flock` in `verify.sh --azure`) after an agent's unfiltered `dotnet test` reached Azure and collided with the lead's gate. Azure gate green (CLI 83/83, 49 min, nothing left). |
| S14 | Sample app | done | PR #16 | 2 | Done ahead of S11–S13 (independent). Round 1: 2 blocking (`/health` false-healthy on a missing container; unsynchronised shared Cosmos client → leaks/flapping). Image `ghcr.io/sleepyshark85/just-deliver-sample-app@sha256:267b1385…` — public (anonymous pull verified 2026-10-09). |
| S15a | `container-app` template | done | PR #25 | 1 | Split from S15. Developer stopped at the risk as briefed: Pulumi YAML cannot turn a map into a list, so the template takes `variables` as `List<Map<String>>` (`{name, value}`) and the engine converts (S15c). APPROVE first round. Azure gate green (CLI 84/84, real Container App: Multiple, SystemAssigned, min 0). |
| S15b | Runtime mapping + `runtime` node | done | PR #26 | 1 | APPROVE first round. Runtime mapping from data, reserved `runtime` node at `@workload`, phase `Runtime` (not deployed until S16), grants now edge to it, probe as data. Review hazards (image/port missing in the second pass; runtime-mapping node current id; `runtime.*` refs unchecked by TemplateLibrary) moved into S15c Part 0. Azure gate: all green except `DeployAzureTests`, which failed under memory pressure (an unrelated 8.8 GB process + a parallel review run) and passed alone (2 min, nothing left) — treated as load-related, cause unconfirmed. |
| S15c | Variables + `runtime.*` policies | done | PR #28 | 5 | Design note approved first. Rounds 1–3: 7 blocking, all security/correctness edges of a cross-scope export mechanism broader than the contract (full expression language in variables; grant bypass of the grantable check via exports, static and pending; `fn::entries` anywhere; same-name outputs across scopes; export crash path). Escalated after round 3; user kept Sonnet/Opus with stricter briefs and narrowed the design (only `runtime` reads `${resource.*}`; exports cannot; `fn::entries` top-level only; string values; second pass by scope). Round 4: second regex grammar → round 5: one grammar. APPROVE. Production +390/−125, over the cap (defensive checks, accepted). Azure gate green (CLI 85/85, nothing left). **S15 complete.** |
| S16a | `container-app` revision + traffic inputs | done | PR #29 | 1 | Spike first: a JSON list keeps its types through Pulumi config (`weight` integer, `latestRevision` boolean; provider rejects text). `traffic` (`List<Object>`), `revisionSuffix`, `maxInactiveRevisions` are inputs; mapping placeholders until S16b. Azure test shared setup (`ProbeAzureTest`). APPROVE first round; Azure gate green (CLI 86/86, dark revision got 0%, shift created no revision). |
| S16b1 | Release data for the deploy steps | done | PR #30 | 1 | Split from S16b after a design note (est. 330 lines). Runtime mapping `release:` block (8 names: engine-driven inputs, read outputs, traffic-item keys), probe `timeoutSeconds`/`intervalSeconds`, load validation, template `traffic` echo output, `TemplateLibrary` waiver + checks. Walk still never deploys the runtime. APPROVE first round; Azure gate green. |
| S16b2 | Deploy steps (orchestrator) + HTTP probe | todo | | | See carry-forward note. |
| S16c | HTTP probe + E2E Azure test | todo | | | Sample release set live on dev; a revision failing `/health` never gets traffic. |
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
- **S16:** the sample app assumes partition key `/id` (the database mapping provides it).
- **S15c review (minor):** errors GraphBuilder raises on a policy-set value point at the node field, not the policy path
  as written; a second-pass export failure names the node id instead of the mapping file; runtime-mapping nodes other than
  `runtime` get policy-added node names in scope (only `runtime` needs them).
- **S16b2 (approved design, from the S16b design note):**
  - New internal `src/jd.orchestrator/RuntimeRelease.cs` used by `Walk`; walk order becomes infrastructure nodes →
    runtime → after-runtime nodes (topological order can put the runtime before unrelated infra). `WaitingForRuntime`
    goes away unless still used. The runtime `NodeReport` is last and carries `Steps` (`StepReport(Dark|Grants|Probe|Shift,
    Done|Planned|Skipped|Failed, Detail)`); a failed step makes it `Failed` with `<step>: <reason>`; `DeployFormatter`
    prints one line per step.
  - Suffix: `"r"` + first 10 hex of SHA-256 over the canonical filled runtime parameters (key, value, secret flag,
    ordinal), excluding `suffixInput`/`trafficInput`; named consts citing Azure's suffix rule.
  - Live revision: `GetOutputsAsync(runtime stack)` → last `trafficOutput` item with `revisionKey`; none / only
    `latestKey` → no live revision. New revision name: the dark deploy's `revisionOutput` (verify live in S16c).
  - Unchanged: dark deploy reports no changes AND new revision == live → runtime `Unchanged`, no probe/shift; after-runtime
    nodes still deploy. (A re-run right after a first deploy re-probes once — accepted.)
  - Steps: dark (traffic = live at 100, or latest at 100 on first deploy) → grants → probe via `IRevisionProbe.ProbeAsync(fqdn,
    probe, ct)` → `ProbeResult(Passed, Detail)`, orchestrator retries up to `1 + timeout/interval` with an injected delay
    (fixed interval) → shift (new revision at 100; skipped on first deploy). Preview: dark package preview only, steps
    `Planned`, probe never called.
  - **HTTP probe adapter in S16b2** (~20 lines, CLI composition root): once the runtime deploys, `EnvUpAzureTests` really
    deploys the sample apps via `jd release deploy`, so a refusing probe would break the gate. Azure tests must pass in
    the lead's gate; `WriteRuntimeStandIn()` (no-resource template, fqdn `example.invalid`) must change accordingly.
  - Required tests (all in the S16b brief, kept here): dark never sends traffic to the new revision when a live one exists;
    no shift after a probe failure (attempts = 1 + timeout/interval, delays = interval); probe target only the FQDN output
    + mapping path; order dark → grants → probe → shift and a grant failure stops before probe/shift; same-config re-run
    unchanged; re-run after probe failure probes and shifts with no duplicate revision; first deploy latest, probe, no
    shift; release set stops after a probe failure; preview deploys/probes nothing; suffix changes with image/variable
    and ignores suffix/traffic entries.
  - **S16b1 review:** a workload-scope policy can still `set`/`default` `revisionSuffix`/`traffic` on the runtime node
    after load → make `TemplateLibrary.Check` reject engine-owned keys in a resolved runtime node's config (test). Minor:
    double pattern match in `TemplateLibrary.cs:115-116`; the missing-name test in `CatalogParserTests.cs:314` should
    remove the key and assert `release.<name>` only.
  - Cap ~215 production lines; design note already approved.
- **S16b (S16a review):** Multiple mode never deactivates 0%-traffic revisions; `maxInactiveRevisions` only bounds
  deactivated ones. Decided: no deactivation in the MVP (Pulumi YAML cannot express the action; they scale to zero) —
  document as a known limitation. Add an offline assertion that the `traffic` backend parameter keeps its types.
  `ContainerAppAzureTests` builds two stacks; fold the dark/shift steps into one test when next touched; neutral tag.
- **S16:** repeat the second-pass assertions (pending variable, secret masking) on the real deployed `runtime` node.
- **S15c/S16 (S15a review):** a secret value in `variables` stays encrypted in Pulumi state but is plain text in the
  Container App's `env` (readable with Reader) — secret values should become Container App `secrets` + `secretRef`
  (ADR 0013 runtime port). Add an offline check that a secret `variables` entry is masked in preview.
- **Tests (S15a review, minor):** `ContainerAppAzureTests` duplicates ~60 lines of `DeployAzureTests` setup — share a
  helper when next touching them; CI downloads azure-native on every run (consider caching `~/.pulumi/plugins`);
  seed templates do not pin the azure-native version.
- **Briefs:** cap production lines (~250) separately from tests; a cohesive slice's tests should not be trimmed to fit.
- **S15:** add a test workload variable reading `${resource.database.engine}` to show the engine export reaching the app (S12a review suggestion).
- **Azure test (S13b suggestion):** the per-container throughput check asserts only a non-zero `az` exit; also assert Azure's
  error text once seen in a run.
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

- **2026-10-10 (S15c blocked)** — S15c hit 3 review rounds without APPROVE; each round the Opus reviewer found new
  edge cases (probe-confirmed) in the cross-scope export mechanism the Sonnet developer built. Model-trial trigger met
  (a slice needing 3 rounds; two rounds broader than the contract). Lead proposal to the user: narrow the design —
  only the `runtime` node may read `${resource.*}`; mapping exports may not contain `${resource.*}` (load error);
  `fn::entries` only as a top-level field and only string values beneath it; the orchestrator keys known outputs by
  scope — and run the fix round (and S16+) with a different developer model. Host note: an unrelated `gremlins`
  mutation-testing job uses ~8.8 GB of 16 GB; run review and Azure gates one at a time.
  **User decision:** keep Sonnet developer / Opus reviewer with stricter briefs (every security boundary listed as a
  required test; narrowest surface stated, everything else an error); narrow the S15c design. Rounds 4–5 under the
  narrowed design → APPROVE; S15c merged (PR #28). S15 complete. S16 split a/b/c; S16a merged (PR #29, 1 round).
  S16b design note → split into S16b1/S16b2; S16b1 merged (PR #30, 1 round). Session stopped at the user's request
  after S16b1. Agent worktrees sometimes vanish mid-task (the harness removes them); developers recreate one — check
  the main checkout is on `main` before each merge.

- **2026-10-09 (team identity)** — Restarted with the `ARM_*` team identity; personal `az` session logged out. S12 Azure
  gate re-run as the team identity: green, nothing left behind. Azure test runs take ~50 min, mostly Cosmos account
  deletion (~20 min per account) — run them in the background. S12 merged (PR #22); S12a merged (PR #23), approved
  first round. Force-push is blocked for the lead: catch a slice branch up with `git merge main`, not rebase.
  S13b merged (PR #24, 2 rounds). Incident: with `ARM_*` in the session, an agent's plain `dotnet test` ran the Azure
  tests, created the free-tier Cosmos account and died mid-teardown; the lead's gate then collided. Cleaned up; guard
  added (Azure tests only via `tools/verify.sh --azure`, one run per host). Agents must not run Azure tests.
  S15 split into S15a/b/c; S15a merged (PR #25, 1 round). A developer experiment with the Pulumi CLI created an
  ephemeral Pulumi Cloud agent account (unclaimed, expires ~3 days); our state stays on the file backend.
  S15b merged (PR #26, 1 round). Don't run the reviewer's offline gate and the Azure gate at the same time on this
  16 GB host when other heavy jobs run — it slowed Pulumi tests 30× and likely caused one Azure test failure.

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
