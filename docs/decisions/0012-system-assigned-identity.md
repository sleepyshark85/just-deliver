# 0012. System-assigned managed identity for workloads and migration jobs

- **Status:** Proposed — pending review
- **Origin:** open-questions D17

## Context
Each workload needs an identity for data-plane access. User-assigned identities can be created before the app and granted ahead of time; system-assigned identities are created by the resource itself. Identity is the platform's differentiator, so scrutiny concentrates here.

## Decision
**System-assigned**, for both the Container App and the migration job.

**Why it wins.** Lifecycle is automatic: no orphaned identities, no naming budget consumed (C14). More importantly it is **structurally impossible to wire the wrong identity onto a workload** — with user-assigned that is one platform bug away from a cross-team data breach, and D19 already makes the resolver a security control. Azure enforces the invariant instead of us.

**Objections that did not survive:**
- Container Apps Jobs are persistent resources (executions are ephemeral, the job is not), so a job's system-assigned identity is stable and the least-privilege split works: DDL on the migration identity, DML on the runtime identity.
- Dangling grants after a replacement largely self-heal: under [0006](0006-infra-deployed-with-app.md) infra is provisioned on every deploy, so the grant step re-reads the current principal id and re-applies within the same deployment.

**The real cost, accepted:** grants necessarily come after the identity exists, so the container starts before it can reach anything. Multiple-revision mode ([0013](0013-container-apps-runtime.md)) absorbs most of this: the new revision is created with no traffic, its identity comes into existence, grants are applied and verified, the migration runs, and only then does traffic shift. Crashlooping happens on a revision serving nobody, and success is judged after grants land. Exception: a workload's *first* deployment has no previous revision — acceptable, since a new workload has no users.

This revises the step order in [0006](0006-infra-deployed-with-app.md): infra → app revision (no traffic) + migration job → grants → verify propagation → migration → shift traffic → post-deploy.

**Platform contract:** applications must tolerate and retry authentication failures on startup. `DefaultAzureCredential` does not do this; it needs application code. A documented obligation on teams, belonging in onboarding material.

## Consequences
- Propagation delay (D21) sits on the critical path of every deployment; resolved in practice by the retrying `verify` hook ([0014](0014-hook-points.md)).
- Principal ids do not exist until the app is created, so resolution is multi-phase (C52).
- Orphaned role assignments still accumulate and need a periodic sweep (D26).

**Follow-up — ACR pull bootstrap (open).** A first revision must pull its image before its identity can be granted pull rights. Suggested resolution: image pull uses a single **environment-scoped, platform-owned user-assigned identity** holding `AcrPull` on the shared registry; all workload data-plane access stays system-assigned. One identity per environment, so no lifecycle burden — at the cost of every workload in an environment being able to pull any image in that registry, normally acceptable within one organisation. That identity is reference-bearing ([0010](0010-replacement-protection-classes.md)).

## Related
- [0006](0006-infra-deployed-with-app.md), [0013](0013-container-apps-runtime.md), [0014](0014-hook-points.md), [0015](0015-database-migrations.md)
- [open-questions](../open-questions.md): C14, C52, D18, D19, D21, D26
