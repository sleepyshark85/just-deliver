# 0021. The platform is an environment value; implementations are selected by platform

- **Status:** Proposed — pending review
- **Origin:** S28 ([architecture/multi-cloud.md](../architecture/multi-cloud.md) §1); the user's decision of 2026-11-21 that jd supports
  several clouds, GCP first

## Context
Implementations are keyed by environment type or `default`, and shared resources have no implementations, so the catalog can describe
one cloud only ([model.md §3](../architecture/model.md#3-parts-components-and-runtimes)). A second cloud needs a second implementation of
the same contract, chosen without matching and without letting one cloud's data reach the other's environment (principles 6, 7, 9).

## Decision
- **The catalog declares its platforms** in `catalog.yaml`, as it declares environment types.
- **An environment definition names its `platform`** (required, no default); the descriptor carries it. One platform per environment.
- **Components and runtimes are keyed by platform, then by environment type.** The platform level is an exact lookup with no fallback;
  the type level keeps `default`. `uses` belongs to the platform entry.
- **A shared resource names its `platform`**; a part reads only shared resources of its own platform.
- **Policies may match `platform`.**
- Node ids, stack names, node hashes and the `env.*` roots do not change.

## Consequences
- The seed catalog and sample definitions are migrated to the new shape once; the deployed-config snapshot stays byte-identical, which
  proves the refactor. Old-shape catalogs and descriptors fail at load with what to add.
- A component without an implementation for a platform cannot be offered there: the error names component and platform.
- Rejected: environment types per platform (`dev-gcp`), whose `default` fallback would serve one cloud's implementation to another; and a
  catalog per platform, which writes each contract twice.

## Related
- [0017](0017-catalog-layer-policies-require.md), [0018](0018-environment-offers-runtime.md), [0019](0019-engine-knows-concepts.md),
  [0022](0022-neutral-deployer-identity.md)
- [architecture/multi-cloud.md](../architecture/multi-cloud.md), [architecture/model.md](../architecture/model.md)
