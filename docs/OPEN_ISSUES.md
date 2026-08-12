# Open Issues

Everything identified but not yet resolved, to be worked through one at a time — some during MVP, some later. Ordered so the things that block other things come first.

## Where the code is

The pipeline from a workload definition to Pulumi calls now runs end to end:

| Piece | Where |
|---|---|
| **Resource type contracts** — what a workload may declare | `src/jd.core/types/{computing,database,observability}.yml` → `ResourceTypeCatalog` |
| **Workload document** | `src/jd.core/workload/` — a uniform named `resources:` list, no special `container:` block |
| **Pulumi type translations** — how this backend realises each type | `src/backend-providers/jd.bp.pulumi/types/` → `BackendTypeCatalog` |
| **Resolver** — workload + contracts + translations → ordered plan | `jd.bp.pulumi/planning/WorkloadResolver` |
| **Executor** — runs the plan, resolves deferred tokens | `jd.bp.pulumi/execution/WorkloadExecutor` |
| **Pulumi definitions** | 7 under `samples/provisioner/` |
| **Sample driver** | `samples/provisioner/Program.cs` — `--plan`, `--preview`, or provision |

100 tests. `dotnet run -- --plan` resolves and prints the plan without Azure or Pulumi.

Both layers' YAML ships to **namespaced** output folders (`resource-types/`, `backend-types/`). A flat copy had Pulumi's `database.yml` silently overwrite core's.

An earlier YAML mapping-layer approach lives on the `mappings-sample-approach` branch, and the `container-registry` definition went with it.

---

## 1. Decisions still open

| Decision | Why it matters |
|---|---|
| **Is `environment` a workload field or a deploy-time argument?** | Currently required in `metadata`, which contradicts the strategy doc's "same workload definition deploys to dev/staging/prod". Decides whether promotion means editing a file or passing a flag. |
| **Does the plan need to carry a resolution chain?** | `WorkloadPlan` records what to deploy, not what each layer contributed. Strategy doc §5 wants that shown before provisioning, after, and in the audit record — and it cannot be reconstructed afterwards without re-resolving. |
| **What replaces `overrides` in the new workload shape?** | All three core types declare `overridable` properties, but the old `requires[].overrides` + `override_reason` mechanism has no equivalent now that properties are set directly. Nothing reads `overridable`, and there is no production approval gate. |
| **Where do policy-attached resources really live?** | `policy_attached` sits in the backend's `_shared.yml` because nothing else has a home. It is platform policy, not a backend concern. |
| **Where do definitions live?** | `definitions_root: samples/provisioner` — a samples folder doing production duty. |
| **Does `standard` need to differ by environment?** | Per-environment mapping was removed, so `standard` is F1/Free and free-tier Cosmos *everywhere*. Fine for dev-only MVP; F1 cannot do `alwaysOn` and the Cosmos free tier is one per subscription, so it is not production-viable. |

**Settled since the last revision**, recorded so they are not reopened by accident: `computing` replaced the `container:` block; resources are named; stack naming is `{workload}-{env}-{resource}-{deployment}`; `Version` comes from the runtime image tag; deployment keys are scoped per resource instance; class is the only variation axis; definition loading goes through `IDefinitionStore`.

---

## 2. Blocking gaps

**The container runtime decision is still the root blocker.** `app-service/Pulumi.yaml` has **no image parameter and no `appSettings` parameter**, so `computing.image` and `computing.variables` are both in `unmapped`. The resolver computes `COSMOS_ENDPOINT` correctly and the executor publishes `primary.endpoint` as a real value — and there is nowhere to put it. This provisions working infrastructure that does not run the team's container and never receives its configuration.

Two exits, unchanged:

1. Teach `app-service` to run a Linux container — `webAppKind: app,linux,container`, `linuxFxVersion: DOCKER|<image>`, plus `appSettings`.
2. Add a Container Apps definition and repoint `computing`.

Either needs a **container registry definition**, currently only on the other branch. ACR has no free tier — Basic is ~$5/month, so the stack stops being $0.

