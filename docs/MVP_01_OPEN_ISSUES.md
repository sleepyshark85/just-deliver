# MVP Phase 1 — Open Issues

Things not yet resolved for [MVP_01](MVP_01.md). Mapping-layer specifics live in [MAPPING_DESIGN.md §6](architecture/MAPPING_DESIGN.md) and are not repeated here — this covers everything else, plus the decisions that layer is waiting on.

Status of the code as of writing: Pulumi definitions for 8 resources, 4 mapping files, a workload schema and a sample workload, and a `provisioner` sample that hardcodes a 5-stack sequence. No CLI, no resolver, no policy layer.

---

## 1. Blockers against MVP_01's own success criteria

Measured against the five criteria in `MVP_01.md`:

| Criterion | Blocked by |
|---|---|
| 1. Deploy in one command (`platform deploy workload.yaml`) | **No CLI project exists.** `samples/provisioner` is a hardcoded sample, not a command that reads a workload file. |
| 2. App Service created and running | **`container.image` has nothing to bind to.** `app-service/Pulumi.yaml` provisions a Windows .NET app with no image parameter. Nothing can deploy the built artifact. |
| 2. All connection strings auto-injected | **`app-service` has no `appSettings` parameter,** so `container.variables` has nowhere to land. Separately, `monitoring` is policy-attached and therefore unreferenceable, so the App Insights connection string cannot reach the container at all. |
| 3. Under 10 minutes end to end | Untested. Cosmos alone took several minutes in a sequential run; parallelism is designed but not implemented. |
| 4. Proof of concept: teams define, platform provisions | Blocked on the above — the chain from workload file to running app is not connected end to end at any point. |
| 5. CLI is documented and user-friendly | No CLI. |

The single highest-value unblock is the container runtime decision, because criteria 2 and 4 both depend on it: **teach `app-service` to run a Linux container** (`webAppKind: app,linux,container` + `linuxFxVersion: DOCKER|<image>` + `appSettings`), **or** add a Container Apps definition and repoint `compute`. Container Apps additionally resolves `definitions/compute.yml`'s `cpu`/`memory`/`replicas`, which App Service has no equivalent for.

---

## 2. Decisions needed before the resolver is written

Cheap to decide now, expensive after state exists on disk.

| Decision | Why it can't wait |
|---|---|
| **Stack naming scheme** | `DeploymentPackage.Name` becomes the Pulumi stack name, the `workDir` path, and the `Pulumi.<name>.yaml` filename. Mapping deployment IDs (`cosmos`, `app`) are unique per mapping folder, **not per workload** — two workloads requiring a database would share one stack and one state file, and the second deploy would adopt the first's resources. Needs something like `{workload}-{environment}-{deployment-id}`. Changing it later orphans state. |
| **Source of `DeploymentPackage.Version`** | Hardcoded `"dev"` today; the workload schema has no version field. It is part of the `workDir` path, so it determines where state lives. Candidates: the `container.image` tag, a platform release ID, or a new workload field. |
| **Resolver return type** | `List<DeploymentPackage>` discards the resolution chain, which the strategy doc (§5) wants shown before provisioning, after provisioning, and in the audit record. The mapping inputs are gone by then, so it cannot be reconstructed without re-resolving. Decide now whether the resolver returns `{ packages, chain }`. |
| **Is `environment` a workload field or a deploy-time argument?** | Currently a required field in `metadata`, which contradicts the strategy doc — see §3. Affects whether promotion means editing a file or passing a flag. |
| **Where definitions actually live** | `definitions_root: samples/provisioner` means the mapping layer points at a *samples* folder. Also, `DeploymentContent` is a string specifically so the backend provider stays unaware of blob storage — the resolver must not re-introduce that coupling with `File.ReadAllText`. Needs a definition-source abstraction and a real home. |

---

## 3. Contradictions between documents and code

Each of these is two statements that cannot both be true. Worth resolving because they will be read as intent by whoever picks this up.

