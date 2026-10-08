# 0003. Static Pulumi YAML template library with orchestrator-generated config

- **Status:** Proposed — pending review
- **Origin:** investigation notes (Pulumi notes)

## Context
Resources can be defined in two ways under the Automation API:

| | Pulumi YAML templates | Inline program in the platform's language |
|---|---|---|
| Provider SDK coupling | None: the CLI resolves provider plugins at runtime | The platform must depend on every provider's typed SDK (Azure Native, AWS, ...) |
| Conditionals/loops | Very limited (`fn::each` only) | Full language |
| Best fit | Many resource types and providers; one template per resource type | One fixed resource shape |

Stacks also have to share outputs (for example resource group → app). This can be done by the orchestrator copying one stack's outputs into the next stack's config, or by Pulumi stack references, which read another stack's outputs live and are read-only.

## Decision
1. Keep resource definitions as **static YAML templates**, one per resource type per cloud provider, version-controlled and reused by every workload. Never generate definitions; generate only **config values** for each run.
2. The **orchestrator** goes through the resources a workload needs. For each one it stages the template in a fresh working directory, sets config from workload data and earlier outputs, runs `up`, captures the outputs, and cleans up.
3. **One stack per workload per template**, not one per environment.
4. **Config-passing through the orchestrator, not stack references**: a single orchestrator already sequences the stacks and holds the values.
5. The state backend is supplied by the orchestrator at run time, never written into templates, so templates do not depend on a particular backend.
6. The orchestrator depends only on the Automation API, never on a cloud provider SDK.

## Consequences
- Templates can be audited and versioned; adding a provider or resource type does not touch orchestrator dependencies.
- Conditional logic ("if the workload needs a database, add X") moves out of templates into the orchestrator/resolver, because YAML cannot express it.
- Stack config is generated on every run and never edited by hand.
- Many small stacks give failure isolation and independent lifecycles, at the cost of more orchestration and per-stack CLI/plugin startup overhead. Scale by running independent workloads in parallel, not by merging stacks with independent lifecycles. Azure ARM rate limits are a separate concern.
- Copied config can go stale. Revisit stack references if stacks are ever provisioned independently, without a shared orchestrator.

## Related
- [0002 Pulumi Automation API](0002-pulumi-automation-api.md)
- [architecture/provisioning.md](../architecture/provisioning.md), [architecture/resolver.md](../architecture/resolver.md), [architecture/workload-definition.md](../architecture/workload-definition.md)
