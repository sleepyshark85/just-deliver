# 0016. Portal deep links rely on team read access; data-plane links are flagged

- **Status:** Proposed — pending review
- **Origin:** open-questions G43

## Context
The platform UI links into the Azure portal for a workload's resources. Whether those links resolve depends on the permissions teams hold in each tier.

## Decision
Answered by [0008](0008-enforcement-in-landing-zone.md): teams hold write in their own environments and read on their workload's resources in stage/prod, so **control-plane deep links resolve in both tiers**.

The limit: read is control-plane only. A link into a data-plane view (Key Vault secret values, a query editor) still fails in stage/prod without break-glass. The UI should say so rather than present a link that 403s.

## Consequences
- The UI must know, per link, whether it targets control or data plane and the viewer's tier.
- Data-plane debugging in stage/prod routes through break-glass (H48).

## Related
- [0008](0008-enforcement-in-landing-zone.md), [0009](0009-rbac-split-team-subscription.md)
- [open-questions](../open-questions.md): G42, H48