| Topic | The conflict |
|---|---|
| **Pulumi state backend** | `MVP_01.md` says local state in `.pulumi/` (gitignored); `decisions-and-approach.md` says Azure Blob Storage, self-hosted, no Pulumi Cloud; the code uses `file://~`, which is the user's home directory, not `.pulumi/`. Three different answers. |
| **Database engine** | `MVP_01.md` names PostgreSQL in three places (component list, data flow, success criteria). The code provisions Cosmos DB, chosen because PostgreSQL Flexible Server has no permanent free tier. |
| **One definition, many environments** | `resource-provisioning-strategy.md` §2: *"Same workload definition deploys to dev/staging/prod."* But `metadata.environment` is a **required** field in the schema, so one file cannot target more than one environment. Either environment moves out of the workload, or that principle is wrong. |
| **Secret injection** | `MVP_01.md`: *"Pulumi sets them on App Service during deployment."* `app-service/Pulumi.yaml` has no `appSettings` parameter, so it cannot. |
| **Sample app** | `MVP_01.md` specifies *".NET Web API with `/health` endpoint + database query endpoint"*. `samples/web` is a Blazor Server app with neither, though it does have a Dockerfile. Success criterion 2 (database accessible) cannot be demonstrated without the query endpoint, and `compute.yml` sets `healthCheckPath: /health` against an app that has no such route. |
| **`disableLocalAuth`** | `monitoring.yml` sets `false` with a comment explaining the browser-SDK hazard; `Program.cs` sets `true` under `--wire-managed-identity`. Latent only because nothing consumes the mapping files yet. |

---

## 4. Not built yet

| Component | Notes |
|---|---|
| **CLI** | Success criterion 1. Nothing exists. |
| **Resolver / parser** | workload + mappings → ordered `DeploymentPackage` list. Design discussed; dependency ordering is a DAG at deployment granularity (verified), with parallel execution preferred as a work queue over level barriers, given Cosmos takes minutes while a resource group takes seconds. |
| **Global policy layer** | Step 3 of the resolution chain has no artifacts. Mappings hardcode policy outcomes with comments saying so. The `inject:` half is what would deliver the App Insights connection string. |
| **Override application** | `override_whitelist` is declared in every mapping and nothing reads it. No whitelist enforcement, no production approval gate. |
| **Resource tags** | `enforce-compliance-tags` is specified in the strategy doc (cost-center, compliance, data-classification). **No mapping or definition applies any tag.** Directly undermines the cost-attribution goal. |
| **Destroy / reconciliation** | `IBackEndProvider` has no `DestroyAsync`. Removing a `requires` entry leaves an orphaned stack and live Azure resources. |
| **Health verification** | Success criterion 2 says the app must be verified healthy. Nothing checks. |
| **Secret sourcing** | `ConfigEntry.IsSecret` exists and nothing sets it. No mapping can say "this value comes from Key Vault", which `decisions-and-approach.md` commits to from day one. |
| **Mapping verification as a test** | The static checks (definitions exist, parameters real, `${deployment.*}` resolves, IDs unique, `${resources.*}` declared in `requires`) have been run ad hoc and already caught a real error. Nothing in the repo runs them. |
| **Azure constraint pre-flight** | Preview is client-side and cannot catch an ARM rejection (see §6), but every Azure failure hit so far was a *known, documentable* rule. Checking resolved config against a small rules file before invoking Pulumi would have caught all of them. `jd.definitionvalidator` is the same shape of work. |
| **Tests for `jd.core` and `jd.bp.pulumi`** | Only `jd.definitionvalidator` has tests. The backend provider — including output formatting and null handling — is untested. |
| **Partial-failure handling** | `MVP_01.md` says fail fast, no rollback, "manual cleanup documented for failed deployments". The documentation does not exist, and with multiple stacks a failure leaves an arbitrary subset provisioned. |

---

## 5. Deferred deliberately

Recorded so they are not re-litigated as oversights.

