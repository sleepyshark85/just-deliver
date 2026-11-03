# 0018. The environment offers the runtime; the workload names none

- **Status:** Accepted (user, 2026-10-18; recorded 2026-11-03)
- **Origin:** S21 design ([components.md](../architecture/components.md) sections 2, 8); supersedes the first decision of [0013](0013-container-apps-runtime.md)

## Context
[0013](0013-container-apps-runtime.md) made the runtime a declared property of the workload, an enum defaulting to Container Apps. With
environment types and offers (S21), what an environment provides is the catalog's knowledge, and a workload definition stays the same in
every environment. A runtime field on the workload would duplicate what the environment already decides.

## Decision
- The runtime is a catalog kind of its own (`kind: Runtime`), not a component: no contract, no classes, never required.
- An **environment offers exactly one runtime** (by name, in `offers`); two is an error. Every workload deployed there gets it.
- The workload names no runtime. Choosing another runtime, such as App Service later, is a platform change to what an environment offers.
- The runtime's implementations are per environment type (or `default`), like a component's; it carries the `probe`, the `release`
  block and the reserved `runtime` node.

The rest of 0013 (Container Apps, multiple-revision mode, Jobs for hooks, the runtime port) is not decided by this record and stays
under review there.

## Consequences
- A workload moves between environments of different types without editing its definition.
- A team cannot choose its runtime per workload; supporting two runtimes in one environment would need a new decision.

## Related
- [0013](0013-container-apps-runtime.md), [0017](0017-catalog-layer-policies-require.md)
- [architecture/components.md](../architecture/components.md)
