# 0015. Database migrations run as the preDeploy job from the app image

- **Status:** Proposed — pending review
- **Origin:** open-questions E33

## Context
Schema migrations must fit the deployment order of [0006](0006-infra-deployed-with-app.md)/[0012](0012-system-assigned-identity.md) (migration before traffic shift), the separate DDL identity, and app-only rollback.

## Decision
**The migration is the app's own image with a different command**, run as the `preDeploy` hook ([0014](0014-hook-points.md)):

```yaml
hooks:
  preDeploy:
    command: ["dotnet", "MyApp.dll", "--migrate"]
    timeout: 10m
    destructive: false
```

Same image guarantees migration and dependent code are the same build — no separate publish pipeline, no version skew. A distinct `image:` field remains as an escape hatch.

- **Never migrate on application startup** — a prohibition: it races across replicas, delays readiness, and would require the *app* identity to hold DDL rights. The least-privilege split holds only if migrations run exclusively in the job.
- **Backward-compatible with the currently deployed code.** The old revision serves traffic during migration. Additive only: add columns/tables, never drop or rename in the same release; drops come later once nothing references them. This is rollback (E32) seen twice: migrations are infrastructure and are not rolled back, so expand/contract is what makes the rollback model work.
- **Destructive changes are declared, not detected.** `destructive: true` routes the deployment into an approval gate **in protected environments only** (dev is frictionless, per [0008](0008-enforcement-in-landing-zone.md)). No static analysis of migration SQL: brittle, and false confidence is worse than none.
- **Timeout defaults to 10 minutes** (continuous development produces small changes); overridable.
- **A failed migration is a genuine no-op** — traffic never shifts, the old revision keeps serving, the new one sits dark. Surface prominently in the UI.
- **Partial failure needs human acknowledgement before retry.** DDL transactionality varies (Postgres transactional DDL; MySQL commits implicitly). Migration tools are idempotent via their bookkeeping table, which a non-transactional partial failure can corrupt — so no auto-retry.
- **Long data backfills are not deployment work.** The timeout is a boundary; backfills are a separate team-triggered operation. Prevents the hook becoming a general batch runner.
- **Concurrency** handled by E30's per-(workload, environment) serialization.

## Consequences
- **Accepted risk:** a team that forgets `destructive: true` bypasses the gate undetected. [0010](0010-replacement-protection-classes.md)'s `protect` and C16's destructive-diff gate cover infrastructure deletion, but a `DROP TABLE` is invisible to Pulumi. The declaration is the only control.

**Follow-ups (open):**
- Whether a platform ceiling on `timeout` applies in protected environments.
- Whether the platform verifies point-in-time restore retention covers the deployment window before a declared-destructive migration — cheap, since PITR is on by default for Azure SQL and Postgres flexible server.

## Related
- [0006](0006-infra-deployed-with-app.md), [0012](0012-system-assigned-identity.md), [0014](0014-hook-points.md)
- [open-questions](../open-questions.md): C16, E30, E32, H50
