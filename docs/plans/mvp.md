# MVP plan

> Status: **done** (2026-10-18; phase 0 of the [roadmap](roadmap.md)). Evidence per criterion: [e2e-evidence.md](e2e-evidence.md).

## Goal

Prove the core loop with nothing hard-coded: a team declares a workload, one command resolves it
**from catalog data** into a resource graph, provisions it on Azure and deploys the app with
identity-based access — inside the free tier.

The MVP proves six things; 5 and 6 come from the [Viedoc use case](../use-cases/viedoc-daybreak.md), a regulated suite that
releases many workloads as one pinned set and needs evidence and approvals.

1. **Data-driven resolution** — `requires` becomes a graph from catalog files; no Azure knowledge in C#
   ([model.md](../architecture/model.md)).
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
| Two environments, `dev` (type `dev`) and `stage` (type `stage`, approval-gated), one subscription ([sandbox](../../tools/sandbox/README.md)), one region (the environment definitions' `region`) | Landing zone, platform identity model (D19), more environments, multi-region (E53) |
| Substrate from data per environment: resource group, Container Apps Environment, a Cosmos SQL database (400 RU/s shared throughput); one Log Analytics workspace and one free-tier Cosmos account shared by both environments | Substrate versioning (B7) |
| One component `database` (the catalog implements it as Cosmos SQL): a container per workload in its environment's database, grant scoped to that container (free tier: 2 environments × 400 RU/s ≤ 1,000) | Other components, `class` values beyond `standard`, overrides |
| One policy `enforce-monitoring`: every workload requires `monitoring` (per-workload App Insights) | More policies — adding one is a test of goal 1, not scope |
| Container App, system-assigned identity, multiple-revision mode; image on a public registry | ACR and the ACR-pull identity (0012 follow-up) |
| Release set: label + pinned workload definitions + image digests + workload-level `dependsOn` | Work-item scope, build-evidence contract (F56) |
| Steps per workload, in dependency order: infra → dark revision → grants → OQ probe on the dark revision (retry to timeout) → traffic shift | Hook jobs and migrations (0014, 0015) — Cosmos needs no DDL |
| Qualification record per deploy: IQ (refresh shows no drift; running digests equal pinned) and OQ (probe declared in the catalog), with machine identity | Smoke/PQ suites, release documents and VIRP (F55) |
| Approval policy as data per approval-gated environment; `jd approve` records who, when and meaning; `jd promote` deploys the same set to `stage` only when approved and qualified | Entra group resolution, e-signature integration (F54), notifications |
| The `jd` CLI ([usage.md](../usage.md)) | API, web UI, queue, durable steps (E29), rollback, destroy command |
| Local release record (label, definition SHAs, catalog version, digests, graph, qualification, approvals, outcome, phase timings) | Audit store (F39) |
| Local Pulumi state and records in jd's data directory | Blob state |

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
