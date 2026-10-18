# Catalog by component

> Status: design note for S21, **phase 1: components, base** ([roadmap](../plans/roadmap.md); serves N1, N3, N5). S21a built (environment types); S21b built (components and runtimes); S21c built (monitoring, policy `require`); S21d–f not built. Direction:
> [context.md](../context.md) § "Catalog by component". Refines [resolver.md](resolver.md), [provisioning.md](provisioning.md) (Substrate) and
> [approvals.md](approvals.md) §1–2. The user's choices: [section 8](#8-choices-taken-by-the-user-2026-10-18).

## 1. Problem

The knowledge about one component is spread across file kinds and directories. For `database` it sits in a type (`types/database.yaml`), a
mapping (`mappings/database/standard.yaml`), two templates, part of the env-substrate mapping (the Cosmos SQL database), part of the
shared-substrate mapping (the Cosmos account), a role line and two naming rules. The dependency is implicit: the substrate decides on its
own that `env.cosmos.*` exists, and the database mapping assumes those names. Nothing at load ties `${env.cosmos.accountId}` to the
resource that produces it. An environment cannot say "no database here". A typo in an `env.*` path is found only when a workload resolves
against a real descriptor. And a policy adds resources (App Insights) that no workload asked for.

## 2. The model

### File kinds

`kind` stays on every file, and the loader still reads every `*.yaml` outside `templates/`. `ResourceType` and `Mapping` go. `Naming`,
`Roles` and the templates stay, shared on purpose.

| Kind | Fields | Replaces |
|---|---|---|
| `Catalog` | `version`, **`environmentTypes`** (type → optional `approval: true`; S21e adds `layer`, see below) | `tier` enums in the schemas |
| `SharedResource` | `name`, `description`, `scope` (`shared` \| `environment`), `uses`, `nodes`, `exports` | the substrate mappings' nodes |
| `Component` | `name`, `description`, `exports` (the contract), `uses`, `implementations` (environment type or `default` → `environment` part (optional), `classes` (class → `nodes`, `exports`)) | `ResourceType` + its mappings + its slice of the substrate |
| `Runtime` | `name`, `description`, `uses`, `implementations` (environment type or `default` → `probe`, `release`, `nodes`, which must include `runtime`) | the runtime mapping (`match: { kind: runtime }`) |
| `Grants` | `targets` (grant template → its target input) | the environment's `grantable` |
| `Policy` | `match` key `environmentType` replaces `tier`; **+ `require`**; **− `add`** | — |

A **part** is a set of nodes with one scope: a shared resource, an implementation's `environment` part, a class part, or the runtime. Node
format (`template`, `kind: create | grant`, `config`), expressions, `name()`, `guid()` and `fn::entries` do not change.

**Names.** Shared resource, component and runtime names share one namespace and use the requirement-type pattern
(`^[a-z][a-z0-9-]{1,14}[a-z0-9]$`), so the seed names are short: `env-group`, `apps-env`. A name may not be `env`, `role`, `workload`,
`resource` or `runtime` (the reserved roots). Environment types use the same pattern; `default` is not a type (it is the fallback key).

### Environment types

The catalog declares the environment types once; an environment definition or descriptor of any other type is an error (at `env up` and
at resolve). Today's `tier` (`team`/`protected`) becomes the **type**.

```yaml
# catalog/catalog.yaml
kind: Catalog
version: "0.1.0"
environmentTypes:
  shared: {}                                    # S21e: { layer: shared }, the shared layer's own type
  dev:    {}                                    # S21e: { layer: environment }
  stage:  { approval: true }                    # a definition of this type must carry an `approval` block
  prod:   { approval: true }
```

`layer` (`shared` \| `environment`, S21e) says which `jd env up` layer a type is; S21a does not declare or read it.

- **One contract, implementations per type.** A component or runtime is looked up as `implementations.<type>`, else
  `implementations.default`, else an error naming the component and the type. Exact key lookup: no matching, no specificity, no ties. An
  implementation replaces `default` whole (no merging).