| Gap | Effect |
|---|---|
| No CLI | `platform deploy workload.yaml` is MVP criterion 1. `Program.cs` is a sample driver, not a command. |
| Observability connection string cannot reach the container | `observability` is policy-attached so a workload cannot reference it, and nothing injects it. The executor *does* publish it — it just has no destination. |
| Sample app cannot demonstrate the MVP | `samples/web` is Blazor Server with no `/health` and no database endpoint; `MVP_01.md` specifies a Web API with both. `computing.healthCheckPath` defaults to `/health` against an app with no such route. |

---

## 3. Inconsistencies in the repo right now

**`schemas/workload.schema.json` describes the old shape.** It still declares `container` and `requires` with a `type` enum of `database, cache, queue, storage, secrets, cdn` — none of which matches the current document. Nothing validates workloads against it today, so a team submitting a valid workload would be rejected and an invalid one accepted. It should be **generated from the catalog** rather than hand-maintained.

**Two definitions of compute.** `definitions/compute.yml` declares `cpu`, `memory`, `replicas`, `network_access`; `src/jd.core/types/computing.yml` declares `image`, `variables`, `ports`, `class`, `healthCheckPath`. One should go — and the former's parameters are the argument for Container Apps over App Service.

**`database`'s classes make a promise the backend does not keep.** Core offers `standard`, `high-availability`, `high-performance`; the translation differentiates them only by losing the free tier, because `cosmos-db/Pulumi.yaml` exposes no throughput, zone-redundancy or backup parameters. So the premium classes are *more expensive without being more available*. The coherence checks pass — each class has a branch, so only the semantics are wrong.

| Topic | The conflict |
|---|---|
| **Pulumi state backend** | `MVP_01.md` says local `.pulumi/`; `decisions-and-approach.md` says Azure Blob Storage; the code uses `file://~`, which is neither. |
| **Database engine** | `MVP_01.md` names PostgreSQL in three places. The code provisions Cosmos DB, chosen because Postgres Flexible Server has no permanent free tier. |
| **One definition, many environments** | `resource-provisioning-strategy.md` §2 says one definition deploys everywhere; `metadata.environment` is required, so it cannot. |
| **Secret injection** | `MVP_01.md`: *"Pulumi sets them on App Service during deployment."* `app-service` has no `appSettings`, so it cannot. |
| **Docs describe the retired shapes** | `MVP_01.md` and `resource-provisioning-strategy.md` both document `container:` + `requires:` with abstract-type requirements. |

---

## 4. Verification

Type contracts moved from C# to YAML, trading compile-time safety for runtime flexibility. These checks buy it back. They are `[Theory]` tests driven by the catalogs, never per-type, so a new `queue.yml` inherits every check with no new test code.

**Built** — 100 tests, and the coherence checks are mutation-verified (each was confirmed to fail when the translation is broken):

- every core type has a translation, and the backend translates nothing core does not declare
- every core property is mapped or explicitly waived under `unmapped`; nothing maps a property core does not declare
- every enum value has a branch; no branch names a value core forbids; non-enum properties are not under `parameter_sets`
- translated outputs are a subset of core's; every non-secret output is produced
- every referenced definition exists; every parameter exists in that definition's `configuration:`
- every `${deployment.*}` names a real deployment and a real output of it; every `role:` alias is in the roles table
- resolution: ordering, scoped keys, defaults, rejections; execution: deferred substitution, identity, role expansion, output publication, fail-fast

**Outstanding:**

| Check | Notes |
|---|---|
| **Class branches must differ from each other** | The §3 gap: three classes, near-identical parameters, structure passes. The one semantic check that *is* automatable. |
| Generate `workload.schema.json` from the catalog | It is currently stale and hand-maintained — see §3. |
| `schemas/resource-type.schema.json`, `schemas/backend-type.schema.json` | Structural validation, plus editor autocomplete via `# yaml-language-server: $schema=…`, which matters now the authoring surface is YAML. |
| Secret outputs are never written in the clear | `Program.cs` masks them on display; nothing stops one reaching plain app settings once injection exists. |

