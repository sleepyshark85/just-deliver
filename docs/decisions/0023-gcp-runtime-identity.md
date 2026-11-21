# 0023. On GCP the runtime's identity is a service account of its own part, checked as a grant target is

- **Status:** Proposed — pending review
- **Origin:** S28 ([architecture/multi-cloud.md](../architecture/multi-cloud.md) §4)

## Context
[ADR 0012](0012-system-assigned-identity.md) gives each workload a system-assigned identity, so that wiring the wrong identity onto a
workload is structurally impossible: Azure enforces it. Cloud Run has no system-assigned identity; a service runs as a service account
named in its configuration, and without one it runs as the project's default compute account, shared by every service and broadly
privileged. On GCP the identity is an input, so the invariant must be enforced by jd, as the grant target rule enforces where a grant
points (boundaries 1, 2, D19).

## Decision
- **The runtime part creates the identity.** The GCP runtime implementation has an `identity` node (a service account) beside the
  `runtime` node; both are deployed with the workload and owned by it, so ADR 0012's intent holds: one identity per workload, its
  lifecycle the workload's.
- **Data names the identity input.** The runtime implementation declares `identityInput`, the `runtime` node's input that carries the
  identity. Its value must be exactly `${<node>.<output>}` of another node of the runtime's own part: no literal, no text around it, no
  function, no shared or environment resource, `env.*`, `role.*` or another part. The loader checks the catalog, and the graph builder
  checks again after policies, so no policy can point a workload at another identity.
- **Never the default compute account:** the template declares the input with no default, and a `runtime` node without its declared
  identity input is refused at load.
- The runtime outputs the account's member as `principalId`; grants read `${runtime.principalId}` as on Azure.

## Consequences
- The boundary that Azure enforces structurally is, on GCP, a load-time and post-policy check with negative tests, like boundary 1.
- A service account exists before the service, so grants could precede the revision; they stay `AfterRuntime` anyway, one release flow
  for both platforms.
- Azure runtimes declare no `identityInput` and are unchanged.
- Rejected: **the default compute account:** one identity for every workload in the project, with broad roles (principle 12, D19).
- Rejected: **one platform-owned service account per environment:** every workload of the environment could reach every other's grants.
- Rejected: **a workload-created account with no check:** a policy `set` or a catalog edit could name another workload's account, the
  bug ADR 0012 calls one step from a cross-team breach.

## Related
- [0012](0012-system-assigned-identity.md), [0021](0021-platform-dimension.md), [0006](0006-infra-deployed-with-app.md)
- [architecture/multi-cloud.md](../architecture/multi-cloud.md), [architecture/model.md §7](../architecture/model.md#7-policies-and-grants)
