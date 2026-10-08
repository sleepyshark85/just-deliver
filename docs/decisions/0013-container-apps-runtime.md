# 0013. Azure Container Apps as the (single) workload runtime

- **Status:** Proposed — pending review
- **Origin:** open-questions E27

## Context
Something has to run the container. Each runtime brings its own deployment mechanism, revision model, identity attachment, network integration, health semantics and log shape, so supporting N runtimes multiplies the deploy step, rollback, hook execution and graph rendering rather than adding to them.

## Decision
- Runtime is a **declared property of the workload**, constrained to a platform-supported **enum**, defaulting to **Container Apps**. Adding a value is a platform-team change. An extension point with one implementation, not a menu.
- **Multiple-revision mode** (not the Container Apps default).
- **Container Apps Jobs** as the hook executor.
- The **Container Apps Environment is an environment-tier resource**: holds VNet integration and the Log Analytics binding, provisioned once per environment; workloads deploy into it.

**The runtime port** — what a second implementation must satisfy: attach identity; deploy image + config + secret references; make a revision live; roll back to a prior revision; report readiness; run a hook in the same network with the same identity; expose where logs and metrics land.

**Not supported, stated rather than discovered:** stateful workloads, DaemonSet-style patterns, custom operators, service mesh. These are the teams who will ask for AKS.

## Consequences
- Hooks ([0014](0014-hook-points.md)) largely fall out: a Job is exactly "run this image, as this identity, in this environment, with a timeout", with retry and timeout built in, running inside the environment so it reaches private-endpointed databases.
- Rollback (E32) is a traffic-weight switch against a warm previous revision, not a redeploy. Single-revision mode would make rollback a redeploy of the old image. Multiple-revision mode also enables deployment strategies (E35) later.
- Each revision has its own FQDN, enabling the dark `verify` step.
- Supports [0012](0012-system-assigned-identity.md)'s dark-revision grant sequence; ACR pull for a private registry is handled per 0012's follow-up.
- B5: a Container Apps Environment needs a dedicated subnet whose minimum size depends on environment type — an IPAM decision before address space is allocated.

**Follow-up (open, D22):** whether a Key Vault secret reference is refreshed without a new revision, or rotation implies a revision restart. Decides whether "rotate a secret" is a platform operation or a deployment.

## Related
- [0006](0006-infra-deployed-with-app.md), [0012](0012-system-assigned-identity.md), [0014](0014-hook-points.md), [architecture/network.md](../architecture/network.md)
- [open-questions](../open-questions.md): B5, C12, D22, E32, E35
