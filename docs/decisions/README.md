# Decision records

The only home of decisions. One file per decision: Context → Decision → Consequences → Related. Other docs link to an ADR
rather than restate it.

**Status rule.** A record is `Proposed — pending review` until the user reviews it; only the user changes a status (to `Accepted`,
`Rejected` or `Superseded by NNNN`). An accepted record's text is never edited: a change is a new record that supersedes it. A
Proposed record's wording may be corrected to match what is built.

| # | Decision | Status | Origin |
|---|---|---|---|
| [0001](0001-custom-build-api-and-clients.md) | Custom build; backend API with CLI and web clients | Accepted | investigation |
| [0002](0002-pulumi-automation-api.md) | Pulumi Automation API, Blob state, job-queued execution | Proposed | investigation |
| [0003](0003-pulumi-yaml-template-library.md) | Static Pulumi YAML template library, generated config | Accepted | Pulumi notes |
| [0004](0004-two-layer-definition-model.md) | Team definitions + IT Ops global policies | Superseded by 0017 | investigation |
| [0005](0005-build-bc-first-design-for-d.md) | Build B/C first, design for D | Proposed | investigation |
| [0006](0006-infra-deployed-with-app.md) | Workload infra deployed with the app, no continuous reconciliation | Proposed | A1 |
| [0007](0007-definition-upload-snapshot.md) | Team stores definition; platform keeps uploaded snapshot | Proposed | A2 |
| [0008](0008-enforcement-in-landing-zone.md) | Enforcement lives in the landing zone; platform is the paved road | Proposed | A3 |
| [0009](0009-rbac-split-team-subscription.md) | RBAC split inside team-owned subscriptions | Proposed | B10 |
| [0010](0010-replacement-protection-classes.md) | Protection classes and plan-content gating for replacements | Proposed | C11 |
| [0011](0011-requirement-id.md) | Optional `id` on every requirement | Proposed | C13 |
| [0012](0012-system-assigned-identity.md) | System-assigned managed identity | Proposed | D17 |
| [0013](0013-container-apps-runtime.md) | Container Apps runtime, multiple-revision mode | Proposed; first decision superseded by 0018 | E27 |
| [0014](0014-hook-points.md) | preDeploy / verify / postDeploy hook points | Proposed | E28 |
| [0015](0015-database-migrations.md) | Database migrations | Proposed | E33 |
| [0016](0016-portal-deep-links.md) | Portal deep-link permissions | Proposed | G43 |
| [0017](0017-catalog-layer-policies-require.md) | Team definitions over a platform catalog; policies require, never add | Accepted | S21 |
| [0018](0018-environment-offers-runtime.md) | The environment offers the runtime | Accepted | S21 |
| [0019](0019-engine-knows-concepts.md) | The engine knows concepts, schemas bind fields | Proposed | S24 |
| [0020](0020-stack-per-graph-node.md) | One Pulumi stack per graph node, the environment in its name | Proposed | built (S07, S12) |

Origin: an open-question ID ([open-questions.md](../open-questions.md)), a slice ID (its PR and design note are in git history and
[status.md](../plans/status.md)), or the investigation notes.
