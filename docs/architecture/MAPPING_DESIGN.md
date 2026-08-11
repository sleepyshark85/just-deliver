# Platform Mapping Design

How a workload definition becomes running Azure infrastructure, and why the mapping layer is shaped the way it is.

Related: [Resource Provisioning & Workload Definition Strategy](../investigation/resource-provisioning-strategy.md) (the two-layer model this implements), [Pulumi Notes](PULUMI_NOTES.md).

---

## 1. Where the mapping sits

```
workload.yaml                 team declares what it needs
     │                        validated against schemas/workload.schema.json
     ▼
definitions/mappings/*.yml    platform decides how, per resource type
     │                        + global policies (not yet implemented)
     ▼
samples/provisioner/*/        Pulumi definitions - the actual IaC
     │
     ▼
PulumiBackendProvider         materialises, configures, deploys
```

The strategy doc describes a resolution chain of team definition → platform mapping → global policy → overrides → final config. **The mapping files are step 2.** Global policies (step 3) do not exist as artifacts yet; where policy is currently implied, the mapping hardcodes the outcome and says so in a comment.

A workload never names an Azure service. `requires: - type: database` is the entire team-side expression of "I need somewhere to put data"; that it becomes a Cosmos DB account with a SQL container and a data-plane role assignment is a platform decision recorded in `database.yml`.

---

## 2. File layout

```
definitions/mappings/
  _shared.yml       cross-cutting settings; not a resource type
  foundation.yml    resource group
  monitoring.yml    Log Analytics + App Insights + telemetry grant
  compute.yml       App Service plan + web app + AcrPull grant
  database.yml      Cosmos account + data-plane grant
```

Not every Pulumi definition gets a mapping file. `container-registry` is shared infrastructure provisioned once, and `role-assignment` / `cosmos-db-access` are building blocks other mappings compose — see §4.

One file per resource type. Each owns its `deployments`, its `outputs`, its `override_whitelist`, and the notes that belong to it.

`_shared.yml` holds only what cannot be duplicated without drifting: `definitions_root`, the `environments` table, built-in Azure role IDs, and `policy_attached`.

---

## 3. Anatomy of a mapping file

```yaml
resource_type: database          # must match the filename

deployments:
  - id: cosmos                   # globally unique across the folder
    definition: cosmos-db        # folder under definitions_root
    parameters:                  # bound to that definition's Pulumi config
      accountName: cosmos-${workload.metadata.name}-${env.name}
    environments:                # per-environment overlay on parameters
      production:
        enableFreeTier: false

outputs:                         # what ${resources.database.<key>} resolves to
  endpoint: ${deployment.cosmos.documentEndpoint}

override_whitelist:              # fields a team may override, and only these
  - databaseName
```

### Expression vocabulary

| Form | Resolves to |
|---|---|
| `${workload.<path>}` | a field from the submitted workload definition |
| `${env.<key>}` | a value from `environments` in `_shared.yml` |
| `${deployment.<id>.<output>}` | an output of any deployment, in any mapping file |

