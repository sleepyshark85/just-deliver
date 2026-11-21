# Multi-cloud: GCP

The design of phase 1.7 ([roadmap](../plans/roadmap.md)): an environment declares its platform, the catalog holds an implementation
per platform, and GCP is the second platform after Azure. It serves N1 (one component shape, now across clouds) and N10 (a platform is
catalog data plus adapters behind the existing ports). Written as the design to build; it becomes as-built as the slices land, and
each fact then moves to its owner ([model.md](model.md), [provisioning.md](provisioning.md), [release-record.md](release-record.md)).
Out of scope: AWS, several platforms inside one environment, the hosted API, remote state and records.

Every GCP behaviour this design relies on is an **assumption** until the spike shows it ([conventions](../engineering/conventions.md#reports-and-context),
"Claims are reproduced"); each is listed in section 9 with the slice that shows it. GCP credentials are not available yet, so the first
GCP slice is that spike, run by the lead.

## 1. The platform in the model

Decided in [ADR 0021](../decisions/0021-platform-dimension.md) (Proposed).

- **Declared once.** `catalog.yaml` gains `platforms`, declared like `environmentTypes` (a name each; a definition naming another is an
  error listing the declared ones). The seed declares `azure`; the GCP slices add `gcp`.
- **Chosen by the environment.** The environment definition gets a required `platform`; `jd env up` copies it to the descriptor. One
  platform per environment; a base descriptor (`--base`) of another platform is an error. Required, not defaulted: a default would choose
  a cloud silently ([standards §4](../engineering/standards.md#4-design-invariants)).
- **Selection is one more exact lookup.** A component's and a runtime's implementations are keyed by platform first, then by environment
  type with the `default` fallback, as today. The platform level has **no fallback**: a component with no entry for the environment's
  platform is an error naming the component and the platform, so Azure data can never reach a GCP environment. It is the existing
  `select` annotation nested once more (`select: env.platform`, no `fallback`), with a new role `env.platform` and namespace `platform`
  ([documents.md](documents.md) §3); no new concept.
- **`uses` moves into the platform entry.** What a component uses depends on how it is built: `database` uses `cosmos-account` on Azure
  and nothing shared on GCP. Shared resources stay one part each and gain a required `platform`; a part may read only shared resources of
  its own platform (a load error otherwise), and `jd env up` reaches only those.
- **Policies** gain a `platform` match criterion (a node- and workload-scope `match` key, like `environmentType`). A policy without it
  applies on every platform; `enforce-monitoring` stays so. A policy that sets an Azure-only field (the walkthrough's `limit-revisions`)
  must match `platform: azure`, or the template check refuses the GCP graph.
- **Not changed:** node ids, stack names and hashes carry no platform (environment names are unique per installation, so stacks cannot
  collide); `env.*` still reads only `name`, `region`, `type` (boundary 4: nothing needs `env.platform`); templates are already namespaced
  (`templates/azure/…`, `templates/gcp/…`); `Naming` kinds are per resource kind, so GCP adds kinds.

**Existing files.** The seed catalog moves each implementation map under `azure:` and each shared resource gets `platform: azure`; the
sample definitions get `platform: azure`. A catalog or definition in the old shape fails at load with a `message` saying what to add,
as `probe.scheme` did. A descriptor written before this phase lacks `platform` and is refused with "re-run `jd env up`", which changes
nothing deployed and rewrites the descriptor. **The deployed-config snapshot stays byte-identical:** the same Azure implementation is
selected, so every node's config, template and stack name is unchanged; that is S28b's proof.

**Rejected, simpler on paper:** (a) *types per platform* (`dev-gcp`): no schema change, but the `default` fallback hands a GCP type the
Azure implementation silently, policies matching `dev` miss `dev-gcp`, and types multiply by platforms; (b) *a catalog per platform*:
no engine change, but each component's contract is written twice and drifts (principles 8, 9), and nothing stops a GCP environment
being deployed with the Azure catalog.

## 2. Layers on GCP

| Azure today | GCP |
|---|---|
| Subscription = the shared layer's boundary | One GCP project = the shared layer's boundary (the sandbox's; per [ADR 0008](../decisions/0008-enforcement-in-landing-zone.md) the landing zone owns it) |
| Resource providers registered by the sandbox | APIs enabled by the GCP sandbox (`run`, `firestore`, `iam`, …), not by jd |
| `shared-group`, `env-group` (resource groups) | none: GCP has no resource group. Resources carry labels (`project`, `env`) where the type supports them; teardown is by stack, and the cleanup script lists by label and name prefix (service accounts have no labels) |
| `apps-env` (Container Apps environment) | none: Cloud Run services are regional, with no environment object |
| `log-analytics` (capped workspace) | the project's Cloud Logging `_Default` bucket, which exists without jd: added to the implicit, platform-owned list ([standards §4](../engineering/standards.md#4-design-invariants)); never a grant target |

The project id is not in any definition: like the Azure subscription it comes from the runner (`GOOGLE_PROJECT` for Pulumi and the
adapters; C1), so jd stays stateless. On GCP the shared and environment layers may deploy nothing today; `jd env up` still writes the
descriptor (name, region, type, platform, offers, approval), which is what workloads resolve against.

**Monitoring.** The `monitoring` contract (`connectionString`) is Application Insights' shape, and the sample definitions name it
(`APPLICATIONINSIGHTS_CONNECTION_STRING`). On GCP, stdout logs and request metrics need no resource and no grant (C14), while telemetry
the app sends itself needs project-level roles, which would be a grant on a shared resource (principle 12). Open choice 4.

## 3. Roles and grants

- `Roles` values widen from a GUID to `anyOf` [Azure role-definition GUID, GCP role name `roles/…` or `projects/<p>/roles/…`]. One
  namespace: role names differ per cloud anyway, and a GCP part reading an Azure GUID fails at deploy, never widening access.
- **The grant target rule is unchanged** ([model.md §7](model.md#7-policies-and-grants)): a grant's target input is exactly
  `${<node>.<output>}` of its own part or the runtime. GCP grants use resource-level IAM (`…IamMember` on the Cloud Run service, bucket,
  topic). Where GCP has none, the grant template takes the owned resource as its target and binds the role on the project **with an IAM
  condition naming that resource** (Firestore, C10); the condition is the template's job, and its slice has a required test that the
  rendered binding carries it.
- The GCP deployer may grant only the roles the sandbox lists (C11), as the Azure sandbox's ABAC condition does.

## 4. The runtime on Cloud Run

The `container-app` runtime gains a `gcp` implementation; the release flow ([provisioning.md](provisioning.md#release-flow-the-runtime-node))
and its four steps are unchanged.

- **Traffic shape.** The Cloud Run traffic item `{revisionName, percent, latestRevision, tag}` maps onto the `release` block as data
  (`revisionKey: revisionName`, `weightKey: percent`, `latestKey: latestRevision`, `fullWeight: 100`). The template sets the revision name
  `<service>-<suffix>`; `suffix.maxLength` follows Cloud Run's name rules; a reused revision name is refused as on Azure, which the salt
  already handles (C2).
- **The dark revision's address.** A Cloud Run revision with no traffic has no URL unless tagged (C3). The `release` block gains an
  optional `tagKey` and `darkTag`: when set, the dark step's traffic **starts** with `{latestKey: true, weightKey: 0, tagKey: darkTag}`, so the
  tagged item is first on both a first and a later deploy and the template outputs its URL by index as `fqdnOutput`. Data, not a platform
  branch; Container Apps labels would fit the same keys. Built only if the spike shows it is needed (S28g).
- **Identity.** No system-assigned identity: the runtime part gets an explicit service-account node, created with the workload and
  owned by it, so [ADR 0012](../decisions/0012-system-assigned-identity.md)'s intent (one identity per workload, its lifecycle the
  workload's) holds. The Cloud Run node runs as it and outputs its member as `principalId`, so the grants read `${runtime.principalId}` and
  stay `AfterRuntime` (C4). Never the default compute account.
- **Ingress.** Public, as `externalIngress: true` is on Azure: the invoker check off on the service, or an `allUsers` invoker binding as a
  grant node on the runtime (C5).
- **Free tier.** Request-based billing, `minInstances: 0`, `maxInstances: 1`, the smallest CPU and memory (C13).
- **Image.** Cloud Run pulls from Artifact Registry or Docker Hub, not GHCR, where the sample images are (C6). Open choice 3.

## 5. Database on GCP: a security boundary

Today a workload's grant is on its own Cosmos container only (boundaries 1, 2). Firestore IAM is at most per database, and the free
quota covers one database per project (C9). The user decides (open choice 2):

| Option | Isolation | Cost |
|---|---|---|
| **A. A Firestore database per workload**, data role bound on the project with a condition naming that database (C10) | Same as Azure: a workload reaches only its own database | One database free per project; the others pay per operation and GiB with no fixed fee (C9), cents at test volume, but not free: [standards §6](../engineering/standards.md#6-azure-and-the-free-tier) needs the user's approval |
| B. One Firestore database per environment, a collection per workload | **Weaker:** IAM cannot scope to a collection, so every workload of the environment can read every other's data (principle 12, D19 broken) | Free (the one database) |
| C. Cloud SQL, a database and user per workload | Per database user, outside IAM grants | No free tier: about USD 7+/month for the smallest instance |
| D. A Cloud Storage bucket per workload as the store (`engine: gcs`) | Same as Azure: bucket-level IAM, a plain grant | Free in `us-east1`, `us-west1`, `us-central1` only (5 GB) |

**Recommendation: A**, with the user's cost approval and the budget as backstop: the same boundary as Azure, a document database like
Cosmos, and a plain grant template. D is the free alternative if the user prefers no paid service, at the price of a store that is not a
database. B is listed only to be refused.

## 6. Identity, records and approvals

Decided in [ADR 0022](../decisions/0022-neutral-deployer-identity.md) (Proposed).

- **Neutral identity.** `DeployerIdentity(TenantId, ClientId, ObjectId)` becomes `DeployerIdentity(Directory, Principal, Name)`:
  `Principal` is the stable key every comparison uses (Entra `oid`; the GCP service account's unique id, C8); `Directory` is where it lives
  (Entra `tid`; on GCP the issuer); `Name` is for people (Entra `appid`; the GCP account's email). Approve's listing, repeat and
  separation checks and `QualificationReader`'s deployers compare `Principal` only, as they compare `objectId` today.
- **Records.** `identity` in the release and approval records becomes `{directory, principal, name}`; the records' `environment` gains
  `platform`. **No version field:** as with `tier`, the old shape stays in the schema as a `oneOf` branch marked "read, never written", and
  the readers map `{tenantId, clientId, objectId}` to `{directory, principal, name}`. A record without `environment.platform` was written by
  a jd that knew only Azure and is read as `azure`, a fact about the past, never a default for new data. The golden records gain a
  legacy-shape twin that must read to the same principal.
- **Approvers** in `approval.schema.json` widen from a GUID to `anyOf` [GUID, GCP unique id (digits)].
- **Promotion stays on one platform:** `jd promote` refuses a target whose platform differs from the qualifying records' (open choice 5),
  since IQ on one cloud says nothing about the other.

## 7. Ports, adapters and composition

| Port | Azure (`jd.azure`) | GCP (new `jd.gcp`, its SDKs only, standards §3) |
|---|---|---|
| `IDeployerIdentity` | `ArmDeployerIdentity`: token claims | `GcpDeployerIdentity`: an Application Default Credentials token, asked of Google's token-info endpoint (C8) |
| `ILiveRevisions` | `ArmLiveRevisions`: ARM revisions | `GcpLiveRevisions`: Cloud Run Admin API, revisions with traffic percent and images, by the runtime's `resourceIdOutput` (C7) |
| credential | `ARM_*` service principal, else the Azure CLI sign-in | `GOOGLE_APPLICATION_CREDENTIALS` service account, else the `gcloud` application-default sign-in; Pulumi's provider reads the same (C1) |

`IBackEndProvider`, `IRevisionProbe`, `IRecordStore`, `ITemplates` and the clock are platform-neutral and stay one each.

**Choosing per environment.** `Connections.Identity` and `LiveRevisions` take the environment's platform name:
`Func<string, IDeployerIdentity>`, `Func<string, ILiveRevisions>`. Every use case that reaches them has parsed a descriptor first
(identity comes after the descriptor in every recorded run and in `approve`), so it passes `platform`. `Composition` holds the only
table from platform name to adapter, each adapter declaring its name as a protocol constant; a platform with no adapter is
`ConnectionFailed` naming it, before anything is deployed. R1–R3 hold unchanged ([application.md §2](application.md#2-what-the-cli-keeps-and-the-rules)):
the table is composition, the use case passes a value.

## 8. State, guardrails and the GCP gate

- **State and records stay local** for both platforms ([provisioning.md](provisioning.md#backend-and-state)): one local backend holds Azure
  and GCP stacks. A GCS backend would be only another `BackendUrl` (`gs://…`, which Pulumi supports), set in the composition root;
  out of scope, as Azure Blob state is.
- **GCP sandbox** (`tools/sandbox/gcp/`, run by the user as project Owner, idempotent like the Azure one): enables the APIs; creates the
  deployer service account with exactly the roles jd needs (Cloud Run, Firestore, service-account admin and user, project IAM admin
  limited to the grantable roles, C11). **The role set is the allowlist:** with no organization there are no organization policies (C12),
  so instead of Azure's deny-list the deployer simply cannot create what its roles do not cover (no VMs, no Cloud SQL, no registry).
  Quota overrides cap Cloud Run instances and CPU; a budget alerts at 50% and 100% (alerts only, as on Azure). Region is not enforceable
  without an organization: it stays the definition's data and the review's. The credentials file `~/.just-deliver/<project>.gcp.env`
  (mode 600) holds `GOOGLE_PROJECT` and the key path.
- **Free-tier rules for GCP** join [standards §6](../engineering/standards.md#6-azure-and-the-free-tier) in S28e: Cloud Run request-billed,
  `minInstances: 0`, smallest size; Firestore per open choice 2; nothing else without the user's approval.
- **GCP gate**, the [Azure gate](../engineering/conventions.md#azure-gate)'s twin: only the lead runs `tools/verify.sh --gcp`, one run at a
  time, tests tagged `Category=GCP` and refused unless `JD_GCP_TESTS=1`. **What reaches GCP:** (1) the GCP packages, snapshotted in their
  own golden file (`deployed-config-gcp.json`) so each gate's trigger is its own file's diff; (2) `src/jd.gcp`. The shared code in the Azure
  rule's (2) (`backend-providers`, `ReleaseFlow.cs`, `Qualifier.cs`, `HttpRevisionProbe.cs`) triggers both gates. Both run at the phase's end.

## 9. Claims to show

Each is an assumption until shown by a command and its output, recorded in status.md. All are S28a's unless marked.

| # | Claim |
|---|---|
| C1 | Pulumi's GCP provider, run from a YAML template by the Automation API, takes the project and the credential from `GOOGLE_PROJECT` and `GOOGLE_APPLICATION_CREDENTIALS`, with nothing in the template |
| C2 | A Cloud Run service deployed from YAML accepts traffic items `{revisionName, percent, latestRevision}`, echoes them as an output, takes a revision name `<service>-<suffix>` within Cloud Run's rules, and refuses a reused revision name |
| C3 | A 0%-traffic tag on the latest revision gives a URL that reaches that revision while traffic stays on the live one, and the URL is readable as an output by index |
| C4 | A service runs as an explicit service account the deployer may act as; a grant on the service account's member works right after the dark deploy |
| C5 | Public access works in a project without an organization (invoker check off, or `allUsers`) |
| C6 | Cloud Run pulls a public Docker Hub image by digest and reports the configured reference unchanged on the revision; a GHCR reference is refused |
| C7 | The Cloud Run Admin API lists a service's revisions with traffic percent and container images, read by the service's id with the deployer's credential |
| C8 | Google's token-info answer for the deployer's ADC token names a stable unique id and email, the same on every run |
| C9 | Firestore named databases are created and deleted from YAML; the free quota covers one database per project; further ones bill per use with no fixed fee; a deleted name's reuse delay |
| C10 | A data role bound on the project with a condition naming one Firestore database lets the workload read that database and refuses another |
| C11 | An IAM condition limits which roles the deployer may grant |
| C12 | A project without an organization accepts no organization policies; quota overrides cap Cloud Run; a budget with alerts can be set |
| C13 | Cloud Run's and Firestore's free tiers apply in the chosen region at the sizes above |
| C14 | Cloud Run stdout logs and request metrics appear without any role on the workload's service account |

## 10. Slices

The cap counts production C# lines; catalog data, templates, schemas and scripts are reviewed like code but outside it.

| ID | Slice | Acceptance | Cap |
|---|---|---|---|
| S28a | **GCP spike** (lead runs; needs the user's project and deployer) | C1–C14 shown or refuted, each with its command and output in status.md; this note corrected where one fails | 0 |
| S28b | Platform in the model | §1 built: `platforms`, `platform` on definition and descriptor, nested selection, `uses` per platform, shared resources' `platform`, policy `platform` match; seed and samples migrated; **snapshot byte-identical**. Required tests: no fallback across platforms (a GCP environment never gets an Azure implementation); a part reading another platform's shared resource is refused; a base of another platform is refused; old-shape catalog and descriptor messages | 150 |
| S28c | Neutral identity and records | §6 built; snapshot unchanged. Required tests: a legacy-shape record reads to the same principal; separation holds across the two shapes; an old record reads as `azure`; promotion across platforms refused | 150 |
| S28d | Connections per platform | §7's choice by platform; unknown platform is `ConnectionFailed` with nothing deployed; R1–R3 with empty allowlists | 80 |
| S28e | GCP sandbox, gate, standards | §8: sandbox script, `verify.sh --gcp`, cleanup script, GCP free-tier rules in standards §6, the gate rule in conventions | 0 |
| S28f | `jd.gcp` adapters | identity and live revisions with offline tests at the transport seam and GCP tests (C7, C8 re-shown); errors carry no SDK text or value | 200 |
| S28g | Dark tag (only if C3 needs it) | `tagKey`/`darkTag` in the `release` block; dark traffic starts with the tagged item; Azure snapshot unchanged | 60 |
| S28h | GCP catalog | `gcp` templates and implementations of `container-app`, `database` (choice 2) and `monitoring` (choice 4); GCP sample environments; `deployed-config-gcp.json` | 0 |
| S28i | Sample workloads on both platforms | choices 3 and 4 applied: the sample app picks its store by `engine`; one definition for both clouds; the Azure snapshot regenerated on purpose (input changed) and the Azure gate run | 0 (samples ~200) |
| S28j | E2E on GCP, exit | the exit evidence below; architecture docs as-built; this note reduced to what is not yet elsewhere | 50 |

## 11. Exit evidence

The roadmap row's criteria, evidenced in status.md: S28b–S28d with an unchanged snapshot (the refactor proof); the sample app and worker
`env up`, `release deploy`, `qualify`, `approve` and `promote` on GCP `dev` and `stage` environments with OQ and IQ passed, by the GCP gate;
the GCP sandbox's budget showing no charge beyond choice 2's; the Azure gate green on the phase's head.

## 12. Later phases stay open

- **N2 contract versions (phase 2):** platforms share one contract per component, so a version is per component, not per cloud. Phase 2's
  messaging and secrets components may ship Azure only: a GCP environment offering them is refused by name, never served Azure.
- **N4–N6 (phases 3, 4):** a workload's offers and the registry are per environment, and an environment has one platform. Cloud Run has
  service-level IAM (`run.invoker`), so phase 4's `invoke` grant fits the unchanged target rule. A bind across clouds can read exported
  values only; a cross-cloud grant is not designed here.
- **Mixed platforms in one environment** stay possible: the platform is one selection value, and a later design could select per part.
- **The hosted API (ADR 0001):** `Connections` by platform is what an API host builds too.

## 13. Open choices for the user

| # | Choice | Recommendation |
|---|---|---|
| 1 | The platform as an environment value with nested selection ([ADR 0021](../decisions/0021-platform-dimension.md)), vs types per platform or a catalog per platform | The ADR (§1) |
| 2 | Database isolation on GCP (§5) | A, a Firestore database per workload, approving its per-use cost |
| 3 | Image registry: move the sample images to Docker Hub (one reference everywhere; Container Apps pulls it too), or an Artifact Registry remote repository (a GCP-only reference, which breaks one definition everywhere, principle 10) | Docker Hub |
| 4 | Monitoring on GCP: (a) the contract gains `engine` (`application-insights`, `cloud-logging`) as `database` has, and the GCP implementation is stdout logs with no grant; (b) project-level telemetry roles as a named exception to principle 12; (c) `enforce-monitoring` Azure-only, which breaks the sample definition on GCP | (a); the sample definitions' variable names become neutral (`DATABASE_*`, `MONITORING_*`) in S28i |
| 5 | Promotion across platforms (qualified on Azure, promoted to GCP) | Refused (§6) |
| 6 | The GCP deployer credential: a service-account key file (as the Azure secret, rotated), or workload identity federation (no key; needs a CI issuer) | A key file for the sandbox now; federation with the hosted API |
