# Decision records

One file per decision, ADR style: Context → Decision → Consequences → Related.

**Records are `Proposed — pending review` unless their status says otherwise.** They capture conclusions reached during
investigation so the reasoning is not lost, but none is accepted until reviewed. Accepted (user, 2026-11-03): 0001, 0003 — the
user asked the lead to accept those already decided; the rest are partly built, partly contradicted by later decisions, or not
built yet. 0017 and 0018 record user decisions of 2026-10-18 (S21) that supersede 0004 and part of 0013. On review, change the
status to `Accepted`, `Rejected`, or `Superseded by NNNN`; never edit an accepted record's decision —
supersede it with a new one.

| # | Decision | Origin |
|---|---|---|
| [0001](0001-custom-build-api-and-clients.md) | Custom build; backend API with CLI and web clients | investigation |
| [0002](0002-pulumi-automation-api.md) | Pulumi Automation API, Blob state, job-queued execution | investigation |
| [0003](0003-pulumi-yaml-template-library.md) | Static Pulumi YAML template library, generated config | Pulumi notes |
| [0004](0004-two-layer-definition-model.md) | Team definitions + IT Ops global policies | investigation |
| [0005](0005-build-bc-first-design-for-d.md) | Build B/C first, design for D | investigation |
| [0006](0006-infra-deployed-with-app.md) | Workload infra deployed with the app, no continuous reconciliation | A1 |
| [0007](0007-definition-upload-snapshot.md) | Team stores definition; platform keeps uploaded snapshot | A2 |
| [0008](0008-enforcement-in-landing-zone.md) | Enforcement lives in the landing zone; platform is the paved road | A3 |
| [0009](0009-rbac-split-team-subscription.md) | RBAC split inside team-owned subscriptions | B10 |
| [0010](0010-replacement-protection-classes.md) | Protection classes and plan-content gating for replacements | C11 |
| [0011](0011-requirement-id.md) | Optional `id` on every requirement | C13 |
| [0012](0012-system-assigned-identity.md) | System-assigned managed identity | D17 |
| [0013](0013-container-apps-runtime.md) | Container Apps runtime, multiple-revision mode | E27 |
| [0014](0014-hook-points.md) | preDeploy / verify / postDeploy hook points | E28 |
| [0015](0015-database-migrations.md) | Database migrations | E33 |
| [0016](0016-portal-deep-links.md) | Portal deep-link permissions | G43 |
| [0017](0017-catalog-layer-policies-require.md) | Team definitions over a platform catalog; policies require, never add (supersedes 0004) | S21 |
| [0018](0018-environment-offers-runtime.md) | The environment offers the runtime (supersedes 0013's first decision) | S21 |

Origin IDs refer to [open-questions.md](../open-questions.md).