- **Networking.** Every workload resource is public (`publicNetworkAccess: Enabled`, no VNet integration, no private endpoints), despite the landing zone defining VNets and subnets. Fine for MVP; a real gap for the org's actual posture.
- **Landing zone integration.** `definitions/landing-zone-definition.*` exists with no Pulumi definitions behind it. The relationship between provisioning a zone and provisioning a workload on it is undefined. MVP treats the zone as a prerequisite.
- **Remaining resource types.** `cache`, `queue`, `storage`, `secrets`, `cdn` are in the schema enum with no mapping files. Should be rejected at submission, not mid-provision.
- **Region.** All three environments are `southeastasia`. No secondary region, no DR.
- **Approvals**, autoscaling, rollback, secret rotation, cost quotas, GUI — per `MVP_01.md` out-of-scope list.

---

### Azure rules worth encoding as pre-flight checks

Each of these caused a real failure during development, and each is a static rule:

| Rule | Would have caught |
|---|---|
| Log Analytics `skuName` must be `PerGB2018` | the retired `Free` tier — ARM 400 after ~80s |
| `minTlsVersion` must serialise as a string, not a number | `'siteConfig.minTlsVersion' should be of type 'string'` |
| ACR name matches `^[a-zA-Z0-9]{5,50}$` (no hyphens) | a registry named like every other resource |
| Cosmos account name ≤ 44 chars, lowercase | generated names overflowing for long workload names |
| One free-tier Cosmos account per subscription | the second workload deploying to `dev` |
| Resource type tokens exist in the provider schema | `azure-native:insights:Component`, `azure-native:documentdb:DatabaseAccount` |

---

## 6. Code-level rough edges

Small, independent, none blocking.

- `PulumiBackendOptions.ScratchDireisctory` — misspelled public property, referenced in `PulumiBackendProvider`. Rename before anything else depends on it.
- `ConfigEntry` lives in `src/jd.core/bp/ConfigValue.cs` — filename does not match the type, and the class is in the **global namespace** while everything around it is `jd.core.bp`.
- **Boolean stringification.** Pulumi config values are strings and .NET's `bool.ToString()` yields `"True"`; a YAML program declaring `type: Boolean` will reject the capitalised form. Affects any non-string default coming out of a mapping's `environments:` overlay.
- **`${env.name}` is not a key in `environments`.** Every mapping uses it; `_shared.yml` defines only `location` and `container_registry_id`. The resolver must inject the environment's own name as a reserved key.
- **Interpolation is partial, not whole-value.** `rg-${workload.metadata.name}-${env.name}` is two expressions inside one literal — string templating, not a lookup.
- **Plugin race under parallelism.** Concurrent first-run deployments share `PULUMI_HOME` and can race installing the same `azure-native` plugin. Pre-warm once before fanning out.
- **Ordering among independent deployments must be deterministic.** A topological sort admits many valid orders; an unstable one makes previews and diffs differ run to run for no reason. Sort each ready set by ID.
- **`--preview` limitations.** Three distinct ones:
  - It validates each stack in isolation, not the wiring between them. Chained values become placeholders (`"<unknown-until-deployed>"`), so a bad cross-stack reference previews clean. Cheap partial mitigation: use placeholders of the right *shape* — a syntactically valid ARM resource ID, a real-format GUID — so code that parses them (`BuildRoleDefinitionId` currently degrades to `/subscriptions/<unknown-subscription>/...`) and any format-constrained Azure field actually exercises.
  - **It cannot catch Azure-side validation at all.** Both remaining failures this session — the retired Log Analytics `Free` tier, and `minTlsVersion` arriving as a number — only surface when the provider calls ARM. Inherent to a client-side preview; narrowed only by the constraint pre-flight in §4.
  - It runs `RefreshAsync` unconditionally, so it is neither fast nor offline. `PreviewOptions.Refresh` / `UpOptions.Refresh` exist and would let the separate refresh call be dropped and made switchable.
- **`--wire-managed-identity` bundles two decisions.** Granting roles is safe and reversible; `disableLocalAuth: true` silently kills browser telemetry. They belong on separate switches, which also closes the window where App Insights rejects key auth before the metrics-publisher grant lands.
