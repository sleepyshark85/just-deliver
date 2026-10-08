# 0010. Replacement protection classes and plan-content gating

- **Status:** Proposed — pending review
- **Origin:** open-questions C11

## Context
Pulumi replaces a resource automatically when a change touches a property the provider marks as requiring replacement — delete and recreate, as part of a normal apply, reported as success. Because the platform generates deterministic resource names, replacement is **delete-first** rather than create-first.

## Decision
Resource types are classified into three protection classes in the template library. The same list is what the preview gate checks replaced URNs against — maintained once, used twice.

| Class | Types | Protection |
|---|---|---|
| **Data-bearing** — loss is irreversible | SQL server + databases, storage accounts, Key Vault, Log Analytics workspace, Service Bus namespace, Cosmos, Container Registry | `protect: true` |
| **Reference-bearing** — recreatable, but recreation invalidates references held elsewhere | user-assigned managed identity (new principal id dangles every grant), public IP (address changes), App Insights component (connection string changes), VNet/subnet (cascades to everything attached) | `protect: true` |
| **Disposable** | Container App, App Service plan, NSG rules, role assignments, private endpoints | none |

The reference-bearing class is the one usually missed: not data loss but silent cascading breakage. Any user-assigned identities (e.g. the environment-scoped ACR-pull identity in [0012](0012-system-assigned-identity.md)) belong in it.

- **Protection is uniform, including dev.** Teardown is an explicit platform operation: unprotect → apply → destroy. Teams keep one-button disposal; the unprotect step is the audit record that *someone chose to destroy this*; dev and prod run the same templates (no reopening of A4).
- **Paired with an Azure-side lock.** `protect` constrains Pulumi only, not portal or CLI, so per F41 the fence belongs in Azure: a `CanNotDelete` lock over the same resource set, removed in the same teardown sequence. Lock scope needs care — locks have known side effects on some data-plane operations.
- **Gate on plan content, not version distance.** Most substrate upgrades are config-only and apply unattended. Stop only when the plan replaces something holding data, crosses a version marked path-dependent, or changes network reachability. Mechanically: preview first, inspect the plan for replacements, cross-reference replaced URNs against the protected classes, escalate rather than execute when they intersect. `protect` is the backstop for anything the gate misses.

## Consequences
- B7 substrate upgrades are checked against these classes.
- Renaming a resource `id` ([0011](0011-requirement-id.md)) or removing a resource (C16) is caught by `protect`.

**Follow-ups — prerequisite spikes before implementing:**
1. **When does `protect` fail** — at plan generation, or mid-apply after other resources have changed? Decides whether the preview gate is load-bearing or belt-and-braces, and whether a blocked upgrade leaves a half-applied stack.
2. **Can Pulumi YAML templates drive `options.protect` from a config value?** If not, the unprotect step needs another mechanism (`pulumi state unprotect` from the orchestrator, or template variants), which changes the teardown flow.

## Related
- [0003](0003-pulumi-yaml-template-library.md), [0011](0011-requirement-id.md), [0012](0012-system-assigned-identity.md), [architecture/provisioning.md](../architecture/provisioning.md)
- [open-questions](../open-questions.md): A4, B7, C16, F41
