# Multi-cloud: GCP

The design of phase 1.7 ([roadmap](../plans/roadmap.md)): an environment declares its platform, the catalog holds an implementation
per platform, and GCP is the second platform after Azure. It serves N1 (one component shape, now across clouds) and N10 (a platform is
catalog data plus adapters behind the existing ports). The decisions are in ADRs [0021](../decisions/0021-platform-dimension.md) (the
platform in the model), [0022](../decisions/0022-neutral-deployer-identity.md) (the deployer identity) and
[0023](../decisions/0023-gcp-runtime-identity.md) (the runtime identity on GCP), all Proposed; this note is how they are built. It becomes
as-built as the slices land, and each fact then moves to its owner ([model.md](model.md), [provisioning.md](provisioning.md),
[release-record.md](release-record.md)). Out of scope: AWS, several platforms inside one environment, the hosted API, remote state and
records.

Every GCP behaviour this design relies on is an **assumption** until the spike shows it ([conventions](../engineering/conventions.md#reports-and-context),
"Claims are reproduced"); each is listed in section 9 with the slice that shows it. GCP credentials are not available yet, so the first
GCP slice is that spike, run by the lead.

## 1. The platform in the model

[ADR 0021](../decisions/0021-platform-dimension.md) holds the decision and the rejected alternatives. Built as:

- `catalog.yaml` gains `platforms`, a name each, referred to as `environmentTypes` are (a definition naming an undeclared one is an error
  listing the declared ones). The seed declares `azure`; S28i adds `gcp`. The environment definition's `platform` is required; `jd env up`
  copies it to the descriptor, and a `--base` descriptor of another platform is an error.
- Selection is the existing `select` annotation nested once more: `implementations` gets `select: env.platform` with no `fallback` around
  today's `select: env.type, fallback: default` ([documents.md](documents.md) §3). New role `env.platform`, new namespace `platform`. A miss
  is the existing `select` error, naming the component and the platform.
- `uses` moves into the platform entry. A shared resource's `platform` is required; the loader refuses a part reading a shared resource of
  another platform, and `jd env up` reaches only its platform's.
- **No policy change.** Node scope already matches `template`, and templates are namespaced (`azure/…`, `gcp/…`): the walkthrough's
  `limit-revisions` matches `azure/container-app` and never reaches a GCP node. No workload-scope policy needs a platform (choice 4).
- Unchanged: node ids, stack names and hashes (environment names are unique per installation, so stacks cannot collide); `env.*` reads
  only `name`, `region`, `type` (boundary 4: nothing needs `env.platform`); `Naming` kinds are per resource kind, so GCP adds kinds.

**Existing files.** The seed catalog moves each implementation map under `azure:` and each shared resource gets `platform: azure`; the
sample definitions get `platform: azure`. A catalog or definition in the old shape fails at load with a `message` saying what to add, as
`probe.scheme` did. A descriptor written before this phase lacks `platform` and is refused with "re-run `jd env up`", which deploys
nothing and rewrites the descriptor (a deliberate document change, §11). **The deployed-config snapshot stays byte-identical:** the same
Azure implementation is selected, so every node's config, template and stack name is unchanged; that is S28b's proof.

## 2. Layers on GCP

| Azure today | GCP |
|---|---|
| Subscription = the shared layer's boundary | One GCP project = the shared layer's boundary (the sandbox's; per [ADR 0008](../decisions/0008-enforcement-in-landing-zone.md) the landing zone owns it) |
| Resource providers registered by the sandbox | APIs enabled by the GCP sandbox (`run`, `firestore`, `iam`, `logging`, …), not by jd |
| `shared-group`, `env-group` (resource groups) | none: GCP has no resource group. Resources carry labels (`project`, `env`) where the type supports them; teardown is by stack, and the cleanup script lists by label and name prefix (service accounts have no labels) |
| `apps-env` (Container Apps environment) | none: Cloud Run services are regional, with no environment object |
| `log-analytics` (capped workspace) | none: each workload's `monitoring` owns its log bucket (choice 4). jd neither creates nor reads the project's `_Default` bucket, so it is not on the platform-owned list ([standards §4](../engineering/standards.md#4-design-invariants)) |

The project id is not in any definition: like the Azure subscription it comes from the runner (`GOOGLE_PROJECT` for Pulumi and the
adapters; C1), so jd stays stateless. On GCP the shared and environment layers deploy nothing; `jd env up` still writes the descriptor
(name, region, type, platform, offers, approval), which is what workloads resolve against.

## 3. Roles and grants

- `Roles` values widen from a GUID to `anyOf` [Azure role-definition GUID, GCP role name `roles/…` or `projects/<p>/roles/…`]. One
  namespace: role names differ per cloud anyway, and a GCP part reading an Azure GUID fails at deploy, never widening access.
- **The grant target rule is unchanged** ([model.md §7](model.md#7-policies-and-grants)). GCP grants use resource-level IAM (`…IamMember`
  on the bucket, service, topic). Where GCP has none (Firestore, C10), the grant template takes the owned resource as its target and binds
  the role on the project **with an IAM condition naming that resource**; the condition is the template's job, and S28i has a required test
  that the rendered binding carries it.
- The GCP deployer may grant only the roles the sandbox lists (C11), as the Azure sandbox's ABAC condition does.

## 4. The runtime on Cloud Run

A new runtime, `cloud-run`, with a `gcp` implementation only (`container-app` keeps only `azure`); a GCP environment offers it
([ADR 0018](../decisions/0018-environment-offers-runtime.md)), and an Azure environment offering it is the §1 selection miss. Workloads
name no runtime, so one definition serves both. The release flow ([provisioning.md](provisioning.md#release-flow-the-runtime-node)) and its
four steps are unchanged.

- **Traffic shape.** The v1 resource `gcp:cloudrun:Service` takes traffic items `{revisionName, percent, latestRevision, tag}`, which map
  onto the `release` block as data (`revisionKey: revisionName`, `weightKey: percent`, `latestKey: latestRevision`, `fullWeight: 100`). The
  template sets the revision name `<service>-<suffix>`; `suffix.maxLength` follows Cloud Run's name rules; a reused revision name is
  refused as on Azure, which the salt handles (C2). The v2 resource `gcp:cloudrunv2:Service` marks the latest revision with an enum
  (`type: TRAFFIC_TARGET_ALLOCATION_TYPE_LATEST`), which a boolean `latestKey` cannot express: if C2 fails on v1, the `release` block
  needs a key-and-value form, designed then.
- **The dark revision's address.** A Cloud Run revision with no traffic has no URL unless tagged (C3). The `release` block gains an
  optional `tagKey` and `darkTag`: when set, the dark step's traffic **starts** with `{latestKey: true, weightKey: 0, tagKey: darkTag}`, so the
  tagged item is first on both a first and a later deploy and the template outputs its URL by index as `fqdnOutput`. Data, not a platform
  branch. Built only if the spike shows it is needed (S28g).
- **Identity** ([ADR 0023](../decisions/0023-gcp-runtime-identity.md)). The runtime part has two nodes: `identity` (`gcp/service-account`)
  and `runtime` (`gcp/cloud-run-service`, `serviceAccount: ${identity.email}`), which outputs the account's member as `principalId`, so the
  grants read `${runtime.principalId}` and stay `AfterRuntime` (C4). The runtime implementation names the input that carries the identity
  (`identityInput: serviceAccount`); the loader, and the graph builder again after policies, require that input on the `runtime` node to
  be exactly `${<node>.<output>}` of another node of the runtime's own part, as the grant target rule does. The template declares the
  input with no default, so the default compute account is never used. Absent on Azure, where the identity is system-assigned.
- **Ingress.** Public, as `externalIngress: true` is on Azure: the invoker check off on the service, or an `allUsers` invoker binding as a
  grant node on the runtime (C5).
- **Free tier.** Request-based billing, `minInstances: 0`, `maxInstances: 1`, the smallest CPU and memory (C13).
- **Image.** Cloud Run pulls from Artifact Registry or Docker Hub, not GHCR, where the sample images are (C6). Open choice 3.

## 5. Contracts on GCP: database and monitoring

**Database: a security boundary.** Today a workload's grant is on its own Cosmos container only (boundaries 1, 2). Firestore IAM is at
most per database, and the free quota covers one database per project (C9). The user decides (open choice 2). The contract is
`[engine, endpoint, database, container]`:

| Option | Exports on GCP | Isolation | Cost |
|---|---|---|---|
| **A. A Firestore database per workload** (`store`), data role bound on the project with a condition naming it (§3) | `engine: firestore`, `endpoint: ${store.name}` (`projects/<p>/databases/<d>`), `database: ${store.databaseId}`, `container`: a collection name from `name()` (a collection is not deployed) | Same as Azure: a workload reaches only its own database | One database free per project; the others pay per operation and GiB with no fixed fee (C9), cents at test volume, but not free: [standards §6](../engineering/standards.md#6-azure-and-the-free-tier) needs the user's approval |
| B. One Firestore database per environment, a collection per workload | as A, the database in the environment part | **Weaker:** IAM cannot scope to a collection, so every workload of the environment reads every other's data (principle 12, D19 broken) | Free (the one database) |
| C. Cloud SQL, a database and user per workload | `engine: postgres`, the instance's host, the database, a schema | Per database user, outside IAM grants | No free tier: about USD 7+/month for the smallest instance |
| D. A Cloud Storage bucket per workload (`bucket`, `engine: gcs`), `storage.objectUser` on it, a resource-level grant | `engine: gcs`, `endpoint: ${bucket.url}`, `database: ${bucket.name}`, `container`: an object prefix from data | Same as Azure | Free in `us-east1`, `us-west1`, `us-central1` only (5 GB) |

No option changes the contract, the schemas or any Azure input; each is GCP catalog data (S28i), and the sample app gains the engine's
client (S28j). **Recommendation: A**, with the user's cost approval and the budget as backstop: the same boundary as Azure and a document
database like Cosmos, through the conditioned project binding of §3. D is the free alternative if the user prefers no paid service, at the
price of a store that is not a database. B is listed only to be refused.

**Monitoring.** The contract `[connectionString]` is Application Insights' shape, and the sample definitions name it
(`APPLICATIONINSIGHTS_CONNECTION_STRING`). A part needs at least one node (`part.schema.json`), so GCP must deploy something the workload
owns. The user decides (open choice 4):

| Option | GCP class part | Contract and Azure inputs |
|---|---|---|
| **(a) An owned log bucket and route, no grant** | `logs` (`gcp/logging-bucket`, 30-day retention, inside the free ingestion) and `route` (`gcp/logging-sink`, the service's entries into `logs`; reads `${runtime.serviceName}`, so it deploys `AfterRuntime`). No grant: Cloud Run writes stdout with no role (C14), and a sink into a bucket of its own project needs no writer grant (C15). The app writes JSON to stdout | The contract becomes `[engine, destination]`: Azure `engine: application-insights`, `destination: ${appinsights.connectionString}`; GCP `engine: cloud-logging`, `destination: ${logs.id}`. Changes every definition's `${resource.monitoring.connectionString}`: S28j, an input change (§11) |
| (b) Project-level telemetry roles (the app sends traces and metrics) | as (a), plus a grant of `cloudtrace.agent` and `monitoring.metricWriter` on the project | As (a). GCP has no resource-level IAM for these, so the grant's target is the project: an exception to principle 12 and to the grant target rule, an ADR and the user's |
| (c) `enforce-monitoring` on Azure only; GCP offers no monitoring | none | Workload-scope policies gain a `platform` match key (S28i, with a code cap), because a workload-scope `match` cannot name a template. The sample definitions declare monitoring, so GCP refuses them: two definitions, principle 10 broken |

## 6. Identity, records and approvals

[ADR 0022](../decisions/0022-neutral-deployer-identity.md) holds the decision. Built as:

- `DeployerIdentity(Directory, Principal, Name)` replaces `(TenantId, ClientId, ObjectId)`. On GCP `GcpDeployerIdentity` fills
  `Principal` with the service account's unique id, `Name` with its email and `Directory` with the project the email names (C8). Approve's
  listing, repeat and separation checks and `QualificationReader`'s deployers compare `Principal` only, as they compare `objectId` today.
- `identity` in the release and approval record schemas becomes `{directory, principal, name}`. As with `tier`, the old shape stays as a
  `oneOf` branch marked "read, never written", and the readers map `{tenantId, clientId, objectId}` to it: jd's own evidence format
  ([standards §1](../engineering/standards.md#1-nothing-hard-coded)), not catalog data. The golden records gain a legacy-shape twin that
  must read to the same principal.
- `approvers` in `approval.schema.json` widen from a GUID to `anyOf` [GUID, GCP unique id (digits)].
- Records gain no platform field under choice 5's recommendation; promotion follows `qualifiedOn`.

## 7. Ports, adapters and composition

| Port | Azure (`jd.azure`) | GCP (new `jd.gcp`, Google SDKs only) |
|---|---|---|
| `IDeployerIdentity` | `ArmDeployerIdentity`: token claims | `GcpDeployerIdentity`: an Application Default Credentials token, asked of Google's token-info endpoint (C8) |
| `ILiveRevisions` | `ArmLiveRevisions`: ARM revisions | `GcpLiveRevisions`: Cloud Run Admin API, revisions with traffic percent and images, by the runtime's `resourceIdOutput` (C7) |
| credential | `ARM_*` service principal, else the Azure CLI sign-in | `GOOGLE_APPLICATION_CREDENTIALS` service account, else the `gcloud` application-default sign-in; Pulumi's provider reads the same (C1) |

`IBackEndProvider`, `IRevisionProbe`, `IRecordStore`, `ITemplates` and the clock are platform-neutral and stay one each.

**Choosing per environment.** `Connections.Identity` and `LiveRevisions` take the environment's platform name:
`Func<string, IDeployerIdentity>`, `Func<string, ILiveRevisions>`. Every use case that reaches them has parsed a descriptor first
(identity comes after the descriptor in every recorded run and in `approve`), so it passes `platform`. `Composition` holds the only table
from platform name to adapter; each adapter project declares its platform's name as a protocol constant, the only place C# spells a
platform (a [standards §1](../engineering/standards.md#1-nothing-hard-coded) exception, written in S28d). A platform with no adapter is
`ConnectionFailed` naming it, before anything is deployed. R1–R3 hold unchanged ([application.md §2](application.md#2-what-the-cli-keeps-and-the-rules)):
the table is composition, the use case passes a value.

**`jd.gcp` in the rules** (S28f): a [standards §3](../engineering/standards.md#3-architecture-and-separation-of-concerns) row (`jd.core`
and Google SDKs; not the resolver, orchestrator, Pulumi, backend providers or CLI) and "Google SDKs only in `jd.gcp`"; the §1 protocol
constant exception widened from `jd.azure` to `jd.gcp` (an endpoint, a token scope); in `tools/verify.sh`, the project allowlist applied
to `src/jd.gcp` (`jd.core`, `Google.*` packages, the .NET runtime), the inner-project check refusing `Google` packages and `jd.gcp`, and
the R2 grep naming `jd\.gcp` ([application.md](application.md) R2 updated).

## 8. State, guardrails and the GCP gate

- **State and records stay local** for both platforms ([provisioning.md](provisioning.md#backend-and-state)): one local backend holds Azure
  and GCP stacks. A GCS backend would be only another `BackendUrl` (`gs://…`, which Pulumi supports), set in the composition root;
  out of scope, as Azure Blob state is.
- **GCP sandbox** (`tools/sandbox/gcp/`, run by the user as project Owner, idempotent like the Azure one): enables the APIs; creates the
  deployer service account with exactly the roles jd needs (Cloud Run, Firestore, Logging buckets and sinks, service-account admin and
  user, project IAM admin limited to the grantable roles, C11). **The role set is the allowlist:** with no organization there are no
  organization policies (C12), so instead of Azure's deny-list the deployer cannot create what its roles do not cover (no VMs, no Cloud
  SQL, no registry). Quota overrides cap Cloud Run instances and CPU; a budget alerts at 50% and 100% (alerts only, as on Azure). Region
  is not enforceable without an organization: it stays the definition's data and the review's. The credentials file
  `~/.just-deliver/<project>.gcp.env` (mode 600) holds `GOOGLE_PROJECT` and the key path (C16).
- **Free-tier rules for GCP** join [standards §6](../engineering/standards.md#6-azure-and-the-free-tier) in S28e: Cloud Run request-billed,
  `minInstances: 0`, smallest size; Firestore per open choice 2; log buckets at the default retention; nothing else without the user's
  approval.
- **GCP gate**, the [Azure gate](../engineering/conventions.md#azure-gate)'s twin: only the lead runs `tools/verify.sh --gcp`, tests
  tagged `Category=GCP` and refused unless `JD_GCP_TESTS=1`, one run at a time under its own lock, as the Azure gate's (one free Firestore
  database per project). Names are deterministic, so if C9 or C15 shows a deleted name is held, the gate's tear-down and re-create must
  respect it; S28a says how. **What reaches GCP:** (1) the GCP packages, snapshotted in their own golden file (`deployed-config-gcp.json`)
  so each gate's trigger is its own file's diff; (2) `src/jd.gcp`. The shared code in the Azure rule's (2) (`backend-providers`,
  `ReleaseFlow.cs`, `Qualifier.cs`, `HttpRevisionProbe.cs`) triggers both gates. Both run at the phase's end.

## 9. Claims to show

Each is an assumption until shown by a command and its output, recorded in status.md. All are S28a's.

| # | Claim |
|---|---|
| C1 | Pulumi's GCP provider, run from a YAML template by the Automation API, takes the project and the credential from `GOOGLE_PROJECT` and `GOOGLE_APPLICATION_CREDENTIALS`, with nothing in the template |
| C2 | A v1 `gcp:cloudrun:Service` deployed from YAML accepts traffic items `{revisionName, percent, latestRevision}`, echoes them as an output, takes a revision name `<service>-<suffix>` within Cloud Run's rules, and refuses a reused revision name |
| C3 | A 0%-traffic tag on the latest revision gives a URL that reaches that revision while traffic stays on the live one, and the URL is readable as an output by index |
| C4 | A service runs as an explicit service account the deployer may act as; a grant on the service account's member works right after the dark deploy |
| C5 | Public access works in a project without an organization (invoker check off, or `allUsers`) |
| C6 | Cloud Run pulls a public Docker Hub image by digest and reports the configured reference unchanged on the revision; a GHCR reference is refused |
| C7 | The Cloud Run Admin API lists a service's revisions with traffic percent and container images, read by the service's id with the deployer's credential |
| C8 | Google's token-info answer for the deployer's ADC token names a stable unique id and an email naming the project, the same on every run |
| C9 | Firestore named databases are created and deleted from YAML; the free quota covers one database per project; further ones bill per use with no fixed fee; how long a deleted name is held |
| C10 | A data role bound on the project with a condition naming one Firestore database lets the workload read that database and refuses another |
| C11 | An IAM condition limits which roles the deployer may grant |
| C12 | A project without an organization accepts no organization policies; quota overrides cap Cloud Run; a budget with alerts can be set |
| C13 | Cloud Run's, Firestore's and Cloud Logging's free tiers apply in the chosen region at the sizes above |
| C14 | Cloud Run stdout logs and request metrics appear without any role on the workload's service account |
| C15 | A log bucket and a sink filtered by service name are created and deleted from YAML; the sink into a bucket of its own project needs no writer grant; how long a deleted bucket's name is held |
| C16 | A service-account key can be created in a project with no organization (no key-creation policy applies) |

## 10. Slices

The cap counts production C# lines; catalog data, templates, schemas and scripts are reviewed like code but outside it.

| ID | Slice | Acceptance | Cap |
|---|---|---|---|
| S28a | **GCP spike** (lead runs; needs the user's project and deployer) | C1–C16 shown or refuted, each with its command and output in status.md; this note corrected where one fails | 0 |
| S28b | Platform in the model | §1 built: `platforms`, `platform` on definition and descriptor, nested selection, `uses` per platform, shared resources' `platform`; seed and samples migrated; **snapshot byte-identical**; descriptors gain `platform` (§11). Required tests: no fallback across platforms (a GCP environment never gets an Azure implementation); a part reading another platform's shared resource is refused; a base of another platform is refused; old-shape catalog, definition and descriptor messages | 150 |
| S28c | Neutral identity | §6 built; snapshot unchanged; new records write the neutral identity (§11). Required tests: a legacy-shape record reads to the same principal; approval counting and separation hold across the two shapes; under choice 5 (b) or (c), promotion across platforms is refused | 120 |
| S28d | Connections per platform | §7's table, Azure's adapters only; the standards §1 exception for the platform name; unknown platform is `ConnectionFailed` with nothing deployed; R1–R3 with empty allowlists | 80 |
| S28e | GCP sandbox, gate, standards | §8: sandbox script, `verify.sh --gcp` with its lock, cleanup script, GCP free-tier rules in standards §6, the gate rule in conventions | 0 |
| S28f | `jd.gcp` adapters | identity and live revisions with offline tests at the transport seam and GCP tests (C7, C8 re-shown), wired into §7's table; §7's "`jd.gcp` in the rules"; errors carry no SDK text or value | 200 |
| S28g | Dark tag (only if C3 needs it) | `tagKey`/`darkTag` in the `release` block; dark traffic starts with the tagged item; Azure snapshot unchanged | 60 |
| S28h | Runtime identity rule | §4's `identityInput` (ADR 0023) in the runtime schema, checked by the loader and the graph builder after policies; Azure snapshot unchanged. Required tests: a literal identity is refused; one reading a shared or environment resource, another part, `env.*`, `role.*` or the `runtime` node itself is refused; a policy `set` or `default` of the input is refused; a runtime node without its declared input is refused | 60 |
| S28i | GCP catalog | `gcp` platform; `cloud-run`; `gcp` implementations of `database` (choice 2) and `monitoring` (choice 4); GCP sample environments; `deployed-config-gcp.json`. Required tests, on the GCP snapshot: the service runs as its own `identity` node's account, never the default compute account; under A the Firestore binding carries the condition naming the workload's own database; no GCP grant binds on the project without a condition | 0 |
| S28j | Sample workloads on both platforms | choices 3 and 4 applied: the monitoring contract change (choice 4), the sample app picks its store and telemetry by `engine`; one definition for both clouds; the Azure snapshot regenerated on purpose (§11) and the Azure gate run | 0 (samples ~200) |
| S28k | E2E on GCP, exit | the exit evidence below; architecture docs as-built; this note reduced to what is not yet elsewhere | 50 |

## 11. Exit evidence

The roadmap row's criteria, evidenced in status.md: S28b–S28d and S28g–S28h with an unchanged snapshot (the refactor proof); the sample
app and worker `env up`, `release deploy`, `qualify`, `approve` and `promote` on GCP `dev` and `stage` environments with OQ and IQ passed,
by the GCP gate; the GCP sandbox's budget showing no charge beyond choice 2's; the Azure gate green on the phase's head.

**Deliberate changes, the exceptions to "the same written documents for the same inputs"** (as phase 1's monitoring move was):

| Slice | Change | Old documents |
|---|---|---|
| S28b | Every descriptor `jd env up` writes carries `platform`; catalog and definitions move to the platform shape (inputs; the snapshot stays byte-identical) | A descriptor without `platform` is refused until `jd env up` rewrites it; nothing is deployed |
| S28c | New release and approval records write `identity: {directory, principal, name}` (and `environment.platform` only if choice 5 is (c)) | Read as before, never rewritten |
| S28j | The sample definitions' monitoring reference and variable names (choices 3, 4): Azure inputs change, so the Azure snapshot changes, by the Azure gate | — |

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
| 1 | The platform as an environment value with nested selection ([ADR 0021](../decisions/0021-platform-dimension.md)), vs types per platform or a catalog per platform | The ADR |
| 2 | Database isolation on GCP (§5) | A, a Firestore database per workload, approving its per-use cost |
| 3 | Image registry: move the sample images to Docker Hub (one reference everywhere; Container Apps pulls it too), or an Artifact Registry remote repository (a GCP-only reference, which breaks one definition everywhere, principle 10) | Docker Hub |
| 4 | Monitoring on GCP (§5): (a) an owned log bucket and route, no grant; (b) project-level telemetry roles, an exception to principle 12; (c) `enforce-monitoring` Azure only | (a); the sample definitions' variable names become neutral (`DATABASE_*`, `MONITORING_*`) in S28j |
| 5 | Promotion across platforms (qualified on Azure, promoted to GCP): (a) allowed as the approval policy says: `qualifiedOn` is explicit data its owner chose, so records gain no platform field, readers no legacy mapping and `promote` no check; (b) refused by `promote` comparing the two environments' descriptors, still no record field; (c) refused from the records, which gain `environment.platform`, the legacy value in the record schema | (a), the simplest; (b) if the user wants cross-cloud promotion refused |
| 6 | The GCP deployer credential: a service-account key file (as the Azure secret, rotated; C16), or workload identity federation (no key; needs a CI issuer) | A key file for the sandbox now; federation with the hosted API |
