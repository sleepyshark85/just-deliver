# MVP plan

> Status: active plan. Progress is tracked in [status.md](status.md), not here.

## Goal

Prove the core loop with nothing hard-coded: a team declares a workload, one command resolves it
**from catalog data** into a resource graph, provisions it on Azure and deploys the app with
identity-based access — inside the free tier.

The MVP must prove four things:

1. **Data-driven resolution** — `requires` becomes a graph from catalog files; no Azure knowledge in C#
   ([resolver.md](../architecture/resolver.md), C52).
2. **Identity without secrets** — system-assigned identity, grants, dark revision, traffic shift only
   after grants work ([0012](../decisions/0012-system-assigned-identity.md), [0013](../decisions/0013-container-apps-runtime.md), D21).
3. **Multi-workload safety** — two workloads share an environment without collisions.
4. **Acceptable deploy latency** — infra is reconciled on every deploy ([0006](../decisions/0006-infra-deployed-with-app.md), H47).

## Scope

| In | Out (deferred deliberately) |
|---|---|
| One environment `dev`, one subscription ([sandbox](../../tools/sandbox/README.md)), region from `JD_REGION` | Landing zone, platform identity model (D19), more environments, promotion |
| Substrate from data: resource group, Log Analytics, Container Apps Environment, one shared free-tier Cosmos account | Substrate versioning (B7) |
| One resource type `cosmos-sql`: database + container per workload on the shared account, grant scoped to the workload's database | Other types, `class` values beyond default, applying overrides (rejected for now) |
| One policy `enforce-monitoring`: per-workload App Insights | More policies — adding one is a test of goal 1, not scope |
| Container App, system-assigned identity, multiple-revision mode; image on a public registry | ACR and the ACR-pull identity (0012 follow-up) |
| Steps: infra → dark revision → grants → health check on dark revision (retry to timeout) → traffic shift | Hook jobs and migrations (0014, 0015) — Cosmos needs no DDL |
| CLI `jd validate`, `jd preview`, `jd deploy`, `jd env up` | API, web UI, approvals, queue, durable steps (E29), rollback, destroy command |
| Local deployment record (definition SHA, catalog version, image, graph, outcome, timings) | Audit store |
| Local Pulumi state | Blob state |

**Intentional, temporary deviations from proposed ADRs:** platform health check instead of `verify`
jobs (0014); `protect` not applied (0010 — sandbox only); local state and in-process loop (0002).
**Assumption:** proving the D-stage engine before the B/C tracker is accepted (tension noted in 0005).

## Success criteria

1. `jd deploy` on the sample workload → app live, reads/writes Cosmos via managed identity, telemetry
   in App Insights; no key or connection string anywhere.
2. A second workload deploys into the same environment with zero changes to the first.
3. Re-running with no change reports zero resource changes.
4. A new image is deployed dark and gets traffic only after the health check passes; a failing image
   never receives traffic.
5. A new policy added as catalog files only (no C# change) shows in `jd preview`.
6. Deploy time measured and recorded.
7. Every resource stays within the free tier; all test resources torn down.

## Slices

One slice = one branch = one PR (see [standards §7](../engineering/standards.md)). Slices run in order
unless "Depends" allows otherwise. Each brief below is what the lead hands the developer, expanded
with file-level detail at dispatch time.

### M1 — Resolver, offline (no Azure)

| ID | Slice | Depends | Acceptance |
|---|---|---|---|
| S01 | **Workload schema v2** (H50): `requires[].id` (default = type, unique, pattern `^[a-z][a-z0-9-]{1,14}[a-z0-9]$`), `class` (default `standard`), drop `metadata.environment`, type `cosmos-sql`, refs `${resource.<id>.<output>}`; reject `overrides` for now | — | Validator tests for each rule; sample workloads updated |
| S02 | **Catalog formats + loader**: JSON Schemas for `catalog.yaml`, type, mapping, policy, naming, roles; `jd.resolver` project with a loader that validates and reports file+path errors; seed catalog for `cosmos-sql` and `runtime` | S01 | Invalid catalog files fail with actionable errors; seed catalog loads |
| S03 | **Environment descriptor**: format + schema + loader (region, tier, substrate outputs, grantable resources) | S02 | Loader tests; a sample descriptor |
| S04 | **Expression evaluator**: `${…}` substitution, typed references for node outputs, built-ins `name()` (from `naming.yaml`), `guid()` (UUIDv5), `role.*` | S02, S03 | Pure unit tests incl. unknown references and naming length limits |
| S05 | **Matching + expansion**: specificity ordering, ties are errors, mapping → nodes, type `exports` contract checked | S04 | Tests: match, tie, missing mapping, missing export |
| S06 | **Policies + provenance**: `default`/`set`/`add`, layering template < mapping < policy, per-field provenance; seed `enforce-monitoring` | S05 | Tests for precedence and provenance; policy adds App Insights node |
| S07 | **Graph builder**: edges from references, cycle detection, topological order, phase derivation (`runtime.*` refs ⇒ after revision), stack names, node hashes; cross-workload references rejected | S06 | Golden test: sample workload → expected graph snapshot |
| S08 | **CLI skeleton**: `jd validate`, `jd preview` (offline graph + provenance) as composition root only | S07 | Both commands work on the sample; no logic in CLI |

### M2 — Infra path (Azure, free tier)

| ID | Slice | Depends | Acceptance |
|---|---|---|---|
| S09 | **Template library**: move to `catalog/templates/azure/`; split `cosmos-db` → `cosmos-account` + `cosmos-sql-database`; add `container-apps-environment`, `resource-group`, `log-analytics` (daily cap); remove opinionated defaults and fixed GUIDs; delete `app-service` | S02 | Catalog CI check: every mapping config key exists in its template; templates have no platform defaults |
| S10 | **Provider fixes** in `jd.core`/`jd.bp.pulumi`: stack name separate from template, preview with unknowns (no fake values), cancellation | S07 | Unit tests; existing behaviour kept |
| S11 | **Orchestrator (infra nodes)**: walk graph in order, pass outputs into dependants' config, capture outputs | S09, S10 | Azure test: sample workload infra up, re-run = zero changes, cleanup |
| S12 | **Substrate from data**: `jd env up dev` resolves an environment definition through the same engine and writes the descriptor from outputs | S11 | Azure test: substrate up; descriptor written; free-tier rules met |
| S13 | **Second workload** | S12 | Azure test: two workloads, no collisions, RU/s budget respected |

### M3 — Runtime and identity

| ID | Slice | Depends | Acceptance |
|---|---|---|---|
| S14 | **Sample app**: .NET minimal API, `/health`, Cosmos read/write via `DefaultAzureCredential`, retries auth on startup; Dockerfile; published to a public registry | — | Runs locally; image published |
| S15 | **Runtime mapping + `container-app` template**: system identity, multiple-revision, consumption, `minReplicas: 0`; env vars from resolved exports | S12, S14 | Preview shows runtime node and env wiring |
| S16 | **Deploy steps**: dark revision → grants → health check with retry → traffic shift; failure leaves traffic on previous revision | S15 | Azure test: good image goes live; bad image never gets traffic |

### M4 — End to end

| ID | Slice | Depends | Acceptance |
|---|---|---|---|
| S17 | **`jd deploy` + deployment record + timings** | S16 | Record written per run; timings recorded |
| S18 | **E2E run and docs**: all success criteria verified on the sandbox; usage guide; teardown | S17 | Success criteria 1–7 evidenced in status.md |

## Known blockers to resolve when reached

- **S14:** publishing the image needs push credentials for a public registry (GHCR or Docker Hub).
  Escalate to the user if none are available.
