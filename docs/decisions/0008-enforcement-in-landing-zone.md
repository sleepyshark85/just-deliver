# 0008. Enforcement lives in the landing zone; the platform is the paved road

- **Status:** Proposed — pending review
- **Origin:** open-questions A3

## Context
Need an enforcement posture per environment tier: who may write what, and where guardrails are enforced. Adoption of the platform is optional by design, and anything enforced only in platform code is defeated by portal or CLI access.

## Decision
Teams provision their own environments and own everything inside them. The landing zone is provisioned by the platform team and is read-only to teams. Enforcement lives in the landing zone (Azure Policy, RBAC, quota, network), not in the platform. **The platform is the paved road, not the fence.**

**Dividing line:** a team writes anything whose blast radius stops at their environment; the platform owns anything whose blast radius crosses it. Address space, peering, routing and DNS cross it (overlapping CIDRs break peering for everyone). Subnets and NSGs inside a team's own allocation do not.

**Mechanism:**
- **Contributor rather than Owner** — its NotActions remove policy and role-assignment writes in one move.
- **Role Based Access Control Administrator** scoped to team resource groups, with an ABAC condition allowlisting assignable data-plane roles.
- **Azure Policy Deny** for dangerous shapes.
- **Platform-owned resources** in a platform resource group where the team is Reader (see [0009](0009-rbac-split-team-subscription.md)).

The full read/write matrix is in [architecture/landing-zone.md](../architecture/landing-zone.md).

## Consequences
Accepted:
- Resource locks are an accident guard in team environments (the team can remove their own) and a genuine control in stage/prod (they cannot). Same mechanism, different force.
- A team can revoke the platform's access to its own subscription. Under opt-in adoption that is their right; the platform detects and surfaces it as a visible state rather than preventing it.
- No standing data-plane access in stage/prod, so debugging a production database requires break-glass (H48) — which therefore must be fast, audited, time-bound PIM elevation.
- Platform-side validation is UX, not control. The exact Policy-vs-platform rule split remains open (F41).
- Adoption must be earned: the paved road has to be faster than a ticket (H47).

## Related
- [0009](0009-rbac-split-team-subscription.md), [0016](0016-portal-deep-links.md), [0015](0015-database-migrations.md) (destructive gate only in protected environments, consistent with this)
- [architecture/landing-zone.md](../architecture/landing-zone.md), [architecture/network.md](../architecture/network.md)
- [open-questions](../open-questions.md): A4, F41, H47, H48