- **Load checks:** implementation keys are declared types (S21e: of layer `environment`), or `default`; every implementation declares the same
  classes (so a workload's `class` is valid in every environment); every class part exports exactly the contract.
- `approval: true` replaces the schema coupling `tier: protected` → `approval` ([approvals.md](approvals.md) §2): a definition or descriptor
  of such a type without the block is an error. As today, any type may carry a block, and the CLI gates on the block, never on the type.
- Policies may match the type (`match: { environmentType: prod }`). `env.tier` becomes `env.type`.

### Scopes

| Scope | Deployed by | Once per | Parts |
|---|---|---|---|
| shared | `jd env up` of a definition whose type has `layer: shared` (platform identity) | shared layer (today: per subscription) | `SharedResource` with `scope: shared` |
| environment | `jd env up` of a definition whose type has `layer: environment` (platform identity) | environment | `SharedResource` with `scope: environment`; the selected implementation's `environment` part |
| workload | `jd deploy` / `release deploy` / `promote` | workload | the selected implementation's class part, per requirement; the runtime |

`uses` lists shared resources only. Allowed: shared → shared; environment → shared, environment; component, runtime → any. The `uses` graph
has no cycles. A workload deploy never expands a shared resource or an `environment` part (section 3). No policy creates anything.

### Reading another part: `${<name>.<export>}`

A part reads a shared resource as `${cosmos-account.endpoint}`, never as `env.*`. A class part reads its own component's `environment` part
by the component's name (`${database.databaseName}`). Every other root is unchanged.

| Root in a part | Allowed when | Resolves to |
|---|---|---|
| `<node>.<output>` | a node of the same part | pending node output (as today) |
| `<name>.<export>` | `<name>` is in the component's `uses`, or is the part's own component (class parts only) | when `<name>` is deployed **in the same run**: its export expression, evaluated in place (below); otherwise the descriptor value `values.<name>.<export>` |
| `env.name`, `env.region`, `env.type` | always | the descriptor's top-level fields |
| `env.<anything else>` | **never** (an error), so `env.*` cannot read past `uses` | — |
| `role.*`, `workload.*`, `runtime.*`, `resource.*` | as today | as today |

**Checked at load (new):** every root in a part's config and exports is a node of that part, a name in its `uses`, its own component, or a
reserved root; `<export>` is declared in that part's `exports`. Unknown names, undeclared exports and missing `uses` are load errors at the
file and location (today they surface only at resolve). A node name may not equal a name the part can read as a root.

**In-run reads are evaluated in place.** All parts of one `env up` run expand into one scope whose node names are unique (section 3). So
when `<name>` is a part of the run, the evaluator evaluates `<name>`'s export expression in place, and the read becomes ordinary node
references: in the shared run, `${shared-group.name}` in `log-analytics` → `${group.resourceGroupName}`. Chained exports resolve the same
way: `${cosmos-account.resourceGroup}` → `${shared-group.name}` → `group.resourceGroupName`.

- Graph edges, `ExportValues` and the second pass are unchanged (they see node references); only `ExpressionContext` gains the run's part
  exports (empty in a workload run), and a secret output stays secret.
- The run's parts come first, then the descriptor. A run's part whose name is already in the base's values is an error (new keys only).
- **Cycles cannot occur:** `uses` is acyclic and an export reads only its own part's nodes, its `uses` and reserved roots (load checks),
  so in-place evaluation follows `uses` edges and ends. The negative tests of those two checks are the cycle tests.

### Example: `database`

```yaml
# catalog/components/database.yaml
kind: Component
name: database
description: A database for the workload; the engine (today a container in the environment's Cosmos SQL database) is the platform's choice, and `engine` says which.
exports: [engine, endpoint, database, container]     # the contract: every class of every implementation exports exactly these
uses: [cosmos-account]
implementations:
  default:                                           # every environment type without its own key (all of them, in the seed)
    environment:                                     # once per environment that offers database
      nodes:
        database:                                    # config as today, with ${cosmos-account.resourceGroup} and ${cosmos-account.name}
          template: azure/cosmos-sql-database
          config: { resourceGroupName: ${cosmos-account.resourceGroup}, accountName: ${cosmos-account.name}, databaseName: ${name('db')}, throughput: 400 }
      exports: { databaseName: ${database.databaseName}, databaseId: ${database.databaseId} }
    classes:
      standard:                                      # what each requiring workload gets
        nodes:
          container:                                 # databaseName reads this component's environment part
            template: azure/cosmos-sql-container
            config: { accountId: ${cosmos-account.id}, databaseName: ${database.databaseName}, containerName: ${name('cosmos-container')}, partitionKeyPath: /id }
          access:
            kind: grant
            template: azure/cosmos-sql-role-assignment
            config:
              accountId: ${cosmos-account.id}        # not the target: may read what the component uses
              scope: ${container.scope}              # the target (grants.yaml): this workload's own container only
              principalId: ${runtime.principalId}
              roleGuid: ${role.cosmos-data-contributor}
              roleAssignmentId: ${guid(container.scope, runtime.principalId, 'data-contributor')}
        exports:
          engine: cosmos-sql
          endpoint: ${cosmos-account.endpoint}
          database: ${database.databaseName}
          container: ${container.containerName}
```

### Example: `monitoring` (explicit observability)

```yaml
# catalog/components/monitoring.yaml
kind: Component
name: monitoring
description: Application Insights for the workload, reporting to the shared Log Analytics workspace.
exports: [connectionString]
uses: [env-group, log-analytics]
implementations:
  default:
    classes:
      standard:
        nodes:
          appinsights:                               # today's policy-added node, config unchanged but for the roots
            template: azure/application-insights
            config: { resourceGroupName: ${env-group.name}, location: ${env.region}, workspaceResourceId: ${log-analytics.id}, appInsightsName: ${name('appi')} }
          access:                                    # today's appinsights-access, unchanged
            kind: grant
            template: azure/role-assignment
            config: { scope: ${appinsights.appInsightsId}, principalId: ${runtime.principalId}, roleGuid: …, roleAssignmentId: … }
        exports: { connectionString: ${appinsights.connectionString} }
```

A workload asks for it (`requires: [{ type: monitoring }]`) and maps the export to its variable like any other contract:
`APPLICATIONINSIGHTS_CONNECTION_STRING: ${resource.monitoring.connectionString}`.

### Shared resources (the seed catalog)

| Name | Scope | Uses | Node (unchanged name) | Exports |
|---|---|---|---|---|
| `shared-group` | shared | — | `group` (resource-group) | `name`, `location` |
| `log-analytics` | shared | `shared-group` | `workspace` | `id`, `name`, `customerId`, `resourceGroup` |
| `cosmos-account` | shared | `shared-group` | `cosmos` | `id`, `name`, `endpoint`, `resourceGroup` |
| `env-group` | environment | — | `group` (resource-group) | `name`, `location` |
| `apps-env` | environment | `env-group`, `log-analytics` | `apps` | `id`, `defaultDomain` |

E.g. `catalog/shared/cosmos-account.yaml`: `kind: SharedResource`, `scope: shared`, `uses: [shared-group]`, today's `cosmos` node with
`${shared-group.name}`/`.location` for the group, and `exports: { id: ${cosmos.accountId}, name: …, endpoint: …, resourceGroup:
${shared-group.name} }`. A shared resource has no implementations: the shared layer has one type. Environment-scope resources (`env-group`, `apps-env`) likewise
cannot vary by environment type yet; that is deferred and additive later (principle 10).

### The platform-owned implicit resources

Implicit provisioning is limited to these. Workloads only read them through `uses`; no grant can target them (boundary 1).

| Resource | Part | Layer | When |
|---|---|---|---|
| shared resource group `rg-shared-…` | `shared-group` | shared | always |
| Log Analytics workspace `law-shared-…` | `log-analytics` | shared | when an offered component or runtime uses it |
| Cosmos DB account `cosmos-shared-…` (free tier) | `cosmos-account` | shared | when an offered component uses it |
| environment resource group `rg-<env>-…` | `env-group` | environment | always |
| Container Apps environment `cae-<env>-…` | `apps-env` | environment | when the offered runtime uses it |
| Cosmos SQL database `db-<env>` | `database`'s environment part | environment | when the environment offers `database` |

Per workload, the only implicit node is its compute, the **runtime** (a Container App), which is the workload's own. Everything else a
workload gets comes from its `requires`.

### The runtime

The runtime is **not** a `Component`: it has no contract and no classes and is never required. The environment offers exactly one, and every
workload deployed there gets it. It keeps its own kind so its checks stay in one place: the `probe`, the `release` block, the reserved
`runtime` node and the inputs the release flow owns (today's checks, per implementation). `MappingMatcher` goes.

`catalog/runtimes/container-app.yaml`: `kind: Runtime`, `uses: [env-group, apps-env]`, `implementations.default` holding today's `probe`,
`release` and `runtime` node, which now reads `${env-group.name}` and `${apps-env.id}`.

**Layout:** `catalog/` keeps `catalog.yaml`, `naming.yaml`, `roles.yaml`, `templates/` and `policies/`, and gains `grants.yaml`,
`components/{database,monitoring}.yaml`, `runtimes/container-app.yaml` and `shared/<name>.yaml`. Deleted: `types/`, `mappings/`. The
layout is organisational only: the loader goes by `kind`.

## 3. Scopes and who deploys

### Environment definitions: type and offers

```yaml
# samples/environments/stage.yaml; dev.yaml: type dev, no approval; shared.yaml: type shared (its descriptor is the --base of the others)
kind: EnvironmentDefinition
name: stage
type: stage
offers: [container-app, database, monitoring]
approval: { ... }             # unchanged; required by the type
```

`tier`, `requires`, `values` and `grantable` leave the definition. Offers are names only. `jd env up` keeps its command line (`--base`,
`--region`, `--out`).

| `jd env up` of layer | Collects (one graph, owner `@<name>`, scope `substrate`) | Must already exist |
|---|---|---|
| `shared` | the `scope: shared` resources reached through `uses` from the offered components and runtime | — |
| `environment` | the offered components' `environment` parts (of the implementation for its type), and the `scope: environment` resources reached through `uses` from them and the offered runtime | every `scope: shared` resource reached the same way, present in the base descriptor; otherwise an error before anything is created, naming the resource and the base |

- **The substrate follows from the offers.** No offered component uses `cosmos-account`, so no Cosmos account.
- **One owner per shared stack.** Only the shared layer deploys shared resources; an environment layer only checks its base has them.
- **One scope, today's ids.** The parts of one run expand into one scope, `substrate` (an engine constant like `@workload`), each node
  evaluated with `substrate` as its current id, so ids, stacks and `name()` hashes stay as today: `@dev/dev/substrate/apps`,
  `_shared.shared.substrate.cosmos`, `cosmos-shared-39d0b1`. Node names must be unique across the parts that can share a scope: `group`,
  `workspace`, `cosmos` (shared); `group`, `apps`, `database` (environment); a component's implementations may reuse one name. This is a load
  check and a **constraint on every later component** (phase 2's messaging and secrets included).
- **Node-scope policies still reach substrate nodes:** `template` and `environmentType` match them; a substrate node has no `type` or
  `class`, so those criteria never match it. No seed policy changes behaviour.
- At most one runtime among an environment's offers; offers name declared components or runtimes.

### The descriptor

`jd env up` writes it as today, but `values` is **every collected part's exports**, evaluated with the deployed outputs and keyed by part
name, merged into the base's values (new keys only). Secret outputs are dropped before evaluation as today, so an export that reads one,
directly or chained, stays pending and is an error: `env up` fails and writes no descriptor (boundary 12).

```yaml
kind: Environment
name: dev
region: southeastasia
type: dev
offers: [container-app, database, monitoring]   # a shared layer's descriptor offers nothing, so no workload can deploy on it
values:
  shared-group:      { name: rg-shared-5d9b60, location: southeastasia }
  log-analytics:     { id: …, name: …, customerId: …, resourceGroup: rg-shared-5d9b60 }
  cosmos-account:    { id: …, name: cosmos-shared-39d0b1, endpoint: …, resourceGroup: rg-shared-5d9b60 }
  env-group:         { name: rg-dev-…, location: southeastasia }
  apps-env:          { id: …, defaultDomain: … }
  database:          { databaseName: db-dev, databaseId: … }
approval: { … }                                  # stage only, still the last block
```

### Workload resolution

- A requirement `type: database` selects the component `database`, which must be among the descriptor's `offers` ("environment dev does
  not offer database"); then the implementation for the descriptor's type (or `default`); then `classes.<class>` (an unknown class names the
  classes). No matching.
- The runtime is the descriptor's offered runtime, its implementation looked up the same way ("environment X offers no runtime" if none).
- Workload parts read shared resources and their component's environment part from the descriptor, at expansion; preview shows them as
  `env.*` values were shown before.
- Workload definitions keep their shape: `requires[].type` names the component, `${resource.<id>.<export>}` reads its contract.

### Grants: the target rule

A grant's **target** may only be a node created by the workload's own requirement, or its runtime; never a shared or environment resource.
`catalog/grants.yaml` declares, per grant template, the input naming the resource the grant is on: `kind: Grants`,
`targets: { azure/role-assignment: scope, azure/cosmos-sql-role-assignment: scope }`.

- **Load:** a `kind: grant` node exists only in a class part or the runtime; its template is listed in `targets`; its target input is set
  and is exactly `${<node>.<output>}`, where `<node>` is a node of the same part or `runtime`: no literal, no surrounding text, no
  `<name>.<export>`, `env.*`, `resource.*`, `role.*` or function. The grant's other fields may read what the component `uses`
  (`accountId: ${cosmos-account.id}`). The resolver does not read templates, so the declaration lives in the catalog like the role GUIDs;
  `TemplateLibrary.Check` already refuses a config key the template does not declare.
- **Graph build** (after policies): the target's value is one pending reference to a node in the same scope or to the runtime node, and
  nothing else. This catches a policy `set`/`default` on a target. `grantable` is gone from definitions and descriptors.

### Policies: `require`

`enforce-monitoring` becomes `match: { kind: runtime }`, `require: monitoring` (its `reason` unchanged). The narrowest form: `require` names **one** declared component, only on a workload-scope policy (`kind: runtime`, optionally
`environmentType`). A workload with no requirement of that type (any id, any class) is an error at its `requires`, naming the policy, its
file and its reason. `require` guarantees only that the monitoring resource exists in the workload; it does not guarantee that the workload maps
`APPLICATIONINSIGHTS_CONNECTION_STRING` to it. **Decided (lead, S21c):** the policy requires only the requirement, not the variable; each
workload maps `APPLICATIONINSIGHTS_CONNECTION_STRING: ${resource.monitoring.connectionString}` itself (principle 6: what it gets, it writes).
A policy cannot add nodes (`add` is gone: a policy file with `add` is a load error saying `require` replaces it), require a class or a count. Node-scope `set`/`default` and workload-scope
`set`/`default` on `runtime.*` are unchanged.

### Security boundaries (D19) that move

| # | Boundary | Today | After | Enforced |
|---|---|---|---|---|
| 1 | What a grant may target | `env.<path>` in the environment's `grantable` (`cosmos.accountId`), or any node | only a node of the grant's own part or the runtime, as a single reference in the input `grants.yaml` names | load (catalog parts); graph build (after policies) |
| 2 | Who may add a grant | catalog mappings and policy `add` | class parts and the runtime only; policy `add` no longer exists | load |
| 3 | What any node may read from the environment | any `env.*` value | only its component's `uses`, its own component's environment part, `env.name/region/type` | load |
| 4 | Bypass through `env.*` | — | `env.<path>` other than name/region/type is an error, so `${env.cosmos-account.id}` cannot skip 1 and 3 | evaluator |
| 5 | A class part reading another component's environment part | possible (any `env.*`) | an error: only its own component's name is a root | load |
| 6 | Workload input crossing into the platform | variables take only `${resource.<id>.<export>}` | unchanged; `${cosmos-account.id}` in a variable is rejected like any other `${…}` | `WorkloadRules` |
| 7 | A workload deploy creating shared or environment resources | substrate types resolved only by `ResolveSubstrate` | a workload graph expands only class parts and the runtime | expander |
| 8 | An environment `env up` creating shared resources | possible (any mapping) | only a `layer: shared` type deploys `scope: shared`; an environment checks its base | resolver |
| 9 | Requirement scopes reading each other; only the runtime reads exports | — | unchanged | graph builder |
| 10 | Owner prefix `@<name>` keeps environment and workload ids apart | — | unchanged | expander |
| 11 | Deploying a component the environment does not offer | resolves, then fails on a missing `env.*` | refused at resolve | resolver |
| 12 | A secret output reaching the descriptor | definition `values` evaluated with secrets dropped | part exports evaluated the same way; an export reading one (directly or chained) fails `env up` | `DescriptorComposer` |
| 13 | A gated environment written without its gate | schema: `tier: protected` requires `approval` | the type's `approval: true` requires the block | env up, resolve |

Every row is a required test in the slice that moves it, and every forbidden path a negative test. Required negative tests for rows 1–2:
a grant targeting a used shared resource (`scope: ${cosmos-account.id}`); a target through a chained export (an export of a used resource
that chains to another part's node, `${cosmos-account.resourceGroup}`-style); a target reading its own component's environment part
(`${database.databaseId}`); a literal target and a target with text around the reference; a grant template not in `grants.yaml`; a grant
node in a shared resource or environment part; a policy with `add` (refused at load); a policy `set` on a grant's target to a shared value
(refused at graph build). Row 12: a part whose export reads a secret output, directly and chained. Row 13: a `stage` definition and
descriptor without `approval`.

### How the later phases stay open (roadmap rule 4)

| Later idea | What phase 1 leaves open | What phase 1 does not do |
|---|---|---|
| Phase 2 components (messaging, secrets) | each is a component file plus any shared resources and a `grants.yaml` line for a new grant template, no C#; node names unique per scope | no `jd catalog new` |
| N1/N2 workloads as components (phase 3) | one shape: named `exports`, `uses`, per-scope parts, per-type implementations, a reserved runtime | no contract version, no workload `kind` change |
| N5 offers | the grant target rule is one check, in the loader and the graph builder: "own nodes only". Offers add a second allowed target, a provider's offered access level, in the same place | no access levels, no consumer lists |
| N4 bind/attach (phases 4–5) | requirement → part lookup lives in one place (the expander); "create" is the only mode. Bind resolves against deployed exports instead of instantiating a class part, and its grant targets an offer (above) | no `mode` field |
| N6 deployed-exports registry | the descriptor's `values` are "deployed exports keyed `<name>.<export>`", read by one root syntax | no registry, no workload entries |
| Name lookup | `requires[].type` resolves a name in the catalog namespace; phase 4 can extend it to workload names | — |

## 4. What changes in the code

| Project / file | Change |
|---|---|
| `schemas/catalog/` | + `component`, `sharedresource`, `runtime`, `grants`; `catalog` + `environmentTypes`; `policy` + `require`, − `add`, `tier` → `environmentType`; − `resourcetype`, `mapping` |
| `jd.resolver/catalog/Catalog.cs` | `Catalog(Version, EnvironmentTypes, Components, SharedResources, Runtimes, GrantTargets, Policies, Naming, Roles)`; records `EnvironmentType`, `Component`, `Implementation`, `SharedResource`, `Runtime`, `Part(Nodes, Exports)`; `Policy.Require`, − `Policy.Add`; − `ResourceType`, `Mapping` |
| `jd.resolver/catalog/CatalogParser.cs` | new kinds; checks: types and implementation keys, same classes per implementation, class exports equal the contract, unique names and reserved roots, `uses` exist, scope rules, no cycles, node names unique per scope, roots and exports, grant nodes (where, template listed, target form), `require` names a component and is workload scope; the runtime checks move to `Runtime`; − mapping/type checks |
| `jd.resolver/matching/MappingMatcher.cs` | **deleted** |
| `jd.resolver/expressions/ExpressionEvaluator.cs`, `ExpressionModel.cs` | `<name>.<export>`: a run's part evaluated in place from `ExpressionContext`'s part exports, else the descriptor's `values`; `env.*` limited to name/region/type; reports `<name>.<export>` reads (replaces the `env.` paths) |
| `jd.resolver/expansion/ExpandedModel.cs` | `ConfigText.EnvPaths` → `PartReads`, read by the preview |
| `jd.resolver/expansion/Expander.cs` | requirement → offered component → implementation → class part; runtime from offers; `ExpandEnvironment(definition, base)` collects parts by layer into scope `substrate` |
| `jd.resolver/policies/PolicyApplier.cs` | − the `add` path (`WorkloadNodes`); + the `require` check; `tier` → `environmentType` |
| `jd.resolver/graph/GraphBuilder.cs` | the grant target check replaces the `grantable` check; − policy-added workload nodes; edges unchanged. `ExportValues.cs` unchanged |
| `jd.resolver/environment/*` | definition: `type`, `offers`; − `tier`, `requires`, `values`, `grantable`. Descriptor: `type`, `offers`; − `tier`, `grantable`. Type declared and `approval` coupling checked against the catalog. `DescriptorComposer` walks the collected parts' exports (secrets dropped as today). Schemas `environment*.schema.json` follow (the `anyOf` goes) |
| `jd.resolver/release/ApprovalRecord.cs`, `jd.orchestrator/ReleaseRecordBuilder.cs` | records write `environment.type`; `release-record` and `approval-record` schemas take `type`, and still accept `tier` so records written before stay readable |
| `jd.orchestrator/Orchestrator.cs` | `ContextFor` passes the run's part exports (empty for workload runs) |
| `jd.cli/EnvCli.cs`, `Cli.cs` | wiring and messages (`type` for `tier`) |
| `jd.cli/PreviewFormatter.cs` | `(from env.cosmos.accountId)` → `(from cosmos-account.id)`: renders `PartReads` |
| `jd.bp.pulumi` | unchanged |
| tests | catalog fixtures (`TestCatalog`, the Azure tests' test-only types and mappings) rewritten to the new kinds |

## 5. Behaviour preservation (the refactor's acceptance)

The same resources on Azure for the same inputs, **except the one change the user's choice 5 makes** (monitoring, below).

| Must be equal before and after | May differ |
|---|---|
| per node: `id`, `scope`, `name`, `template`, `kind`, `stack`, `phase`, `dependsOn`, resolved config values; resource names (`name()` hashes) | `provenance` (`Mapping: mappings/database/standard.yaml` → the component file) |
| what the orchestrator hands the backend per stack (stack name, every config entry and its secret flag) after the second pass | substrate nodes' pending text (`${group.resourceGroupName}` → `${shared-group.name}`), and so those nodes' `hash` |
| workload graphs' node `hash` (their environment reads resolve at expansion) | a substrate graph's `exports` (gone); the descriptor's layout (`type`, `offers`, values by part, no `grantable`); records' `environment.tier` → `type` |

**Proof:**
- **`DeployedConfigSnapshotTests`** (new, S21a's first commit, test-only, offline): `env up` shared → dev → stage and `jd deploy` of the seed
  workload with a fake backend whose outputs are deterministic (`<stack>/<output>`); it snapshots every package (stack, config entries,
  secret flags). Byte-identical through every slice except S21c, whose diff is exactly the monitoring change below.
- **Golden graphs** (`GoldenGraphTests`, `GoldenSubstrateTests`): each diff touches only "may differ" items (S21c: plus monitoring).
- **Upgrade gate** (lead-run, after the normal Azure gate, in S21c only: the one slice whose snapshot changes on purpose. S21e and S21f
  are proven by an unchanged snapshot, which per the Azure gate rule ([conventions](../engineering/conventions.md#how-we-work), 2026-10-18) means no
  Azure gate for them; phase 1 ends with one gate as its exit evidence). On one `PULUMI_BACKEND_URL` and `$JD_WALK`
  (walkthrough set-up, `JD_REGION` fixed): (1) `jd` from `main`'s head with `main`'s `catalog/` and `samples/`: `env up` shared, then dev
  (`--base`); `jd release create samples/walkthrough/releases/v1.manifest.yaml`; `jd release deploy` to dev. (2) `jd` from the slice head with
  its `catalog/` and `samples/`: the same two `env up` with `--force`, then `jd release create` and `release deploy` of v1 to dev. (3) Pass: step
  2 exits 0; every `env up` node `unchanged`; every workload node `unchanged` (S21e, S21f), or exactly the S21c set below. (4) Teardown with the
  slice's `teardown.sh`, nothing left; delete the Pulumi state and `$JD_WALK`; the lead records the result in status.md.

### The monitoring change (S21c)

Moving App Insights from a policy `add` (scope `@workload`) into the `monitoring` requirement's scope changes, per workload: the ids and
stacks `@workload/appinsights` → `monitoring/appinsights` and `@workload/appinsights-access` → `monitoring/access`; the resource name
`appi-<w>-<env>-<hash>`, because `name()` hashed the node name `appinsights` and now hashes the requirement id `monitoring`; and the
runtime's `APPLICATIONINSIGHTS_CONNECTION_STRING`, now the workload's variable, carrying the new component's value.

**Options:**
- **(a) Recommended: one-time recreation.** The next deploy creates the new App Insights and its grant, and rolls a new runtime revision
  (dark, probe, shift), as for any config change. jd deletes nothing that leaves a graph (no removal path yet, C16), so the old App Insights
  and its role assignment stay, in their old stacks, untouched. **Effect:** the telemetry history stays readable in the old component; until
  the shift the live revision keeps reporting there, after it the new revision reports to the new one, so the only gap is the minutes the
  new Metrics Publisher grant may take to propagate. The orphans cost nothing (App Insights bills by ingestion) and go with the environment
  resource group at teardown; a long-lived environment removes them by hand (`pulumi destroy` of the two old stacks per workload;
  `usage.md` says how). **Upgrade gate, S21c:** per workload, exactly two creates (`monitoring/appinsights`, `monitoring/access`) and the
  runtime updated (new revision, probe and shift run); every other node and every `env up` node `unchanged`; the old stacks untouched.
- **(b) Keep the old ids.** Needs a component flag that puts a requirement's nodes into the workload scope `@workload` and hashes names by
  node name: a permanent special case in the expander and the graph builder (~30 lines), and a second scope rule for everything that reads
  ids, serving only this migration. Not recommended: a permanent special case (or a per-workload migration step) for a one-off, against
  principle 7. (A workload could instead require `{ type: monitoring, id: appinsights }`, which keeps the hash and so the resource name, and
  `pulumi stack rename` could then move the state. Each workload would still need that manual migration step, and the stack ids and node
  names still change.)

Old records carry the old `graph.sha256`; nothing compares graph hashes across that line. S21c's sample workloads make new sets.

## 6. Migration

| What | Change |
|---|---|
| `catalog/` | `types/`, `mappings/` deleted, content moved to `components/`, `runtimes/`, `shared/`; `catalog.yaml` + `environmentTypes`; `grants.yaml` new |
| `catalog/policies/enforce-monitoring.yaml` | `add` and `set` → `require: monitoring`; the nodes move to `components/monitoring.yaml` |
| `samples/environments/{shared,dev,stage}.yaml` | `type` (`shared`, `dev`, `stage`) replaces `tier`; `offers` replaces `requires`/`values`/`grantable`; `approval` unchanged |
| `samples/workloads/*`, `samples/walkthrough/workloads/*` (app, app.bad, worker.v1–v3) | + `- type: monitoring` and `APPLICATIONINSIGHTS_CONNECTION_STRING: ${resource.monitoring.connectionString}`; the "Deliberately absent: monitoring" comment goes |
| descriptors already written (`$JD_WALK/*.env.yaml`) | invalid after S21a (`type`) and S21e (layout): re-run the three `jd env up --force`; every node `unchanged` |
| `JD_RECORDS_DIR` | records written before stay readable (`tier` still accepted) |
| `docs/walkthrough.md` | `DEV_RG`/`STAGE_RG` (lines 110–111) read by indentation (`^  resourceGroup:`), which the new layout breaks: a keyed read of `values.env-group.name`, as in `teardown.sh`; the preview step (lines 159–160) lists `monitoring/appinsights` and `monitoring/access`, provenance `Workload: container.variables`; the catalog row in "Architecture"; a note to re-run `env up` |
| `samples/walkthrough/teardown.sh` | lines 13–16 read the groups by indentation; both now sit at the same depth under different keys: a keyed read of `values.env-group.name` and `values.shared-group.name` (block style), e.g. `sed -n '/^  env-group:$/,/^  [^ ]/s/^    name: *//p'` |
| `stage-policy.sh`, `policies/limit-revisions.yaml`, release manifests | unchanged (`approval` stays the last block; the policy matches `template`) |
| docs | `resolver.md` (kinds, layout, expressions, engine rules, descriptor, policies), `provisioning.md` (Substrate), `approvals.md` §1–2 (types; the coupling from the catalog), `usage.md` (orphaned App Insights), `walkthrough.md`: each slice updates what it changes |

## 7. Slices

Each slice leaves `tools/verify.sh` green, the snapshot of section 5 byte-identical (S21c: exactly the monitoring change) and the Azure E2E
test passing. Line counts are production lines (C#, schemas), estimated.

| Slice | Content | Lines | Azure gate | Acceptance |
|---|---|---|---|---|
| **S21a** Environment types | `DeployedConfigSnapshotTests` first, in its own commit. `environmentTypes` in `catalog.yaml`; definition and descriptor `type` replaces `tier` (unknown type an error); `approval: true` replaces the schema `anyOf`; `env.type`; policy and mapping key `environmentType`; records write `type`, schemas still accept `tier`. Samples migrated | ~120 | **yes**: E2E | snapshot identical; boundary 13 tested; old records still read |
| **S21b** Components and runtimes | `Component` and `Runtime` (implementations by type, `default`, classes) replace `ResourceType` + `Mapping`; `MappingMatcher` deleted. Transitional: the substrate types become components `env-substrate`, `shared-substrate`, still required by the definitions | ~200 | no | goldens differ only in provenance; old kinds rejected clearly; lookup (type, `default`, none), same-classes and contract checks, each with a negative test |
| **S21c** Monitoring as a component | `components/monitoring.yaml` (reading today's `env.*` paths until S21f); policy `require`; policy `add` deleted; sample and walkthrough workloads require monitoring | ~80 | **yes**: E2E, then the upgrade gate expecting exactly the section 5 set | a workload without `monitoring` fails naming the policy; boundary 2's `add` part (rejected at load); golden and snapshot diffs exactly the monitoring change |
| **S21d** Shared resources and `uses` | `SharedResource`; implementations' `environment` part; `uses` on components and runtimes; the load checks of section 2 (roots, exports, scope rules, cycles, node-name uniqueness). `shared/*.yaml` and `database`'s environment part loaded and checked, consumed in S21e | ~180 | no | one negative test per load check; seed catalog loads |
| **S21e** Environments from offers | `offers`; the type's layer; `env up` collects parts per layer into `substrate`; in-run exports evaluated in place, else the base; descriptor values from part exports (secrets dropped), `offers`. Transitional: definitions keep `grantable: [cosmos-account.id]`, workload parts read `${env.cosmos-account.id}` until S21f. Transitional components deleted. Walkthrough, teardown, samples migrated | ~200 | **yes**: E2E, then the upgrade gate | substrate goldens equal per section 5; chained-export and in-run tests; boundaries 8 and 12 tested; upgrade gate all `unchanged` |
| **S21f** Workloads through `uses`; grant targets | parts and the runtime read `${<name>.<export>}`; `env.*` reserved only; `EnvPaths` → `PartReads`; `grants.yaml` and the target rule (load and graph build); `grantable` deleted; offers check; runtime from offers | ~220 | **yes**: E2E, then the upgrade gate | boundaries 1, 2 (grant nodes), 3–7, 9, 11 tested, with every negative test listed in section 3; workload golden hashes unchanged; upgrade gate all `unchanged` |

Total about 1,000 production lines added, offset in part by deletions (the matcher, the type/mapping checks, policy `add`, the definition
`values` walk, the `grantable` check).

## 8. Choices taken by the user (2026-10-18)

1. **Shared scope:** a separate shared layer, deployed once by its own `env up` (a definition whose type has `layer: shared`) and read
   through `--base`; one owner per shared stack. Deferred to phase 2: `env up <env>` bringing the shared layer up first when it is missing
   or stale.
2. **Offer values:** names only in phase 1.
3. **Environment types instead of tier matching** (section 2): the workload definition is the same everywhere; types declared once,
   `implementations` keyed by type with `default`, exact lookup, one contract; policies may match the type; approvals unchanged.
4. **Grants:** no `grantable` list; a grant targets only the workload's own nodes; targets declared in `grants.yaml` (section 3).
5. **Observability explicit:** `monitoring` is a required component; `enforce-monitoring` only requires it. Implicit resources: the
   platform-owned list in section 2. The one-time change: section 5.

## 9. ADRs affected

| ADR | Effect |
|---|---|
| [0003](../decisions/0003-pulumi-yaml-template-library.md) | holds: static templates, one stack per node, config-passing. Templates stay shared; a grant template's target input is declared in the catalog |
| [0004](../decisions/0004-two-layer-definition-model.md) | refines the platform layer: "mapping" becomes a component's implementation (per environment type); policies lose `add` and gain `require` |
| [0006](../decisions/0006-infra-deployed-with-app.md) | holds: App Insights stays workload-tier, now from the workload's `monitoring` requirement instead of a policy |
| [0008](../decisions/0008-enforcement-in-landing-zone.md) | holds: platform-owned shared and environment scopes are deployed only by `env up` |
| [0009](../decisions/0009-rbac-split-team-subscription.md) | holds and is made structural: scopes map to who deploys (boundaries 7, 8) |
| [0011](../decisions/0011-requirement-id.md) | holds: `requires[].type` names a component; `id` and `class` unchanged (classes live in each implementation) |
| [0013](../decisions/0013-container-apps-runtime.md) | refines "runtime is a declared property of the workload": the **environment offers** the runtime; the workload names none, as in the MVP |
| [0014](../decisions/0014-hook-points.md) | wording: hooks restrictable by environment **type** (was tier) |

No new ADR now. Once S21f lands, the component model (kinds, scopes, environment types, the grant target rule) is worth one
(`Proposed — pending review`), so resolver.md stops carrying the decision.
