# MVP plan

> Status: active plan. Progress is tracked in [status.md](status.md), not here.

## Goal

Prove the core loop with nothing hard-coded: a team declares a workload, one command resolves it
**from catalog data** into a resource graph, provisions it on Azure and deploys the app with
identity-based access — inside the free tier.

The MVP must prove six things. Items 5 and 6 were added after the [Viedoc use case](../use-cases/viedoc-daybreak.md)
(2026-10-09): a real regulated suite releases many workloads as one pinned set and needs evidence and
approvals, so the MVP proves the shape of that early, when it is cheap to change.

1. **Data-driven resolution** — `requires` becomes a graph from catalog files; no Azure knowledge in C#
   ([resolver.md](../architecture/resolver.md), C52).
2. **Identity without secrets** — system-assigned identity, grants, dark revision, traffic shift only
   after grants work ([0012](../decisions/0012-system-assigned-identity.md), [0013](../decisions/0013-container-apps-runtime.md), D21).
3. **Multi-workload safety** — two workloads share an environment without collisions.
4. **Acceptable deploy latency** — infra is reconciled on every deploy ([0006](../decisions/0006-infra-deployed-with-app.md), H47).
5. **A release is a pinned set** — several workloads under one label, deployed and switched in dependency
   order (C53).
6. **Evidence and promotion** — every deploy records IQ/OQ qualification with machine identity, and the same
   set is promoted to a second environment only behind a recorded approval (E52, F54, E34).

## Scope

| In | Out (deferred deliberately) |
|---|---|
| Two environments, `dev` (tier team) and `stage` (tier protected), one subscription ([sandbox](../../tools/sandbox/README.md)), one region from `JD_REGION` | Landing zone, platform identity model (D19), more environments, multi-region (E53) |
| Substrate from data per environment: resource group, Container Apps Environment, a Cosmos SQL database (400 RU/s shared throughput); one Log Analytics workspace and one free-tier Cosmos account shared by both environments | Substrate versioning (B7) |
| One resource type `database` (platform maps it to Cosmos SQL): a container per workload in its environment's database, grant scoped to that container (free tier: 2 environments × 400 RU/s ≤ 1,000) | Other types, `class` values beyond default, applying overrides (rejected for now) |
| One policy `enforce-monitoring`: per-workload App Insights | More policies — adding one is a test of goal 1, not scope |
| Container App, system-assigned identity, multiple-revision mode; image on a public registry | ACR and the ACR-pull identity (0012 follow-up) |
| Release set: label + pinned workload definitions + image digests + workload-level `dependsOn` | Work-item scope, build-evidence contract (F56) |
| Steps per workload, in dependency order: infra → dark revision → grants → OQ probe on the dark revision (retry to timeout) → traffic shift | Hook jobs and migrations (0014, 0015) — Cosmos needs no DDL |
| Qualification record per deploy: IQ (refresh shows no drift; running digests equal pinned) and OQ (probe declared in the catalog), with machine identity | Smoke/PQ suites, release documents and VIRP (F55) |
| Approval policy as data per environment tier; `jd approve` records who, when and meaning; `jd promote` deploys the same set to `stage` only when approved and qualified | Entra group resolution, e-signature integration (F54), notifications |
| CLI `jd validate`, `jd preview`, `jd release create`, `jd deploy`, `jd approve`, `jd promote`, `jd env up` | API, web UI, queue, durable steps (E29), rollback, destroy command |
| Local release record (label, definition SHAs, catalog version, digests, graph, qualification, approvals, outcome, phase timings) | Audit store (F39) |
| Local Pulumi state | Blob state |

**Intentional, temporary deviations from proposed ADRs:** platform health check instead of `verify`
jobs (0014); `protect` not applied (0010 — sandbox only); local state and in-process loop (0002).
**Assumption:** proving the D-stage engine before the B/C tracker is accepted (tension noted in 0005).

## Success criteria

1. `jd deploy` on the sample workload → app live, reads/writes Cosmos via managed identity, telemetry
   in App Insights; no key or connection string anywhere.
2. A release set of two workloads (one depending on the other) deploys in order, each switched only after
   its OQ passes; the set's second workload causes zero changes to the first.
3. Re-running with no change reports zero resource changes.
4. A new image is deployed dark and gets traffic only after the health check passes; a failing image
   never receives traffic.
