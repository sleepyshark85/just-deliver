# 0020. One Pulumi stack per graph node, the environment in its name

- **Status:** Proposed — pending review
- **Origin:** built since the MVP (S07 stack names, S12 substrate); supersedes decision 3 of [0003](0003-pulumi-yaml-template-library.md)
  if accepted

## Context
[0003](0003-pulumi-yaml-template-library.md) decision 3 says "one stack per workload per template, not one per environment". The
engine does not work that way: a workload uses one template for several nodes (two databases, two grants), the same workload deploys
to several environments, and the shared and environment layers deploy nodes that belong to no workload. A stack keyed by workload and
template would hold two nodes' resources, or one workload's resources in every environment, in one state.

## Decision
- **Every node of the resolved graph is its own stack.** A node's id is `<owner>/<environment>/<scope>/<node>` (the workload or the
  environment's owner, the environment, the requirement or layer scope, the node's name); its stack name is that id made valid for
  Pulumi (`GraphBuilder`, `/` → `.`, `@` → `_`; `StackNames.ToPulumi` shortens a name over 100 characters to a prefix plus a 16-hex
  hash of the full name). Ids are unique in a graph, so stacks are unique by construction.
- **The environment is part of the stack name**, so the same workload in `dev` and `stage` has separate stacks and separate state.
- The template is the node's input, not its stack's identity: two nodes on one template are two stacks.

## Consequences
- A node is the unit of deploy, preview, drift check and failure: the orchestrator runs one stack per node in graph order and passes
  outputs on as config ([0003](0003-pulumi-yaml-template-library.md) decision 4).
- Renaming a requirement `id`, a node or an environment renames its stack, so Pulumi sees a new stack and a forgotten old one: an id
  is an identity ([0011](0011-requirement-id.md)), and C16 and [0010](0010-replacement-protection-classes.md) apply.
- More stacks means more per-stack CLI and plugin start-up (H47); that cost is accepted in 0003 already.

## Related
- [0003](0003-pulumi-yaml-template-library.md), [0011](0011-requirement-id.md), [architecture/provisioning.md](../architecture/provisioning.md)
- [open-questions](../open-questions.md): C16, H47
