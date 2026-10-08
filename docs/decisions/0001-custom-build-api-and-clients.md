# 0001. Custom build: backend API with multiple clients

- **Status:** Proposed — pending review
- **Origin:** investigation notes (decisions and approach; IDP vs traditional DevOps)

## Context
The platform needs three things together:
1. Dynamic, definition-driven provisioning (team definitions merged with global IT Ops policy, see [0004](0004-two-layer-definition-model.md)).
2. A structured release lifecycle with enforced approval gates (HoSD, QA).
3. An IT Ops work-order and secrets-handoff workflow (needed for the B/C stages, see [0005](0005-build-bc-first-design-for-d.md)).

Four products were evaluated:

| Product | Finding |
|---|---|
| Port.io | Does not cover all three needs |
| Backstage | Does not cover all three needs |
| Humanitec | Closest in concept, but would still need significant customisation |
| Azure Deployment Environments | Does not cover all three needs |

The sources give no per-product breakdown beyond this. The SCORE workload specification was also evaluated as a possible container runtime layer. It could be adopted as an optional component for that layer, but it covers neither infrastructure provisioning nor the release workflow.

The platform has several user types (Dev, IT Ops, QA, Management), and each works with it differently.

## Decision
- Build the platform in-house instead of adopting a vendor IDP.
- Put the business logic and audit trail in one dedicated backend API.
- Provide several clients on top of that API: a CLI for pipelines and a web UI for visibility and approvals.

## Consequences
- The rules (approvals, policy merge, audit) are enforced in one place, whichever client is used.
- The team owns the full build and maintenance cost, including the features vendors already provide (catalogue, UI).
- SCORE stays available as an optional runtime-layer format later; this decision does not rule it out.
- Each client is a thin layer over the API; new surfaces (for example, portal integrations) can be added without duplicating logic.

## Related
- [context.md](../context.md)
- [0002 Pulumi Automation API](0002-pulumi-automation-api.md), [0004 two-layer definition model](0004-two-layer-definition-model.md), [0005 B/C first](0005-build-bc-first-design-for-d.md)
- [architecture/workload-definition.md](../architecture/workload-definition.md)
- [open-questions.md](../open-questions.md)
