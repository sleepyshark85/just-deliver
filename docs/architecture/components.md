# Catalog by component

> Status: design note for S21, **phase 1: components, base** ([roadmap](../plans/roadmap.md); serves N1, N3, N5). Not built. Direction:
> [context.md](../context.md) § "Catalog by component". Refines [resolver.md](resolver.md), [provisioning.md](provisioning.md) (Substrate) and
> [approvals.md](approvals.md) §1. Open choices for the user: [section 8](#8-open-choices).

## 1. Problem

The knowledge about one component is spread across file kinds and directories. For `database` it sits in a type (`types/database.yaml`), a
mapping (`mappings/database/standard.yaml`), two templates, part of the env-substrate mapping (the Cosmos SQL database), part of the
shared-substrate mapping (the Cosmos account), a role line and two naming rules. The dependency is implicit: the substrate decides on its
own that `env.cosmos.*` exists, and the database mapping assumes those names. Nothing at load ties `${env.cosmos.accountId}` to the
resource that produces it. An environment cannot say "no database here". A typo in an `env.*` path is found only when a workload resolves
against a real descriptor.

## 2. The model

### File kinds

`kind` stays on every file, and the loader still reads every `*.yaml` outside `templates/`. Three kinds replace `ResourceType` and
`Mapping`. `Policy`, `Naming`, `Roles`, `Catalog` and the templates stay, shared on purpose.

| Kind | Fields | Replaces |
|---|---|---|
| `SharedResource` | `name`, `description`, `scope` (`shared` \| `environment`), `uses`, `nodes`, `exports`, `grantable` (export names) | the substrate mappings' nodes, and the environment's `grantable` |
| `Component` | `name`, `description`, `exports` (the contract), `uses`, `environment` (optional: `nodes`, `exports`), `classes` (class → `nodes`, `exports`) | `ResourceType` + its requirement mappings + its slice of the substrate |
| `Runtime` | `name`, `description`, `uses`, `probe`, `release`, `nodes` (must include `runtime`) | the runtime mapping (`match: { kind: runtime }`) |
| `Policy` | unchanged, plus `uses` | — |

A **part** is a set of nodes with one scope: a shared resource, a component's `environment` part, a component's class part, the runtime, or a
policy's `add`. Node format (`template`, `kind: create | grant`, `config`), expressions, `name()`, `guid()` and `fn::entries` do not change.

**Names.** Shared resource, component and runtime names share one namespace and use the requirement-type pattern (`^[a-z][a-z0-9-]{1,14}[a-z0-9]$`).
A name may not be `env`, `role`, `workload`, `resource` or `runtime` (the reserved roots).

### Scopes

| Scope | Deployed by | Once per | Parts |
|---|---|---|---|
| shared | `jd env up` of a `layer: shared` definition (platform identity) | shared layer (today: per subscription) | `SharedResource` with `scope: shared` |
| environment | `jd env up` of an environment definition (platform identity) | environment | `SharedResource` with `scope: environment`; a component's `environment` part |
| workload | `jd deploy` / `release deploy` / `promote` | workload | a component's class part (per requirement); the runtime; workload-scope policy `add` |

`uses` lists shared resources only. Allowed: shared → shared; environment → shared, environment; component, runtime, policy → any. The
`uses` graph has no cycles. A workload deploy never expands a shared resource or an `environment` part (section 3).

### Reading another part: `${<name>.<export>}`

A part reads a shared resource as `${cosmos-account.endpoint}`, never as `env.*`. In a component's class part, the component's own
`environment` part is read by the component's name (`${database.databaseName}`). Every other root is unchanged.

| Root in a part | Allowed when | Resolves to |
|---|---|---|
| `<node>.<output>` | a node of the same part | pending node output (as today) |
| `<name>.<export>` | `<name>` is in the part's `uses`, or is the part's own component (class parts only) | a pending reference when that part is deployed **in the same run** (edges to the nodes its export waits on); otherwise the descriptor value `values.<name>.<export>` |
| `env.name`, `env.region`, `env.tier` | always | the descriptor's top-level fields |
| `env.<anything else>` | **never** (an error); otherwise `env.*` would read past `uses` and `grantable` | — |
| `role.*`, `workload.*`, `runtime.*`, `resource.*` | as today | as today |

**Checked at load (new):** every root in a part's config and exports is a node of that part, a name in its `uses`, its own component, or a
reserved root. `<export>` is declared in that part's `exports`. Unknown names, undeclared exports and missing `uses` are load errors at the
file and location. Today these errors surface only at resolve. A node name may not equal a name the part can read as a root.

### Example: `database`

```yaml
# catalog/components/database.yaml
kind: Component
name: database
description: A database for the workload; the engine (today a container in the environment's Cosmos SQL database) is the platform's choice, and `engine` says which.
exports: [engine, endpoint, database, container]     # the contract: every class exports exactly these
uses: [cosmos-account]
environment:                                         # once per environment that offers database
  nodes:
    database:
      template: azure/cosmos-sql-database
      config:
        resourceGroupName: ${cosmos-account.resourceGroup}
        accountName: ${cosmos-account.name}
        databaseName: ${name('db')}
        throughput: 400                              # shared by the environment's containers; two environments use 800 of the free 1000
  exports:
    databaseName: ${database.databaseName}
    databaseId: ${database.databaseId}
classes:
  standard:                                          # what each requiring workload gets
    nodes:
      container:
        template: azure/cosmos-sql-container
        config:
          accountId: ${cosmos-account.id}
          databaseName: ${database.databaseName}     # this component's environment part
          containerName: ${name('cosmos-container')}
          partitionKeyPath: /id
      access:
        kind: grant
        template: azure/cosmos-sql-role-assignment
        config:
          accountId: ${cosmos-account.id}            # grantable: cosmos-account lists `id`
          scope: ${container.scope}
          principalId: ${runtime.principalId}
          roleGuid: ${role.cosmos-data-contributor}
          roleAssignmentId: ${guid(container.scope, runtime.principalId, 'data-contributor')}
    exports:
      engine: cosmos-sql
      endpoint: ${cosmos-account.endpoint}
      database: ${database.databaseName}
      container: ${container.containerName}
```

### Shared resources (the seed catalog)

| Name | Scope | Uses | Node (unchanged name) | Exports | Grantable |
|---|---|---|---|---|---|
| `shared-group` | shared | — | `group` (resource-group) | `name`, `location` | — |
| `log-analytics` | shared | `shared-group` | `workspace` | `id`, `name`, `customerId`, `resourceGroup` | — |
| `cosmos-account` | shared | `shared-group` | `cosmos` | `id`, `name`, `endpoint`, `resourceGroup` | `id` |
| `environment-group` | environment | — | `group` (resource-group) | `name`, `location` | — |
| `container-apps-environment` | environment | `environment-group`, `log-analytics` | `apps` | `id`, `defaultDomain` | — |

```yaml
# catalog/shared/cosmos-account.yaml
kind: SharedResource
name: cosmos-account
description: The one free-tier Cosmos DB account (Azure allows one per subscription).
scope: shared
uses: [shared-group]
nodes:
  cosmos:
    template: azure/cosmos-account
    config:
      resourceGroupName: ${shared-group.name}
      location: ${shared-group.location}
      accountName: ${name('cosmos')}
      enableFreeTier: true
      totalThroughputLimit: 1000
      disableLocalAuth: true
exports: { id: ${cosmos.accountId}, name: ${cosmos.accountName}, endpoint: ${cosmos.documentEndpoint}, resourceGroup: ${shared-group.name} }
grantable: [id]
```

### The runtime

The runtime is **not** a `Component`. It has no contract, no classes, and is never required. A workload names no runtime. The environment
offers exactly one, and every workload deployed there gets it. It keeps its own kind so its checks stay in one place: the `probe`, the
`release` block, the reserved `runtime` node and the inputs the release flow owns. Those checks are today's, moved from the runtime mapping.
`MappingMatcher` goes. With offers, nothing is left to match (choice 3).

```yaml
# catalog/runtimes/container-app.yaml
kind: Runtime
name: container-app
uses: [environment-group, container-apps-environment]
probe: { ... }        # unchanged
release: { ... }      # unchanged
nodes:
  runtime:
    template: azure/container-app
    config:
      resourceGroupName: ${environment-group.name}
      location: ${env.region}
      containerAppsEnvironmentId: ${container-apps-environment.id}
      # the rest unchanged
```

`enforce-monitoring` gains `uses: [environment-group, log-analytics]` and reads `${environment-group.name}` and `${log-analytics.id}`.

### Catalog layout

```
catalog/
  catalog.yaml  naming.yaml  roles.yaml                  # shared, unchanged
  components/database.yaml                               # contract + environment part + classes
  runtimes/container-app.yaml
  shared/{shared-group,log-analytics,cosmos-account,environment-group,container-apps-environment}.yaml
  policies/enforce-monitoring.yaml                       # + uses
  templates/azure/*/Pulumi.yaml                          # unchanged, generic, shared
```

Deleted: `types/`, `mappings/`. The layout stays organisational only: the loader goes by `kind`.

## 3. Scopes and who deploys

### Environment definitions: layer and offers

```yaml
# samples/environments/shared.yaml
kind: EnvironmentDefinition
name: shared
layer: shared                 # deploys the shared scope; its descriptor is the --base of environments
tier: team
offers: [container-app, database]

# samples/environments/stage.yaml (dev.yaml is the same without approval and with tier: team)
kind: EnvironmentDefinition
name: stage
tier: protected               # layer defaults to environment
offers: [container-app, database]
approval: { ... }             # unchanged
```

`requires`, `values` and `grantable` leave the definition. `jd env up` keeps its command line (`--base`, `--region`, `--out`).

| `jd env up` of | Collects (one graph, owner `@<name>`, scope `substrate`) | Must already exist |
|---|---|---|
| `layer: shared` | the `scope: shared` resources reached through `uses` from the offered components and runtime, and from every policy | — |
| `layer: environment` | the offered components' `environment` parts, and the `scope: environment` resources reached through `uses` from them, the offered runtime and every policy | every `scope: shared` resource reached the same way, present in the base descriptor; otherwise a load error before anything is created, naming the resource and the base |

- **The substrate follows from the offers.** No offered component uses `cosmos-account`, so no Cosmos account. Policies apply to every
  workload, so what they use is always collected.
- **One scope, today's ids.** The parts of one run expand into one scope, named `substrate` (an engine constant like `@workload`). Each
  node is evaluated with `substrate` as its current id. Ids, stacks and `name()` hashes therefore stay exactly as today:
  `@dev/dev/substrate/apps`, `_shared.shared.substrate.cosmos`, `cosmos-shared-39d0b1`. The node names of all parts of one scope must be
  unique across the catalog: `group`, `workspace` and `cosmos` (shared); `group`, `apps` and `database` (environment). This is a load check.
- At most one runtime among an environment's offers. Offers name declared components or runtimes.

### The descriptor

`jd env up` writes it as today, but `values` is no longer a hand-written layout. It is **every collected part's exports**, evaluated with
the deployed outputs and keyed by part name, merged into the base's values (new keys only, as today).

```yaml
kind: Environment
name: dev
region: southeastasia
tier: team
offers: [container-app, database]        # a shared layer's descriptor offers nothing, so no workload can deploy on it
values:
  shared-group:      { name: rg-shared-5d9b60, location: southeastasia }
  log-analytics:     { id: …, name: …, customerId: …, resourceGroup: rg-shared-5d9b60 }
  cosmos-account:    { id: …, name: cosmos-shared-39d0b1, endpoint: …, resourceGroup: rg-shared-5d9b60 }
  environment-group: { name: rg-dev-…, location: southeastasia }
  container-apps-environment: { id: …, defaultDomain: … }
  database:          { databaseName: db-dev, databaseId: … }
approval: { … }                          # stage only, still the last block
```

`grantable` leaves the descriptor. The grant check now needs only the catalog (below).

### Workload resolution

- A requirement `type: database` selects the component `database`, which must be among the descriptor's `offers` (new error: "environment
  dev does not offer database"). Its `class` selects `classes.<class>` (an unknown class is an error naming the classes). No matching.
- The runtime is the descriptor's offered runtime. If there is none, resolution fails: "environment X offers no runtime".
- Workload parts read shared resources and their component's environment part from the descriptor. They are resolved at expansion, so
  preview shows them as `env.*` values were shown before.
- Workload definitions do not change: `requires[].type` names the component, and `${resource.<id>.<export>}` reads its contract.

### Security boundaries (D19) that move

| # | Boundary | Today | After | Enforced |
|---|---|---|---|---|
| 1 | What a grant may target | `env.<path>` in the environment's `grantable` (`cosmos.accountId`) | `${X.e}` where X is in the part's `uses` **and** `e` is in X's `grantable` (`cosmos-account.id`): the same set for the seed catalog, narrowed by `uses` | load (catalog nodes and policy `add`); graph build (policy `set`/`default` on a grant, as today) |
| 2 | What any node may read from the environment | any `env.*` value | only its `uses`, its own component's environment part, and `env.name/region/tier` | load |
| 3 | Bypass through `env.*` | — | `env.<path>` other than name/region/tier is an error, so `${env.cosmos-account.id}` cannot skip 1 and 2 | evaluator |
| 4 | A class part reading another component's environment part | possible (any `env.*`) | an error: only its own component's name is a root | load |
| 5 | Workload input crossing into the platform | variables take only `${resource.<id>.<export>}` | unchanged; `${cosmos-account.id}` in a variable is rejected like any other `${…}` | `WorkloadRules` |
| 6 | A workload deploy creating shared or environment resources | substrate types resolved only by `ResolveSubstrate` | a workload graph expands only class parts, the runtime and workload-scope policy `add`; never a `SharedResource` or an `environment` part | expander |
| 7 | An environment `env up` creating shared resources | possible (any mapping) | an environment layer never collects `scope: shared` (it checks the base has them); only `layer: shared` deploys them, so each shared stack has one owner (ADR 0009 split; no race) | resolver |
| 8 | Requirement scopes reading each other; only the runtime reads exports | — | unchanged | graph builder |
| 9 | Owner prefix `@<name>` keeps environment and workload ids, stacks and hashes apart | — | unchanged | expander |
| 10 | Deploying a component the environment does not offer | resolves, then fails on a missing `env.*` | refused at resolve | resolver |

Every row is a required test in the slice that moves it. Rows 1–4 and 6–7 each need a negative test for the forbidden path.

### How the later phases stay open (roadmap rule 4)

| Later idea | What phase 1 leaves open | What phase 1 does not do |
|---|---|---|
| N1/N2 workloads as components (phase 3) | One shape with named `exports` and `uses`, per-scope parts and a reserved runtime. A workload can later declare a contract with the same `exports` and use the runtime as its compute part | no contract version, no workload `kind` change |
| N5 offers | `grantable` is declared by the **provider**, per export: the seed of "access by offer". Phase 3 can replace it with offered access levels without moving the check | no access levels, no consumer lists |
| N4 bind/attach (phases 4–5) | requirement → part lookup lives in one place (the expander); "create" is today's only mode. Bind resolves against deployed exports instead of instantiating a class part | no `mode` field |
| N6 deployed-exports registry | the descriptor's `values` are already "deployed exports keyed `<name>.<export>`", read by one root syntax. A registry can add workload entries with the same key form | no registry, no workload entries |
| Name lookup | `requires[].type` resolves a name in the catalog namespace; phase 4 can extend the lookup to workload names (a collision rule is phase 4's) | — |

## 4. What changes in the code

| Project / file | Change |
|---|---|
| `schemas/catalog/` | + `component`, `sharedresource`, `runtime` schemas; `policy` + `uses`; − `resourcetype`, `mapping` |
| `jd.resolver/catalog/Catalog.cs` | `Catalog(Version, Components, SharedResources, Runtimes, Policies, Naming, Roles)`; records `Component`, `SharedResource`, `Runtime`, `Part(Nodes, Exports)`; `Policy.Uses`; − `ResourceType`, `Mapping`, `MatchCriteria.TypeKey/ClassKey` uses in mappings (policies keep `MatchCriteria`) |
| `jd.resolver/catalog/CatalogParser.cs` | new kinds; checks: unique names and reserved roots, `uses` exist, scope rules, no cycles, node names unique per scope, roots and exports at load, grantable at load, class exports equal the contract; the runtime checks move to `Runtime`; − mapping/type checks |
| `jd.resolver/matching/MappingMatcher.cs` | **deleted** |
| `jd.resolver/expressions/ExpressionEvaluator.cs`, `ExpressionModel.cs` | new reference kind `Part` (`<name>.<export>`), resolved from the descriptor or pending in-run; `env.*` limited to name/region/tier; reports part reads for the grant check (replaces `EnvPaths`) |
| `jd.resolver/expansion/Expander.cs` | requirement → `component.classes[class]`; runtime from offers; `ExpandEnvironment(definition, base)` collects parts by layer into scope `substrate` |
| `jd.resolver/graph/GraphBuilder.cs`, `ExportValues.cs` | edges for in-run part references (the export's node references, like `${resource.…}` today); grant check against the catalog's `grantable`; exports keyed by part name for environment owners |
| `jd.resolver/environment/*` | definition: `layer`, `offers`; − `requires`, `values`, `grantable`. Descriptor: `offers`; − `grantable`. `DescriptorComposer`: values from part exports (− the `values` expression walk). `EnvironmentParser`: − the grantable rule. Schemas `environment*.schema.json` follow |
| `jd.orchestrator/Orchestrator.cs` | second pass: known `Part` references from `ExportValues` (as `Resource` today) |
| `jd.cli/EnvCli.cs` | wiring only (the definition's new shape) |
| `jd.bp.pulumi` (`TemplateLibrary.Check`) | unchanged: it checks graph nodes against templates |
| tests | catalog fixtures (`TestCatalog`, Azure tests' test-only types and mappings) rewritten to the new kinds |

## 5. Behaviour preservation (the refactor's acceptance)

The same resources on Azure for the same inputs.

| Must be equal before and after | May differ |
|---|---|
| per node: `id`, `scope`, `name`, `template`, `kind`, `stack`, `phase`, `dependsOn`, resolved config values; resource names (`name()` hashes) | `provenance` (file paths and rule names: `Mapping: mappings/database/standard.yaml` → the component file) |
| what the orchestrator hands the backend per stack (stack name, every config entry and its secret flag) after the second pass | substrate nodes' pending text (`${group.resourceGroupName}` → `${shared-group.name}`), and so those nodes' `hash` |
| workload graphs' node `hash` (all their environment reads resolve at expansion) | a substrate graph's `exports` section (keyed by part, not `substrate`); the descriptor's layout (values by part, `offers`, no `grantable`) |

Proof:
- **`DeployedConfigSnapshotTests` (new, first slice, test-only).** This runs `env up` shared → dev → stage and `jd deploy` of the seed
  workload with a fake backend whose outputs are deterministic (`<stack>/<output>`). It snapshots every package (stack, config entries,
  secret flags). The snapshot is committed before any production change and must stay **byte-identical** through every slice.
- **Golden graphs** (`GoldenGraphTests`, `GoldenSubstrateTests`, `seed-workload`/`shared`/`dev`/`stage-environment.graph.json`). Each slice's
  diff may touch only the "may differ" items above; the reviewer checks it line by line. The fixture descriptors
  (`seed-environment.yaml`, `shared-`/`stage-environment.yaml`) are rewritten to the new layout with the same values.
- **Azure:** `EnvUpAzureTests` and the E2E test on an existing environment. A re-run of `env up` and `deploy` after the change reports every
  node `unchanged`.

Release records written before the change carry the old `graph.sha256` (provenance is in it). Nothing compares records across that line:
qualification and promotion compare `set.yaml` and records, not graph hashes.

## 6. Migration

| What | Change |
|---|---|
| `catalog/types/*`, `catalog/mappings/*` | deleted; content moved to `components/database.yaml`, `runtimes/container-app.yaml`, `shared/*.yaml` (section 2) |
| `catalog/policies/enforce-monitoring.yaml` | + `uses`; `${env.resourceGroup}` → `${environment-group.name}`, `${env.logAnalytics.id}` → `${log-analytics.id}` |
| `samples/environments/{shared,dev,stage}.yaml` | `layer`/`offers` replace `requires`/`values`/`grantable`; `approval` unchanged |
| descriptors already written (`$JD_WALK/*.env.yaml`) | invalid in the new layout: re-run the three `jd env up --force`; every node `unchanged`, nothing changes on Azure |
| `docs/walkthrough.md` | the `env up` commands are unchanged; `DEV_RG`/`STAGE_RG` read `environment-group.name` instead of `resourceGroup`; the catalog row in "Architecture" says components, shared resources, runtimes; a note to re-run `env up` |
| `samples/walkthrough/teardown.sh` | reads `values.environment-group.name` and `values.shared-group.name` instead of `values.resourceGroup` and `values.shared.resourceGroup` |
| `samples/walkthrough/stage-policy.sh` | unchanged (`approval` stays the last block) |
| `samples/walkthrough/policies/limit-revisions.yaml` | unchanged (a `kind: runtime` policy reading no environment value) |
| workload definitions, release manifests, records | unchanged |
| docs | `resolver.md` (catalog layout and kinds, expressions, engine rules, descriptor), `provisioning.md` (Substrate), `approvals.md` §1 (the `env-substrate/standard` sentence), `usage.md`, `walkthrough.md`: each slice updates what it changes |

## 7. Slices

Each slice leaves `tools/verify.sh` green, the snapshot of section 5 byte-identical and the Azure E2E test passing. Line counts are production
lines (C#, schemas), estimated.

| Slice | Content | Lines | Azure gate | Acceptance |
|---|---|---|---|---|
| **S21a** Components and runtimes | `DeployedConfigSnapshotTests` first, in its own commit. `Component` and `Runtime` replace `ResourceType` + `Mapping`; `MappingMatcher` deleted. During this slice only: the substrate types become two transitional components (`env-substrate`, `shared-substrate`), still required by the environment definitions, and the runtime is the catalog's only `Runtime` | ~200 | no | goldens differ only in provenance; old kinds rejected with a clear error; catalog checks moved, each with its test |
| **S21b** Shared resources and `uses` | `SharedResource` kind; component `environment` part; `uses` on all parts and policies; every load check of section 2 (roots, exports, scope rules, cycles, node-name uniqueness, grantable). The catalog gains `shared/*.yaml` and `database.environment`, loaded and checked, consumed in S21c | ~200 | no | one negative test per load check; seed catalog loads |
| **S21c** Environments from offers | definition `layer`/`offers`; `env up` collects parts per layer into scope `substrate`; in-run `<name>.<export>` references (edges, second pass); descriptor values from part exports, `offers`. Transitional `grantable` derived from the resources. Transitional components deleted. Workload parts read the new layout through `${env.cosmos-account.id}` until S21d. Walkthrough, teardown and samples migrated | ~250 | **yes** (`env up` shared/dev/stage) | substrate goldens equal per section 5; boundary 7 tested; re-run `env up` on Azure: all `unchanged` |
| **S21d** Workloads through `uses` | parts, runtime and policies read `${<name>.<export>}`; `env.*` reserved only; grant check from the catalog (load + graph); descriptor `grantable` removed; offers check and runtime from offers | ~150 | **yes** (E2E) | boundaries 1–6, 8, 10 tested; workload golden hashes unchanged; E2E green on existing environments |

Total about 800 production lines added, with deletions offsetting part of it (the matcher, the type/mapping checks, the definition `values` walk).

## 8. Open choices

1. **How the shared scope is deployed.** (a) **Recommended:** a `layer: shared` definition deploys it once, and environments read it as
   `--base`, as today. Every shared stack has one owner, the region is fixed by that one run, and today's commands stay. (b) Each
   environment's `env up` deploys the shared resources its offers use. One fewer file, but several environments then co-own the shared
   stacks: concurrent runs race, a different `--region` would move (replace) the Cosmos account, and nothing owns the shared stacks when an
   environment is removed. This departs from the direction's "env up collects the shared and environment parts": with (a), each layer
   collects its own scope.
2. **Values on an offer** (the direction's "with their values"). (a) **Recommended:** offers are names only in phase 1. Nothing varies per
   environment today (dev and stage are identical), and the first component that needs a per-environment value adds it. (b) Now: a component's
   `environment` part declares `settings` with defaults (`throughput: 400`), an offer may override them (`database: { throughput: 400 }`),
   and the part reads `${settings.<name>}`. About 60 lines and one more slice.
3. **Per-tier variants.** Mappings could match `tier`. No seed mapping does, and `approvals.md` §1 relies on that. (a) **Recommended:** drop
   tier matching for components and runtimes. A tier difference is a different component or runtime offered by that environment, or a
   policy (policies keep `tier`). (b) Keep tier variants inside a class part. That keeps the matcher and its tie rules.
4. **Grantable from `uses`.** (a) **Recommended:** a grant may target `X.e` only if X is in `uses` **and** X lists `e` as `grantable`. That is
   as strict as today. (b) Any export of a used resource, which is the direction's wording taken literally. This is weaker: a database grant
   could then target `${database.databaseId}`, the environment's whole database, and reach every workload's container.

## 9. ADRs affected

| ADR | Effect |
|---|---|
| [0003](../decisions/0003-pulumi-yaml-template-library.md) | holds: static templates, one stack per node, config-passing. Templates stay shared, not per component |
| [0004](../decisions/0004-two-layer-definition-model.md) | refines the platform layer: "mapping" becomes the component's class part; policies unchanged (+ `uses`) |
| [0008](../decisions/0008-enforcement-in-landing-zone.md) | holds: platform-owned shared and environment scopes are deployed only by `env up` |
| [0009](../decisions/0009-rbac-split-team-subscription.md) | holds and is made structural: scopes map to who deploys; a workload deploy never creates a shared resource (boundaries 6, 7) |
| [0011](../decisions/0011-requirement-id.md) | holds: `requires[].type` now names a component; `id` and `class` unchanged |
| [0013](../decisions/0013-container-apps-runtime.md) | refines "runtime is a declared property of the workload": the **environment offers** the runtime. The workload still names none, as in the MVP |

No new ADR now. If the user takes choice 1(a) and 3(a), the component model is worth one (`Proposed — pending review`) once S21d lands,
recording kinds, scopes and grantable-by-provider, so resolver.md stops carrying the decision.
