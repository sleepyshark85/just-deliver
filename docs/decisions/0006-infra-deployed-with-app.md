# 0006. Workload infrastructure is deployed with the app

- **Status:** Proposed — pending review
- **Origin:** open-questions A1

## Context
A workload could either be continuously reconciled by a control loop (GitOps auto-sync) or applied by a pipeline when it is deployed. Continuous reconciliation means the platform mutates production with no release record attached; self-healing cannot coexist with "nothing changes prod outside a gated deployment."

## Decision
Workload infrastructure is deployed with the app. No continuous reconciliation. This gives a **two-speed model**:

- **Substrate (environment tier)** — versioned, pinned per environment, moved only by a deliberate gated upgrade (B7). Never touched by a workload deploy.
- **Workload tier** — its database on the shared server, its App Insights component, its queue, its identity and grants — provisioned as part of the deployment, on every deploy.

The model stays **declarative**: every deploy converges desired state onto actual and reverts manual drift. What is dropped is the control loop, not desired state (Terraform-in-CI rather than auto-sync). The audit trail wins.

**Rider 1 — drift is detected continuously, corrected only on deploy.** A scheduled preview reports divergence without applying it (keeps G42 honest). Without it, drift accumulates silently and is reverted by whoever deploys next, landing the surprise on the wrong person.

**Rider 2 — rollback rolls back the app only; infra is forward-only.** Reverting a revision is cheap; reverting infra that added a queue or altered a database setting may be unsafe or impossible. Stated explicitly because everyone will otherwise assume rollback covers both.

**Step order** (as revised by [0012](0012-system-assigned-identity.md)):

1. infra
2. create the app revision with no traffic, and the migration job (their identities come into existence here)
3. grants
4. verify propagation
5. pre-deploy hook (migration)
6. shift traffic
7. post-deploy hook (tests)

[0014](0014-hook-points.md) later inserts the `verify` hook between the migration and the traffic shift.

## Consequences
- A deployment is identified by image tag **and** definition SHA; a manifest change with no code change is still a deployment (reinforces [0007](0007-definition-upload-snapshot.md)).
- No self-healing: a manual change in prod persists until someone deploys.
- Policy and mapping changes propagate lazily — a workload not shipped for three months runs three months of stale resolution. Requires platform-initiated redeploy (E51).
- Partial failure (E31) becomes more urgent: "provisioning failed, nothing deployed" no longer exists as a clean state; one failed deployment spans half-changed infra and an undeployed app.
- Deploy latency becomes an adoption risk (H47): every deploy pays infra reconciliation.

## Related
- [0012](0012-system-assigned-identity.md), [0014](0014-hook-points.md), [0015](0015-database-migrations.md), [0013](0013-container-apps-runtime.md)
- [open-questions](../open-questions.md): B7, E31, E32, E51, G42, H47
- [architecture/provisioning.md](../architecture/provisioning.md)