5. A new policy added as catalog files only (no C# change) shows in `jd preview`.
6. Deploy time measured and recorded.
7. Every deploy leaves a qualification record (IQ and OQ) on the release record; an IQ fails when a running
   image differs from the pinned digest.
8. `jd promote` refuses a set that is unqualified or unapproved, and deploys the identical set to `stage`
   once a required approval is recorded.
9. Every resource stays within the free tier; all test resources torn down.

## Slices

One slice = one branch = one PR (see [standards §7](../engineering/standards.md)). Slices run in order
unless "Depends" allows otherwise. Each brief below is what the lead hands the developer, expanded
with file-level detail at dispatch time.

### M1 — Resolver, offline (no Azure)

| ID | Slice | Depends | Acceptance |
|---|---|---|---|
| S01 | **Workload schema v2** (H50): `requires[].id` (default = type, unique, pattern `^[a-z][a-z0-9-]{1,14}[a-z0-9]$`), `class` (default `standard`), drop `metadata.environment`, type `cosmos-sql`, refs `${resource.<id>.<output>}`; reject `overrides` for now | — | Validator tests for each rule; sample workloads updated |
| S02 | **Catalog formats + loader**: JSON Schemas for `catalog.yaml`, type, mapping, policy, naming, roles; `jd.resolver` project with a loader that validates and reports file+path errors; seed catalog for `cosmos-sql` and `runtime` | S01 | Invalid catalog files fail with actionable errors; seed catalog loads |
| S03 | **Environment descriptor**: format + schema + loader (region, tier, substrate outputs, grantable resources) | S02 | Loader tests; descriptor generated in S12 |
| S04 | **Expression evaluator**: `${…}` substitution, typed references for node outputs, built-ins `name()` (from `naming.yaml`), `guid()` (UUIDv5), `role.*` | S02, S03 | Pure unit tests incl. unknown references and naming length limits |
| S05 | **Matching + expansion**: specificity ordering, ties are errors, mapping → nodes, type `exports` contract checked | S04 | Tests: match, tie, missing mapping, missing export |
| S06 | **Policies + provenance**: `default`/`set`/`add`, layering template < mapping < policy, per-field provenance; seed `enforce-monitoring` | S05 | Tests for precedence and provenance; policy adds App Insights node |
| S07 | **Graph builder**: edges from references, cycle detection, topological order, phase derivation (`runtime.*` refs ⇒ after revision), stack names, node hashes; cross-workload references rejected | S06 | Golden test: sample workload → expected graph snapshot |
| S08 | **CLI skeleton**: `jd validate`, `jd preview` (offline graph + provenance) as composition root only | S07 | Both commands work on the sample; no logic in CLI |

### M2 — Infra path (Azure, free tier)

| ID | Slice | Depends | Acceptance |
|---|---|---|---|
| S09 | **Template library**: move to `catalog/templates/azure/`; split `cosmos-db` → `cosmos-account` + `cosmos-sql-database` (substrate) + `cosmos-sql-container`; mapping creates the container and a container-scoped grant; add `container-apps-environment`, `resource-group`, `log-analytics` (daily cap); remove opinionated defaults and fixed GUIDs; delete `app-service` | S02 | Catalog CI check: every mapping config key exists in its template; templates have no platform defaults |
| S10 | **Provider fixes** in `jd.core`/`jd.bp.pulumi`: stack name separate from template, preview with unknowns (no fake values), cancellation | S07 | Unit tests; existing behaviour kept |
| S11 | **Orchestrator (infra nodes)**: walk graph in order, pass outputs into dependants' config, capture outputs | S09, S10 | Azure test: sample workload infra up, re-run = zero changes, cleanup |
| S12 | **Substrate from data**: `jd env up dev` resolves an environment definition through the same engine and writes the descriptor from outputs | S11 | Azure test: substrate up; descriptor written; free-tier rules met |
| S12a | **`database` type (user decision)**: rename the `cosmos-sql` type to `database`; the platform's mapping decides the engine (today Cosmos SQL); add an `engine` export; sample workloads, golden snapshots, docs and C52 updated. An engine change for an existing workload must later route to approval (F54) | S12 | Golden graphs show `database` resolving to the Cosmos container; `engine` exported |
| S13 | **Release set (offline)**: format + schema + loader (label, workloads: definition ref + image digest + `dependsOn`), validation (unknown workload, cycles, duplicate ids), resolve each workload, deploy order; `jd release create` writes the set | S08 | Tests: order, cycle, unknown dependency; golden set for two sample workloads |
| S13b | **Deploy a release set (infra)**: orchestrator walks the set in order on `dev` | S12, S13 | Azure test: two workloads, no collisions, RU/s budget respected |

### M3 — Runtime and identity

| ID | Slice | Depends | Acceptance |
|---|---|---|---|
| S14 | **Sample app**: .NET minimal API, `/health`, Cosmos read/write via `DefaultAzureCredential`, retries auth on startup; Dockerfile; published to a public registry | — | Runs locally; image published |
| S15 | **Runtime mapping + `container-app` template**: system identity, multiple-revision, consumption, `minReplicas: 0`; env vars from resolved exports; the OQ probe (path, expected status) declared in the runtime mapping | S12, S14 | Preview shows runtime node, env wiring and probe |
| S16 | **Deploy steps**: per workload in set order: dark revision → grants → OQ probe with retry → traffic shift; failure leaves traffic on the previous revision and stops dependants | S13b, S15 | Azure test: good set goes live in order; a bad image never gets traffic |

### M4 — Evidence and promotion

| ID | Slice | Depends | Acceptance |
|---|---|---|---|
| S17 | **Release record + qualification**: record per environment (label, SHAs, catalog version, digests, graph, outcome, phase timings, machine identity); IQ = refresh with no drift + running digests equal pinned; OQ results from S16 | S16 | Azure test: record written; IQ fails on a digest mismatch |
| S18 | **Second environment + approvals**: `stage` substrate (tier protected) via `jd env up`; approval policy as data per tier; `jd approve <label> --env stage --meaning <text>` records identity and time | S12, S17 | Tests: policy evaluation; approval recorded and immutable |
| S19 | **Promotion**: `jd promote <label> --to stage` refuses unqualified or unapproved sets, then deploys the identical set (same digests, same SHAs) | S18 | Azure test: refusal without approval; promotion after approval; records on both environments |

### M5 — End to end

| ID | Slice | Depends | Acceptance |
|---|---|---|---|
| S20 | **E2E run and docs**: all success criteria verified on the sandbox; usage guide; teardown | S19 | Success criteria 1–9 evidenced in status.md |

## Known blockers to resolve when reached

- **S14:** publishing the image needs push credentials for a public registry (GHCR or Docker Hub).
  Escalate to the user if none are available.