A workload's `container.variables` use a fourth form, `${resources.<type>.<key>}`, resolved through the type's `outputs:` block. Syntax matches [SCORE](https://docs.score.dev/docs/score-specification/score-spec-reference/), which keeps the door open to adopting it for the container runtime layer.

**A workload may only reference types listed in its own `requires`.** Policy-attached types (`foundation`, `monitoring`) and implicit ones (`compute`) are not referenceable, even though they have `outputs:` blocks. This makes two things checkable at submission with no Azure call: a reference to an undeclared type, and a reference to a key the type does not expose.

It also means policy-attached outputs have to reach a container by *injection* rather than reference — see the App Insights note in §6.

Anything without `${}` is a literal. The vocabulary is deliberately tiny — no conditionals, no functions, no arithmetic. Branching belongs in the orchestrator, for the same reason Pulumi YAML has no conditionals: a config format that grows expressions becomes a bad programming language.

---

## 4. Design decisions

### One file per resource type, not one mapping file

The alternative was a single `platform-mapping.yml` with every type under one `mappings:` key.

Per-type files mean adding a resource type is adding a file rather than editing a shared one, review diffs stay scoped to the type being changed, and each type's caveats sit next to the thing they describe instead of accumulating in a notes section at the bottom.

**Cost:** deployment IDs became global across files (see below), and a shared settings file is now unavoidable.

### Deployment IDs are global, not file-scoped

`database.yml` refers to `${deployment.app.webAppPrincipalId}`, which is defined in `compute.yml`.

This is not incidental coupling that better structure would remove — it is the real dependency. A Cosmos role assignment genuinely cannot run until the App Service has an identity to grant. Scoping IDs per file would mean inventing an export/import syntax to express the same edge, which is more machinery for no gain.

**Consequence:** a duplicate ID across two files would silently rebind a reference rather than collide visibly. The loader must reject duplicates. This is the one real regression from splitting, and it is called out in `_shared.yml`.

### Order is derived from references, never declared

Deployment order comes from the `${deployment.*}` graph. Nothing in the files states a sequence, and file order is meaningless.

Declared order is a second source of truth that drifts from the real dependencies the moment someone reorders a list. A reference cycle is a mapping error and should be rejected at load.

Current edges:

```
compute    → foundation
monitoring → foundation, compute
database   → foundation, compute
```

### `compute` is implicit, not declared

A workload never writes `requires: - type: compute`. It is implied by the presence of `container`, and treated internally as an implicit requirement so the runtime resolves through the same machinery as everything else — same parameter binding, same output wiring, same ordering.

### Monitoring is policy-attached, not requested

`policy_attached: [foundation, monitoring]` provisions both for every workload regardless of what it declares. Teams cannot request or decline them.

This is what makes the sample workload's `requires: - type: database` complete rather than an omission. It implements `enforce-monitoring` from the strategy doc's global policy set. When real policy artifacts exist, this list should move there.

### Outputs are named by intent, not by Pulumi output name

`${resources.database.endpoint}` maps to `documentEndpoint` on the `cosmos` deployment. The team-facing name is stable across a change of backing service; the Pulumi name is not.

This is the seam that lets `database` become PostgreSQL later without touching a single workload definition — `endpoint` still means "where the database is."

### Outputs expose no credentials

`database.outputs` is endpoint, database name, container name. No key, no connection string.

The workload authenticates with its managed identity, so there is nothing secret to hand to the container. App Insights is the exception: its `connection_string` is exposed because SDKs require it for endpoint routing, and it is an identifier rather than a credential.

### The container registry is shared infrastructure, not a workload resource

`container-registry` is a Pulumi definition but has no mapping file, and a workload cannot write `requires: - type: registry`.

The image named in `workload.container.image` must already exist in the registry before the workload deploys — CI pushes it during the build. A registry provisioned *as part of* deploying a workload could never hold the image that workload is deploying. The ordering makes it impossible in principle, not just awkward.

So the registry is provisioned once as shared infrastructure and referenced by ID from `${env.container_registry_id}`. Only the **AcrPull grant** is workload-scoped, because that depends on the workload's identity — it lives in `compute.yml`.

This split is worth noting as the general pattern for shared infrastructure: the resource itself is platform-owned and pre-existing, while the per-workload access grant flows through the normal mapping machinery.

It also gives `role-assignment` its second real consumer (alongside monitoring's metrics-publisher grant), which is what a generic definition needed to justify existing.

### Cosmos does not use the generic role-assignment definition

`monitoring.yml` grants telemetry access through the reusable `role-assignment` definition. `database.yml` uses a dedicated `cosmos-db-access` definition instead.

Cosmos DB SQL API does not route data-plane access through Azure RBAC — it has a separate role-assignment resource type with its own role definitions. A generic `RoleAssignment` against a Cosmos account creates a resource that grants no data access. This asymmetry is unavoidable, not an inconsistency to tidy up.

### `disableLocalAuth` defaults to off

Enabling it makes the App Insights ingestion endpoint reject instrumentation-key auth. The browser/JavaScript SDK cannot present an Entra token, so client-side telemetry stops arriving with nothing surfaced in the portal.

Defaulting it on would silently break any workload with browser telemetry. It should be enabled per-workload, once a workload is known to be server-side only.

The metrics-publisher role assignment is granted regardless — it is inert while local auth is enabled, so granting it up front costs nothing and removes a step later.

### Environment overlays live with the deployment

`environments:` overlays parameters per environment, inside the deployment it applies to, rather than in a separate per-environment file.

Keeping the dev/staging/production SKU ladder in one place makes it reviewable as a progression. Splitting per environment hides the ladder across files and makes "what changes between staging and production" a diffing exercise.

### `role:` is expanded by the executor

Mapping files reference `role: monitoring-metrics-publisher` and the executor expands it to the full `/subscriptions/{id}/providers/Microsoft.Authorization/roleDefinitions/{guid}` path, taking the subscription from the target scope.

The alternative is putting subscription IDs in the mapping files. Deriving from scope reuses logic the provisioner already has and keeps the files subscription-agnostic.

`role` is not a real Pulumi parameter — it is the one place the mapping is not a direct binding, which is why it is documented rather than inferred.

---

## 5. Verification

References in these files are checkable without deploying: every `definition:` must exist under `definitions_root`, every parameter must exist in that definition's `configuration:` block, every `${deployment.<id>.<output>}` must name a real deployment and a real output of it, every `${workload.*}` path must exist in the schema, every `override_whitelist` entry must be a real parameter, and IDs must be unique.

This has caught real errors — a `healthCheckPath` bound to a workload field that exists in `definitions/compute.yml` but not in the workload schema, for one. **This should become a test rather than an ad-hoc script.** It is the cheapest possible guard against a mapping that only fails halfway through a deployment.

---

## 6. Known gaps

| Gap | Impact | Where noted |
|---|---|---|
| `container.image` has no parameter to bind to | **Blocking.** The mapping describes a workload it cannot deploy. `app-service` provisions a Windows .NET app with no image parameter. | `compute.yml` NOTE 1 |
| `container_registry_id` is a placeholder | The AcrPull grant cannot resolve until real registry IDs are filled in per environment. | `_shared.yml` |
| ACR has no free tier | Basic is roughly $5/month, so the stack is no longer $0 once a registry exists. Everything else (App Service F1, Cosmos free tier, App Insights under 5GB) stays free. | `container-registry/Pulumi.yaml` |
| `definitions/compute.yml` declares `cpu`, `memory`, `replicas` | No App Service equivalent — App Service sells SKU tiers and worker counts, not CPU millicores. Same decision as above. | `compute.yml` NOTE 2 |
| Generated names can exceed Azure limits | `cosmos-${name}-${env}` against a 40-char `metadata.name` breaks the 44-char Cosmos limit. | `_shared.yml` |
| `enableFreeTier: true` in dev | One free-tier Cosmos account per subscription; a second workload in dev fails. Needs per-subscription state, not a per-environment constant. | `database.yml` |
| Fixed `roleAssignmentId` GUIDs | Holds only while each workload gets its own account/scope. Sharing needs generated per-assignment IDs. | `database.yml`, `monitoring.yml` |
| Duplicate deployment IDs rebind silently | Loader must reject them. | `_shared.yml` |
| No mappings for `cache`, `queue`, `storage`, `secrets`, `cdn` | Schema accepts them; they should be rejected at submission, not mid-provisioning. | `_shared.yml` |
| Global policies are not artifacts | Policy outcomes are hardcoded in mappings. Step 3 of the resolution chain does not exist yet. | this doc, §1 |
| App Insights connection string cannot reach the container | `monitoring` is policy-attached, so a workload cannot reference it, and nothing injects it. Telemetry stays unwired until the `inject:` half of `enforce-monitoring` exists. | `monitoring.yml` |
| Overrides are not applied | Whitelists are declared but nothing consumes them, and there is no production approval gate. | strategy doc §6 |

### The blocking one

`container.image` is unmapped, which means no workload can currently be deployed end to end. Two ways out:

1. **Linux container on App Service** — `webAppKind: app,linux,container` plus `linuxFxVersion: DOCKER|<image>`. Smaller change; keeps the existing definition and the SKU ladder.
2. **Container Apps** — a new definition, and `compute.yml` points at it instead. Also resolves the `cpu`/`memory`/`replicas` mismatch, since those map directly.

These are the same decision. Until it is settled, the mapping layer is complete on paper and unusable in practice.
