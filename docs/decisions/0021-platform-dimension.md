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
- **An environment definition names its `platform`** (required: a default would choose a cloud silently); the descriptor carries it. One
  platform per environment.
- **Components and runtimes are keyed by platform, then by environment type.** The platform level is an exact lookup with no fallback,
  so a component without an entry for the environment's platform is an error naming both, and one cloud's data never reaches another's
  environment; the type level keeps `default`. `uses` belongs to the platform entry, since what a component uses depends on how it is
  built.
- **A shared resource names its `platform`**; a part reads only shared resources of its own platform.
- **Policies are unchanged:** node scope already matches `template`, and templates are namespaced per platform.
- Node ids, stack names, node hashes and the `env.*` roots do not change.

## Consequences
- The seed catalog and sample definitions are migrated to the new shape once; the deployed-config snapshot stays byte-identical, which
  proves the refactor. Old-shape catalogs and descriptors fail at load with what to add; descriptors gain `platform`.
- A component without an implementation for a platform cannot be offered there.
- Rejected: **environment types per platform** (`dev-gcp`): no schema change, but the `default` fallback hands a GCP type the Azure
  implementation silently, policies matching `dev` miss `dev-gcp`, and types multiply by platforms.
- Rejected: **a catalog per platform:** no engine change, but each component's contract is written twice and drifts (principles 8, 9),
  and nothing stops a GCP environment being deployed with the Azure catalog.

## Related
- [0017](0017-catalog-layer-policies-require.md), [0018](0018-environment-offers-runtime.md), [0019](0019-engine-knows-concepts.md),
  [0022](0022-neutral-deployer-identity.md), [0023](0023-gcp-runtime-identity.md)
- [architecture/multi-cloud.md](../architecture/multi-cloud.md), [architecture/model.md](../architecture/model.md)
