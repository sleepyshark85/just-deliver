# 0009. RBAC split inside a team-owned subscription

- **Status:** Proposed — pending review
- **Origin:** open-questions B10

## Context
Under [0008](0008-enforcement-in-landing-zone.md), teams own their environments while the platform still places some resources it owns inside the team's subscription. The RBAC boundary between the two needs defining.

## Decision
Guardrails at the subscription, ownership at the resource group.

- Platform-owned resources sit in a dedicated **platform resource group** inside the team's subscription, where the team is **Reader**.
- The team holds **Contributor** over its own resource groups.
- The team's role-assignment rights (Role Based Access Control Administrator) are constrained by an ABAC condition to a **data-plane role allowlist**.
- Azure Policy assignments at subscription scope provide the guardrails.

## Consequences
- Teams cannot alter policy or platform-owned resources, but can grant their own workloads data-plane access within the allowlist.
- A team can still revoke the platform's access to its subscription; this is surfaced, not prevented (see [0008](0008-enforcement-in-landing-zone.md)).
- Role assignments count against the per-subscription cap (D20).

## Related
- [0008](0008-enforcement-in-landing-zone.md), [0016](0016-portal-deep-links.md)
- [architecture/landing-zone.md](../architecture/landing-zone.md)
- [open-questions](../open-questions.md): D19, D20, F41
