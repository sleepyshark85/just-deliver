# 0014. Hook points: preDeploy, verify, postDeploy as Container Apps Jobs

- **Status:** Proposed — pending review
- **Origin:** open-questions E28

## Context
"Expose hooks so teams can inject their logic" is where a platform either builds a CI/CD system or delegates to one. A hook needs network placement, identity injection, resolved provisioning outputs, timeouts, log capture, exit-code semantics and re-run safety — a job scheduler, not a callback.

## Decision
The platform owns the graph — what steps run, in what order, with what gates and inputs — and **never executes team code in its own process**. Team code runs as Container Apps Jobs inside the environment ([0013](0013-container-apps-runtime.md)), inheriting VNet and workspace, with timeout and retry supplied by the runtime. (An alternative considered: a step in the team's existing pipeline that the platform triggers and waits on.)

Three hook points, each its own persistent job resource, provisioned during the infra step and executed at its point in the pipeline:

| | `preDeploy` | `verify` | `postDeploy` |
|---|---|---|---|
| Runs | after grants, revision dark | after preDeploy, still dark | after traffic shift |
| Default image | **app image** | test image | test image |
| Grants | DDL on the database | none by default | none by default |
| Injected target | none — app is not serving | dark revision FQDN | live app FQDN |
| Auto-retry | **no** | **yes, until timeout** | no |
| On failure | traffic never shifts | traffic never shifts | traffic stays, deploy marked degraded |

- **`verify` inverts deploy-then-test into test-then-expose.** Each revision has its own FQDN, so the new one is reachable while dark. With [0015](0015-database-migrations.md)'s no-op migration failure, nothing user-visible happens until infra, grants, migration and smoke tests pass. A retrying `verify` also discriminates "grants have not propagated" from "the application is broken", closing D21's ambiguity ([0012](0012-system-assigned-identity.md)'s accepted cost).
- **Retry policy is per-hook.** `preDeploy` must not auto-retry (partial migrations need human acknowledgement); `verify` must (absorbs propagation); `postDeploy` retrying is pointless.
- **Grants are derived from declared need, not inherited from the app.** A hook wanting direct data access must declare it; the platform records the request rather than granting silently. Keeps identity count from driving grant count against D20's cap.
- **`verify` runs against real dependencies** — in production the dark revision holds a live production database connection. In protected environments `verify` is smoke/contract checks: read-mostly, no destructive fixtures, no leftover test data. Fuller suites in team environments. The platform cannot enforce this (same honesty as `destructive`).
- **A failed `verify` leaves the revision dark and inspectable** at its FQDN — surface in the UI, not as a bare failure.
- **Uniform across environments by default**, restrictable by environment *tier* (not name), consistent with `destructive`. Parity (A4) says dev should exercise the same hooks.
- **Rule:** a hook point's only value is the platform guaranteeing *when* it runs relative to platform steps; two hooks at the same moment should be one.
- **Hook results are captured into the release record** — Azure retains only a bounded number of job executions.
- No DSL, no conditionals, no fan-out. Hook points can be added later; a DSL cannot be removed.

**Deliberately not added:** pre-flight before infra (approval gates do that); `onFailure` (notification is a platform feature; a failing failure-hook sits on the most fragile path); hooks between infra resources (ties hooks to the graph, becomes a DSL). **`preDestroy` deferred** — valuable for exporting data before a drop, but the deletion flow (C16, B6) does not exist yet.

## Consequences
- Schema gains a `hooks` block (H50): each hook has `command`, optional `image` (defaults to workload image for `preDeploy`), `timeout` (default 10m), optional tier restriction, `destructive` on `preDeploy`.
- `postDeploy` gets teeth only via promotion gating (E34); flaky `verify` suites remain a convention problem (E36).

## Related
- [0006](0006-infra-deployed-with-app.md), [0012](0012-system-assigned-identity.md), [0013](0013-container-apps-runtime.md), [0015](0015-database-migrations.md)
- [open-questions](../open-questions.md): A4, B6, C16, D20, D21, E34, E36, H50
