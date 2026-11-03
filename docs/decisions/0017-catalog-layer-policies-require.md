# 0017. Team definitions over a platform catalog; policies require, never add

- **Status:** Accepted (user, 2026-10-18; recorded 2026-11-03)
- **Origin:** S21 design ([components.md](../architecture/components.md) section 8); supersedes [0004](0004-two-layer-definition-model.md)

## Context
[0004](0004-two-layer-definition-model.md) split definitions into a team layer and an IT Ops global layer whose policies are merged into
every workload without the team declaring them (its example: all traffic through an application gateway). The MVP built that merge as a
policy `add`: App Insights reached every workload though no workload asked for it. A workload then got resources it never declared, which
breaks design principle 6 ([context.md](../context.md#design-principles): explicit over implicit).

## Decision
Two layers remain, but the platform layer is the **catalog**, and it never puts anything into a workload that the workload did not declare.

- **Team layer (per workload).** The workload definition declares what it needs (`requires`: a component type, optional `id` and
  `class`, [0011](0011-requirement-id.md)) and is the same in every environment. The platform provisions exactly that.
- **Platform layer (the catalog).** Components with one contract and an implementation per environment type (or `default`); shared
  resources; the runtime ([0018](0018-environment-offers-runtime.md)); grant targets; policies. An environment offers components by name.
- **Policies require, never add.** A workload-scope policy may `require` one component type; a workload without that requirement is an
  error naming the policy and its reason. A policy cannot add nodes (a policy file with `add` is a load error), nor require a class or a
  count. Policies may still `set` or `default` values on nodes they match.
- **Implicit resources are few and platform-owned:** the groups, the workspace and the Container Apps environment that the offered
  components and runtime use, and the workload's own runtime ([components.md](../architecture/components.md) section 2).

## Consequences
- A guardrail is still written once, but each team sees it as a failed check and adds the requirement itself: what a workload gets, it
  wrote. Observability is explicit: `monitoring` is a requirement, `enforce-monitoring` only requires it.
- A rule like 0004's "all traffic through an application gateway" is not a policy any more; it becomes a component, a shared resource
  or a landing-zone control ([0008](0008-enforcement-in-landing-zone.md)).
- How provisioning runs (a background worker, [0002](0002-pulumi-automation-api.md)) is not part of this decision.

## Related
- [0004](0004-two-layer-definition-model.md) (superseded), [0011](0011-requirement-id.md), [0018](0018-environment-offers-runtime.md)
- [architecture/components.md](../architecture/components.md), [architecture/resolver.md](../architecture/resolver.md)