Two things stay in code because JSON Schema cannot express them: `default` ∈ `values` (no way to compare an instance value against a sibling array — Ajv's `$data` is non-standard and NJsonSchema will not honour it), and filename ↔ declared name.

**Azure pre-flight rules.** Each caused a real failure; each is statically checkable before Pulumi is invoked. This narrows the problem permanently but can never close it — it is a record of failures already suffered, not an enumerable set.

| Rule | Caught |
|---|---|
| Log Analytics `skuName` must be `PerGB2018` | the retired `Free` tier, ARM 400 after ~80s |
| `minTlsVersion` must serialise as a string | `should be of type 'string' but got a number` |
| ACR name `^[a-zA-Z0-9]{5,50}$`, no hyphens | a registry named like every other resource |
| Cosmos account name ≤ 44 chars | generated names overflowing |
| One free-tier Cosmos account per subscription | a second workload deploying to `dev` |
| Resource type tokens exist in the provider schema | `azure-native:insights:Component`, `azure-native:documentdb:DatabaseAccount` |

---

## 5. Implementation not built

| Component | Notes |
|---|---|
| **CLI** | MVP criterion 1. |
| **Global policy layer** | Step 3 of the resolution chain has no artifacts. The `inject:` half is what would deliver the observability connection string. |
| **Override application** | See §1 — the mechanism itself needs redesigning for the new shape. |
| **Resource tags** | `enforce-compliance-tags` specifies cost-center, compliance, data-classification. **No definition applies any tag.** Undermines cost attribution and is far cheaper before resources exist than backfilled. |
| **`DestroyAsync` / reconciliation** | `IBackEndProvider` has no destroy. Removing a resource from a workload orphans a stack and leaves live resources billing. |
| **Health verification** | MVP criterion 2 requires it. Nothing checks. |
| **Secret handling** | Outputs declare `secret: true` and the sample masks them; nothing routes them to Key Vault. `ConfigEntry.IsSecret` is never set. |
| **Manual cleanup docs** | The executor now fails fast and reports what was skipped, but `MVP_01.md` promises documented cleanup that does not exist. |
| **Hoisting the resolver** | Its algorithm — expand, substitute, order — is backend-neutral; only its inputs are Pulumi-shaped. Worth moving when a second backend arrives rather than duplicating. |

---

## 6. Deferred by choice

- **Networking.** Everything is public — `publicNetworkAccess: Enabled`, no VNet integration, no private endpoints — despite the landing zone defining VNets and subnets.
- **Landing zone integration.** `definitions/landing-zone-definition.*` has no Pulumi definitions behind it.
- **Remaining resource types.** No cache, queue, storage, secrets, cdn. The resolver already rejects them with a clear message.
- **Region.** Single region, no DR.
- Approvals, autoscaling, rollback, secret rotation, cost quotas, GUI — per `MVP_01.md`'s out-of-scope list.

---

## 7. Code rough edges

- `PulumiBackendOptions.ScratchDireisctory` — misspelled public property, already referenced.
- `ConfigEntry` lives in `src/jd.core/bp/ConfigValue.cs` — filename does not match the type, and the class sits in the **global namespace** while everything around it is `jd.core.bp`.
- **Two computing instances would make `${identity.principalId}` ambiguous.** An api and a worker would have separate identities, both needing grants. The executor takes the first principal id it sees. Fine with one runtime; wrong with two.
- **Generated name length.** A long workload name plus a prefix and a resource name can exceed Cosmos's 44-char limit. Either constrain names or hash-truncate in the resolver.
- **Fixed `roleAssignmentId` GUIDs** in `cosmos-db-access` and `role-assignment` — holds only while each workload gets its own account or scope.
- **`jd.bp.pulumi` has no tests of its own provider** — `PulumiBackendProvider`'s output formatting and null handling are still untested. The new tests cover planning and execution, which use a fake backend.

---

## What tests can never cover

- **Unknown cloud constraints.** The rules in §4 are failures already suffered, not an enumerable set. ARM is the final authority.
- **Whether a class promise holds.** A test can assert `high-availability` sets `zoneRedundant: true`; it cannot assert that delivers what the class claims. §3 is the live example.
- **Whether the abstraction has gone leaky.** If `database` accretes Cosmos-shaped properties, no test notices — the signal is a property only one backend can honour, and a human sees that in review. The most likely failure mode and the least automatable.
- **Whether a design decision was right.** A test pins what was chosen, not whether it was correct.
