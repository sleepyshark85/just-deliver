# Work status

> Single source of truth for where the MVP stands. **Read this first every session; update it before
> the session ends** (lead only). Plan and slice briefs: [mvp.md](mvp.md).

## Now

- **Next slice:** S18c — `jd approve` (approvals.md §4, §6–8 S18c). S18b needed 3 rounds (a trial stop condition); asked, the user chose to keep the Sonnet developer.
- **For the user's review (lead decisions, S17 design):** `jd deploy` writes no record (a release is the unit); `JD_RECORDS_DIR`
  required; new adapter project `jd.azure` (standards §1 exception for Azure protocol constants, §3 row); IQ failure exits 1 and
  leaves the deployment live; ADR 0007 kept (the set file's bytes are stored write-once as `set.yaml` per label).
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
| S16b2 | Deploy steps (orchestrator) + HTTP probe | done | PR #32 | 3 | Round 1: 1 blocking — **design gap the lead approved**: the first deploy left traffic as `latestRevision`, so state never named a revision and every later deploy sent all traffic to the unprobed revision; fixed by always shifting after a passed probe (first deploy included; first deploy is the documented exception). Round 2: 1 blocking — the HTTP probe followed redirects (a revision could pass via a 302 elsewhere). APPROVE round 3. Production ≈ +390/−85 raw, ~210 net code lines (rest doc comments). Azure stand-in tests use a passing probe; `EnvUpAzureTests` runs the real probe. Azure gate green (CLI 96/96, 1 h 7 min, sample apps deployed and probed over HTTP; nothing left). |
| S16c | Revert-safe suffix + E2E Azure test | done | PR #33 | 1 + suggestions | Lead spike first (Azure rejects any template change reusing an existing revision suffix, even a revert). Lead decision: suffix = `r` + 8 hex over params + template content + 6 hex salt from the previous suffix (reused when the config part matches); `release.suffixOutput` from data. Azure test: a bad `sample-worker` (missing container → 503) fails its probe, traffic stays 100% on the good revision, the app behind it is untouched; revert deploys a new revision. ~40 production lines. APPROVE first round; 3 suggestions applied. Azure gate green (CLI 96/96, 1 h 2 min, nothing left). **S16 complete.** |
| S17a | Design note + deployer identity | done | PR #34 | 2 | Design note `release-record.md` (split S17a/b/c, ~550 lines). Lead decisions recorded in Now. Identity port in `jd.core`, adapter `jd.azure` reads `tid`/`appid`/`oid` from the ARM token. Round 1: 1 blocking — port in `jd.orchestrator` made `jd.azure` depend on `jd.resolver` transitively, against its own standards row; plus secret in a record's `ToString`, ARM scope URL vs §1, 5 design-note gaps (OQ `unchanged` evidence chain, refusal order, IQ time apart from deploy time). Azure gate green (CLI 98/98, 1 h 3 min, identity test live, nothing left). |
| S17b | Release record + OQ + timings | done | PR #35 | 2 | Record per attempt under `JD_RECORDS_DIR/<env>/<label>/`, write-once; `set.yaml` holds the set's bytes (hash-checked, ADR 0007); schema `release-record.schema.json` (`iq` to be added as optional in S17c); OQ from runtime steps; step/node/deploy timings; identity from the port; `AuthenticationFailedException` → clear CLI error. Round 1: 3 blocking — a `..` label crashed instead of a clear error, a probe with 0 attempts violated the schema, the preview-writes-nothing test was weak. ~233 production lines. Azure gate green (CLI 112/112, 1 h 6 min; good/bad/revert records, same-label refusal; nothing left). |
| S17c1 | IQ (drift + live images) + `jd release qualify` | done | [#40](https://github.com/sleepyshark85/just-deliver/pull/40) | 3 + final | Blocked after 3 rounds (each found new test gaps); user chose: lead finishes. Lead added the preview-failure test and round-3 hardening (jd.azure guard refuses Target/Import/UsingTask/Sdk and Directory.Build files, allowlists FrameworkReference; qualify records refuse deploy fields); one scoped final review (2 findings, fixed, APPROVE). Azure gate failed first: the new qualify step expected env name `dev` (test renames it) — drift on the runtime node **is** reported on Azure (`Update 1`), design note §7 confirmed. `verify.sh` now prints failure messages. Gate green: CLI 126/126, 1 h 6 min, nothing left. Not done (reasoned): narrowing the qualify `try` (Qualifier throws for a mapping without a release block). |
| S17c2 | IQ inside `jd release deploy` | done | [#41](https://github.com/sleepyshark85/just-deliver/pull/41) | 2 | **Trial, Sonnet developer:** round 1 found 3 blocking (a real-clock sleep in a test instead of the run's `TimeProvider`, duplicated provider/qualifier wiring, a schema line with no effect), fixed by a fresh Sonnet developer; round 2 APPROVE. Early Azure test (new rule) passed on the first head; gate green on the approved head: CLI 133/133, 1 h 6 min, nothing left. Older deploy records without `iq` no longer validate — no reader exists yet (release-record.md §1). |
| S18 | Second environment + approvals | in-progress | design [#42](https://github.com/sleepyshark85/just-deliver/pull/42) | 3 (design) | Design note `docs/architecture/approvals.md` (Sonnet developer, Opus reviewer; round 2 fixed by the fixer). User chose (a) ×3. **S18a done** (PR #43, 2 rounds; Sonnet developer, one blocking finding in round 1; 31 mutations all caught; Azure: `stage` up, `db-stage` 400 RU/s, limit 1000). **S18b done** (PR #44, 3 rounds; Sonnet developer, round-1 fixes and round 2 by the fixer; 5 → 2 → 0 blocking: literals shared with the record writer, an unguarded directory listing, surviving mutants, a host-dependent test; no design rework; ~142 lines, over the ~120 target; no Azure gate — no Azure-facing code, the first slice under the new gate rule). Built as S18a (policy, gate, `stage`, ~110), S18b (qualification reader, ~120), S18c (`jd approve`, ~200; reader may move to S19). |
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

- **S16b2/S16c review (minor, left):** pin "suffix distinguishes null from empty" with a test; the suffix hashes secret values into
  a public suffix (fine until secret variables become Container App secrets); `ContainerAppAzureTests.cs:180` hard-codes the ARM host
  (use a resource-id URL as `EnvUpAzureTests` does).
- **S17b review (minor, left):** `docs/usage.md` could list a malformed credential and a label the store rejects among the exit-1
  cases; an optional `release records:` prefix on directory IOExceptions; residual: a `set.yaml` truncated by a crash must be
  deleted by hand. **S17c (S17a review):** make `verify.sh`'s `jd.azure` reference check an allowlist (`jd.core` +
  `Azure.*` packages only).
- **Release flow residual (S16c, documented):** a dark deploy that fails after Azure created the revision (unpullable image) leaves
  the old suffix in outputs, so a same-config re-run is rejected until the config changes; traffic unaffected.
- **S16a review (left):** `ContainerAppAzureTests` builds two stacks; fold the dark/shift steps into one test when next
  touched; neutral tag. (Non-deactivation of 0%-traffic revisions is now documented; traffic types asserted offline.)
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
| Session start | Start Claude Code with the team identity loaded: `set -a; source ~/.just-deliver/ca89cbcc-e368-4e81-9e80-6686b4d9f3b9.env; set +a; claude`. Keep the personal Owner login out of the default `az` config (`AZURE_CONFIG_DIR=~/.azure-owner az login` for Owner tasks). Azure tests run only via `tools/verify.sh --azure` (lead only, ~2 h since S18a, one at a time). |
| Live Azure resources | none |

## Session log

Newest first. One entry per session: what moved, decisions taken, anything the next session must know.

- **2026-10-17 (S18b)** — S18b done in 3 rounds (third trial slice). Findings were code-bar items (duplicated record literals, a crash on an
  unlistable directory, two weak tests), not design; the fixer only partly fixed the literals in round 1, which cost round 2. Three rounds is a
  model-trial stop condition; asked, the user chose to keep the Sonnet developer for S18c.
- **2026-10-17 (S18a)** — S18a done in 2 rounds (second trial slice; one blocking finding, no design rework). The early Azure test
  ran 1 h 38 m: the Azure suite now brings up two environments (`dev`, `stage`), so budget ~2 h for the full gate.

- **2026-10-17 (S17c2, S18 choices)** — S17c2 done in 2 rounds (first trial slice: Sonnet code met the bar after one round of
  structural findings, no design rework). User took (a) for all three S18 open choices (policy in the environment definition,
  listed Entra object ids, approver = service principal; per-person sign-in deferred).

- **2026-10-17 (S17c1 done, S18 design)** — S17c1 finished by the lead (option a) and merged after a green gate; see its row. User
  agreed to parallel work: S18's design note ran beside S17c1's gate (developer Sonnet, reviewer Opus, fixer for round 2's finding; 3
  rounds, APPROVE). Its review removed a tier constant from code (the gate is the descriptor's `approval` block) and found NJsonSchema
  ignores `if`/`then` (use `anyOf`). Branch `mvp/s18-design` merges after the user answers its open choices. S17c2 brief:
  scratchpad `s17c2-brief.md` (lead re-creates it if the scratchpad is gone: release-record.md §7/§8, required tests from §8 S17c).

- **2026-10-17 (early Azure test)** — S17c1's gate failed in the end-to-end test's new qualify part, the first time it ran
  on Azure (the slice was blocked before any gate); the log named only the test. User agreed: `verify.sh` prints each
  failure's message (on the S17c1 branch), and a slice that adds or changes an Azure test gets that test run early by the
  lead, before review round 1 (conventions, "Azure is the lead's"; CLAUDE.md step 3). Auto-compact set to 300k by the user.

- **2026-10-17 (process)** — User chose (a)+(b) for blocked S17c1: the lead adds the missing Qualifier test and the
  round-3 hardening, then a final review and the Azure gate (in progress: test written in a worktree, not yet committed);
  models briefly Opus developer / Opus reviewer / Sonnet fixer; then the user set the developer back to **Sonnet** to
  test whether Sonnet code meets the new bar (from S17c2; trial judged on rounds per slice). Adopted from `~/sources/officina`: `conventions.md`
  (how we work + code quality bar + tests + design rules), one reviewer kept per slice for its rounds, a `fixer`
  agent, ten-line reports, mutation-proof tests as blocking, reviewer in a detached worktree, the context-size hook
  (150k/300k warnings, tests in `verify.sh`). Not adopted: PR-comment review gate and auto-merge (our PR opens after the
  Azure gate), parallel-agent file ownership.

- **2026-10-17 (S17c1 blocked)** — S17b merged (PR #35). S17c split into S17c1/S17c2 (lead). S17c1: 3 rounds without APPROVE →
  blocked per the workflow, waiting for the user. R3 suggestions to fold in when resumed: `verify.sh` also refuses `<Target>`/`<Import>`/
  `<UsingTask>` and `FrameworkReference` in `jd.azure.csproj`; schema `qualify` branch forbids the deploy-only fields; narrow the
  `try` in `ReleaseDeployCli.cs:149-159` to construction; re-wrap the `Orchestrator.cs:32-34` doc comment.

- **2026-10-17 (S17b)** — Release record + OQ + timings: 2 rounds, APPROVE; Azure gate green (real records for good, bad and
  revert deploys; SP identity). Trial: round-1 findings were robustness/test-strength, no design rework.

- **2026-10-17 (S17a)** — S17 design note written by the developer, reviewed with S17a; lead took the five open choices (recorded in
  Now for the user's review; no ADR deviation). S17a: 2 rounds, APPROVE; Azure gate green. Trial: design-level finding in round 1
  was real (transitive dependency), fixed cleanly.

- **2026-10-16/17 (S16b2 merged, S16c)** — S16b2 Azure gate green, merged (PR #32). Lead spike on Azure: a suffix naming an
  existing revision is rejected even for a revert, so the hash-only suffix broke recovery from a bad release; lead designed the
  revert-safe suffix (S16c brief). S16c: APPROVE round 1, suggestions applied, Azure gate green — a failing revision never got
  traffic on real Azure, revert recovered. **S16 complete.** Trial: S16c 1 round. User asleep; lead continues with S17.

- **2026-10-16 (S16b2)** — S16b2 built and reviewed in 3 rounds (APPROVE). Round 1 caught a gap in the lead-approved
  design (no live revision ever recorded after a first deploy); lead amended the design: Shift always runs after a passed
  probe. Round 2: probe followed redirects. Session was started without the team identity (`ARM_*` absent), so the Azure
  gate is pending; merge only after `tools/verify.sh --azure` is green. Trial: 3 rounds, both blocking findings real.
  Second session (team identity): a parallel S16b2 dispatch was superseded (no code); the user closed the other session.
  Azure gate green (CLI 96/96, 1 h 7 min, subscription empty after); S16b2 merged (PR #32).

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
