# Resolver — proposed design

> Status: proposed design — not yet reviewed. Addresses open question [C52](../open-questions.md).
> Flow context: [workload_deployment_flow.html](workload_deployment_flow.html).

The resolver turns a workload definition into a graph of concrete, backend-renderable resources.
It is a **generic engine**: all Azure knowledge lives in a versioned **catalog** of YAML files that
platform engineers maintain. The C# code knows only the file formats, a substitution-only expression
syntax and a few built-in functions.

```
resolve(workload, environment, catalog@version) → ResolvedGraph     pure, deterministic, no Azure calls
```

## Requirements

1. Nothing Azure- or workload-specific is hard-coded in the resolver.
2. A handful of platform engineers can maintain the catalog: adding a resource type needs no C# change.
3. Resolution is a pure function separate from execution, so preview, approval diff and the
   destructive-change gate ([ADR 0010](../decisions/0010-replacement-protection-classes.md)) all
   consume the graph before anything is applied.
4. The resolver is a security boundary ([D19](../open-questions.md)): a definition must not be able
   to reach another workload's resources.

## Defects the design removed

The first hand-wired provisioner (`samples/provisioner`, deleted in S09) had these defects; each is now removed by the
design below. They stay listed as the requirements the design answers.

| # | Defect | Answer |
|---|---|---|
| 1 | Stack name = template name (`cosmos-db`), so every workload would share one stack | One stack per node, named from the node id ([Output](#output-resolvedgraph)) |
| 2 | Fixed role-assignment GUIDs collide when a second workload targets the same scope | `guid()` ids supplied by the resolver; templates carry none |
| 3 | `${resource.database.endpoint}` matched nothing: the template exported `documentEndpoint`; no type contract | Type `exports` contract; template contract check ([Catalog CI](#catalog-ci--what-makes-it-maintainable)) |
| 4 | `enableFreeTier: true` default: one free-tier account per subscription | Input without a default, set by the substrate |
| 5 | Preview fed `"<unknown-until-deployed>"` strings into downstream stacks | Pending references, never fake values (S10) |
| 6 | Resource list, order, names, region, role GUIDs, output wiring and grant logic hard-coded | Catalog data and the generic engine |
| 7 | Platform opinions (SKU `F1`, region, free tier) in template defaults | Templates are mechanics only (below) |

## Catalog layout

```
catalog/
  catalog.yaml                               # catalog version — recorded on every deployment
  types/database.yaml                        # contract teams bind to
  mappings/database/standard.yaml            # type + class → nodes
  policies/enforce-monitoring.yaml           # IT Ops layer
  templates/azure/cosmos-account/Pulumi.yaml # mechanics only
  naming.yaml                                # name rules per resource kind
  roles.yaml                                 # role name → GUID
```

The directory layout is organisational only. The loader takes every `*.yaml` under the catalog root
(except `templates/`, which holds Pulumi YAML) as **one YAML document per file** (no `---`
multi-document files) and finds out what it is from the required top-level `kind`: `Catalog`,
`ResourceType`, `Mapping`, `Policy`, `Naming` or `Roles`. An unknown or missing `kind` is an error.

The catalog is versioned as one unit; a deployment records the catalog version next to the
definition SHA ([ADR 0007](../decisions/0007-definition-upload-snapshot.md)).

## Catalog file kinds

Each kind has a JSON Schema in [`schemas/catalog/`](../../schemas/catalog/) (`<kind>.schema.json`),
so editors and CI validate it; the schemas are also embedded in `jd.resolver`, which validates every
file against its kind's schema. `${…}` expressions and config values are kept as written by the
loader; the expression evaluator interprets them later.

| Kind | Required fields | Cardinality |
|---|---|---|
| `Catalog` | `version` | exactly one |
| `ResourceType` | `name`, `classes`, `exports`, `description` | `name` unique |
| `Mapping` | `match`, `nodes`; `exports` for a requirement mapping | `match` has `type` (a requirement mapping, naming a declared type) or `kind: runtime` (a runtime mapping, [below](#runtime-is-a-mapping-too)); one with neither would match every type and skip the exports contract, so it is rejected; a requirement mapping's `exports` keys equal the type's `exports`. A runtime mapping must declare a node named `runtime`, has no `exports`, and may have a `probe`; `runtime` is reserved, so no requirement mapping or policy `add` declares a node of that name, and only a runtime mapping has a `probe` (`path` starting with `/`, `expectedStatus` 100-599). |
| `Policy` | `name`, `reason`, `match`, and at least one of `set` / `default` / `add` | `name` unique |
| `Naming` | `rules` (resource kind → `pattern`, `maxLength`, `allowed`) | files merge; a rule is defined once; `allowed` must be a valid regex character class that accepts every hex digit `0-9a-f` |
| `Roles` | `roles` (name → role-definition GUID) | files merge; a role is defined once |

A node is `{ template, kind?: create | grant, config }`; `kind` defaults to `create`. Mapping `match`
keys are `type`, `class`, `tier`, `kind`, `runtime`; a policy may also match on `template`.
Naming `pattern` placeholders are `{workload}`, `{id}`, `{env}` and `{hash}`. The loader collects
every error (file and location inside it) in one pass.

### Type — the contract teams bind to

Source of the workload schema's `type` enum (generated, not hand-maintained). The name is what the
workload asks for (`database`, `cache`); the platform's mapping picks the engine, and an `engine` export
tells the code which one it got (user decision recorded under C52; supersedes the earlier "type names
the interface" rule). Changing the engine behind a type for an existing workload must later route to
approval (F54).

```yaml
kind: ResourceType
name: database
description: A database for the workload. The platform's mapping decides the engine (today a container in the environment's shared Cosmos DB, SQL API); the `engine` export tells the code which one it got.
classes: [standard]
exports: [engine, endpoint, database, container]   # the only valid ${resource.<id>.*}
```

Team overrides are out of the MVP, so the format has no `overridable` block yet.

### Mapping — requirement → graph nodes

Selected by matching criteria, never by conditionals.

```yaml
# mappings/database/standard.yaml (the seed catalog)
kind: Mapping
match: { type: database, class: standard }
nodes:
  container:
    template: azure/cosmos-sql-container
    config:
      accountId: ${env.cosmos.accountId}
      databaseName: ${env.cosmos.databaseName}
      containerName: ${name('cosmos-container')}
      partitionKeyPath: /id
  access:
    kind: grant                                   # surfaced in the approval diff
    template: azure/cosmos-sql-role-assignment
    config:
      accountId: ${env.cosmos.accountId}
      scope: ${container.scope}                   # this container only, not the account
      principalId: ${runtime.principalId}         # this reference alone orders it after the revision step
      roleGuid: ${role.cosmos-data-contributor}
      roleAssignmentId: ${guid(container.scope, runtime.principalId, 'data-contributor')}
exports:
  engine: cosmos-sql                              # a literal from data: what the code is talking to
  endpoint: ${env.cosmos.endpoint}
  database: ${env.cosmos.databaseName}
  container: ${container.containerName}
```

The Cosmos account and one shared-throughput database (400 RU/s) per environment are substrate resources, so the mapping reads them from `${env.cosmos.…}` and creates only the workload's container (it has no throughput of its own) plus a grant scoped to that container. A grant node reads only grantable environment values, so the grant template derives everything else from `accountId`.

### Policy — IT Ops layer

Can `set` fields (wins over everything), fill `default`s, or `add` nodes. One policy per file:

```yaml
# policies/enforce-monitoring.yaml
kind: Policy
name: enforce-monitoring
reason: Every workload reports to the environment workspace
match: { kind: runtime }
add:
  appinsights:
    template: azure/application-insights
    config: { workspaceResourceId: ${env.logAnalytics.id}, appInsightsName: ${name('appi')} }
set:
  runtime.appInsightsConnectionString: ${appinsights.connectionString}
```

```yaml
# policies/protected-cosmos-failover.yaml
kind: Policy
name: protected-cosmos-failover
reason: Protected tiers need automatic failover
match: { template: azure/cosmos-account, tier: protected }
set: { enableAutomaticFailover: true }
```

### Template — Pulumi YAML, mechanics only

Templates live in `catalog/templates/<provider>/<name>/Pulumi.yaml` (Pulumi YAML runtime, `azure-native` provider);
a node's `template` is `<provider>/<name>`.

- The template's own `configuration:` and `outputs:` blocks are its interface; no extra metadata. Every input is
  declared; a node sets each one, since the seed templates carry **no defaults**.
- No platform opinions: no SKUs, regions, free-tier flags, throughput or names. Mappings, policies and the
  environment descriptor supply them. Literals that are mechanics stay (the only SKU Azure still allows, the
  `ServicePrincipal` principal type for workload identities).
- No fixed GUIDs; the resolver supplies deterministic ones. A role template takes the role GUID and builds the
  full role-definition id itself (`roles.yaml` holds GUIDs only).
- Resource-specific id formats live in the template, not in mappings: for example `cosmos-sql-container` outputs
  `scope`, the data-plane scope id of the container.
- `options: protect: true` per protection class ([ADR 0010](../decisions/0010-replacement-protection-classes.md)).
- Pulumi config values are strings; structured inputs (`Map<String>` tags) are passed as JSON.
- Pulumi YAML has no loop, no map-to-list function and no `fromJSON`, so a template cannot reshape a map into the list a
  resource wants. List-shaped inputs are built by the engine and declared as lists: `container-app` takes `variables` as
  `List<Map<String>>`, items `{name, value}`, the shape of the container's `env`.
- Templates declare inputs under `configuration:`, which current Pulumi reports as deprecated in favour of `config:`.
  They stay on `configuration:` because project-level `config:` cannot declare the `number` type the Log Analytics
  daily cap needs. When that changes, rename the block in each template and change the block name `TemplateLibrary`
  reads (`jd.bp.pulumi`); the contract check fails every template it can no longer read.
- Where a template needs part of an ARM id (resource group, account name, subscription), it takes the id as its
  input and splits it (`fn::split`) instead of asking for the parts, so there is one source and no Azure call.
- A secret read inside a template (the Log Analytics shared key) is wrapped in `fn::secret`, because the provider
  schema does not mark it secret and it would otherwise sit in state in clear text.

The seed library: substrate `resource-group`, `log-analytics` (daily cap), `container-apps-environment`
(Consumption only), `cosmos-account` (with a throughput hard cap), `cosmos-sql-database` (shared throughput); workload `cosmos-sql-container`,
`cosmos-sql-role-assignment`, `application-insights`, `role-assignment`, and the runtime template `container-app` (a Container App
revision on Consumption with a system-assigned identity, `Multiple` active revisions and ingress on `targetPort`; size, replicas,
image and variables are inputs; all traffic goes to the latest revision until the release flow adds traffic control).

### Runtime is a mapping too

A runtime mapping, `match: { kind: runtime }` (optionally `tier`), produces the workload's runtime: a node named `runtime`
([ADR 0013](../decisions/0013-container-apps-runtime.md)). The engine expands it like any other mapping, once per workload, and
the app is not special-cased:

```yaml
# mappings/runtime/container-app.yaml
kind: Mapping
match: { kind: runtime }
probe: { path: /health, expectedStatus: 200 }     # data for the release flow; the resolver only carries it
nodes:
  runtime:
    template: azure/container-app
    config:
      resourceGroupName: ${env.resourceGroup}
      location: ${env.region}
      containerAppsEnvironmentId: ${env.containerAppsEnvironment.id}
      containerAppName: ${name('ca')}
      image: ${workload.image}
      targetPort: ${workload.port}
      externalIngress: true
      cpu: 0.25                                   # free tier: standards section 6
      memory: 0.5Gi
      minReplicas: 0
      maxReplicas: 1
      variables:                                  # the workload's variables, deployed as a list of {name, value}
        fn::entries: workload.variables
```

#### Workload variables

The workload's `container.variables` reach the runtime node through the one **config-level form** the engine has, `fn::entries`
(in the style of Pulumi's `fn::` functions): `{ fn::entries: workload.variables }` as a node's config value stands for that map,
and the node deploys it as a list of `{name, value}` items sorted by name (ordinal), because a Pulumi YAML template cannot turn a
map into the list a Container App's `env` wants. The form is exactly `{ fn::entries: workload.variables }`, and it is valid only as the
value of a **top-level field of the `runtime` node's config** in the runtime mapping, and never in a `grant`. Anywhere else (nested in
an object, an array item, another node, a requirement mapping, a policy `add` or `set` value, a literal map as the source) it is an
error: `fn::entries is not valid here`. Only the walk of that one field is given the workload's variables, which are walked as plain
properties, so a variable may itself be named `fn::entries`. A policy gives individual variables, `runtime.variables.<NAME>`, as a
**string** (an expression string is fine); not the whole field (which would drop the list form), not a path below a variable
(`runtime.variables.<NAME>.x`), and not an object, array, number, boolean or null, all errors at the path as written in the policy.

- In the graph the value stays a **map** (`ConfigObject` with `AsEntries`), so every variable is a leaf `variables.<NAME>` with its
  own provenance (`Workload: container.variables`, or the policy that set it) and its own pending state, and a
  workload-scope policy addresses it as `runtime.variables.<NAME>`. The canonical config, graph JSON and node hash show the
  deployed form: the sorted list. The orchestrator builds that list when it fills the node, so a change of form changes the hash.
- A variable's value is text that may contain only `${resource.<id>.<output>}` references; the workload validator
  (`WorkloadRules`) rejects any other `${…}` (`env`, `name()`, `workload`, `runtime`, `role`, a node), naming the variable, because a
  variable is workload input crossing into the platform ([D19](../open-questions.md)). `${resource.<id>.<export>}` takes the value of that requirement's export. An export
  that is already a value (`${env.cosmos.endpoint}`) is resolved at expansion, so preview shows it; one that reads a node output
  (`${container.containerName}`) stays pending and adds an edge from the runtime node to that node (see Security and the depends-on
  field). An unknown id or export is an error naming the variable, reported against the workload file at `container.variables.<NAME>`, as is any
  other problem in a variable's value.
- A value that reads a secret output makes the whole `variables` entry secret (the existing rule), so it is masked in the backend's
  config and state. In the Container App it still appears as plain text in `env`; `secrets` + `secretRef` are a later slice.

A workload names no runtime yet (one runtime for the MVP); the `runtime` match key (`runtime: container-app`) stays reserved for
when a second runtime exists, and a mapping that uses it is never selected. The runtime node gets its own phase (`runtime`) and
is not deployed by the infrastructure walk ([provisioning.md](provisioning.md)). Its fields carry the runtime mapping as their source.

## Environment descriptor

An environment publishes what the resolver needs as one YAML document, validated by
[`schemas/environment.schema.json`](../../schemas/environment.schema.json) (embedded in `jd.resolver`).
It is written by `jd env up` from substrate deployment outputs ([provisioning.md](provisioning.md#substrate-jd-env-up)); the resolver only reads it.

```yaml
kind: Environment
name: dev                 # same pattern as workload ids
region: southeastasia
tier: team                # team | protected
values:                   # substrate outputs, addressable as ${env.<path>}
  resourceGroup: rg-…
  cosmos: { accountName: …, accountId: …, databaseName: …, databaseId: …, endpoint: … }
  logAnalytics: { id: … }
grantable:                # paths under values that workloads may grant on
  - cosmos.accountId
```

- **Lookup:** `env.name`, `env.region` and `env.tier` are reserved and come from the top-level
  fields; any other `env.<dot.path>` resolves to a string inside `values`. A path to a group (for
  example `env.cosmos`) or to nothing is not found.
- **Rules beyond the schema:** `values` must not define `name`, `region` or `tier`; no key in `values`
  may contain `.` (a dot path must be unambiguous); every `grantable` path must be a value in `values`. The loader reports all errors with their location.
- **Security:** `grantable` is the boundary the engine enforces for `grant` nodes
  ([D19](../open-questions.md)): a workload can only grant on the listed `env.*` resources.

### Environment definition

The descriptor is produced from an **environment definition**
([`schemas/environment-definition.schema.json`](../../schemas/environment-definition.schema.json), `jd.resolver`):
`kind: EnvironmentDefinition`, `name`, `tier`, `requires` (substrate types, with `id` and `class` as in a workload; effective ids
unique), `values` (the descriptor's layout; each leaf an expression, normally `${resource.<id>.<export>}`) and `grantable`.
`Resolver.ResolveSubstrate(definition, file, catalog, environment)` is the one entry point. The owner is the definition, with
the name `@<name>` (a workload name cannot start with `@`) in node ids, stacks and `name()` hashes: ids `@dev/dev/substrate/group`,
stacks `_dev.dev.substrate.group`, so a workload named like the environment, with a requirement `substrate`, shares nothing with the
substrate and a workload deploy never touches it ([ADR 0006](../decisions/0006-infra-deployed-with-app.md)). `ExpansionResult.Owner` records the owner kind
(`Workload` or `Environment`); an environment owner has no runtime, so workload-scope policies add nothing. In a substrate
mapping `${workload.name}` therefore evaluates to `@<name>`; the `{workload}` naming placeholder drops the `@` through `allowed`. Its team is empty
(`${workload.team}` is empty). The `environment` is `definition.Over(region, base)`: the definition's name and tier, the region given on the
command line, and the **base descriptor's values and `grantable`**, so mappings read `${env.…}` of the layer below.

`DescriptorComposer` turns the resolved graph and the node outputs into the descriptor text: each export is evaluated with its
requirement's node outputs; each `values` leaf is evaluated with those as the known `resource.*` references (a leaf that stays
pending, or names a node or an unknown export, is an error); the result is merged into the base's values (a key the base has is a
collision error), `grantable` is the union, and the composed text must pass `EnvironmentParser`, so every rule above holds.
`region` is the caller's; the descriptor's `name` and `tier` are the definition's.

## Expressions

Config values, exports and workload variables are strings that may mix text and `${…}` expressions
(`${env.cosmos.accountId}/dbs/${env.cosmos.databaseName}`). Non-string values are never
touched. There is no escaping and no operators. Evaluation is pure (`jd.resolver.expressions`): the caller supplies a
context (catalog roles and naming, environment descriptor, workload name and team, the current id, the node names in
scope, the outputs already known) and a file/location for error reports.

```
expression := path | function '(' [ argument { ',' argument } ] ')'
function   := [a-z][a-z0-9]*
argument   := path | "'" literal "'"          # a literal has no quote inside it; valid only as an argument
path       := segment { '.' segment }          # segment: letters, digits, '_' and '-'
```

| Path | Meaning |
|---|---|
| `env.<path>` | `EnvironmentDescriptor.TryGet`; an unknown path is an error |
| `role.<name>` | role GUID from the catalog `Roles`; unknown is an error |
| `workload.name`, `workload.team` | the workload being resolved |
| `workload.image`, `workload.port` | `container.image`, and the first of `container.ports`; a workload with no ports makes `workload.port` an error naming the workload. Like every expression result it is text (`"8080"`): Pulumi config values are strings, and the template's `Integer` input parses it. Meant for runtime mappings; the graph carries both (`WorkloadImage`, `WorkloadPort`), so the orchestrator's second pass evaluates them like the first |
| `resource.<id>.<export>` | reference to a requirement's export: **pending** unless the caller knows its value. Read only by the `runtime` node (its workload variables); an error in any other node |
| `runtime.<output>` | **pending** reference to the runtime node (an edge to it); an environment definition has no runtime, so there it is an error |
| `<node>.<output>` | **pending** reference to an output of a node in scope; unknown node is an error |

**Two phases.** An expression evaluates to `Resolved(value)`, or to `Pending(original, references)` when it needs
values that exist only at deploy time. `references` is the set of `(node, output)` or `(resource id, export)` pairs
it waits on; the graph builder turns them into edges. A reference the context already knows resolves at once, so
the orchestrator re-runs the same evaluator with the stack outputs it has collected, and `Pending` becomes
`Resolved`. A string is pending when any expression in it is; a function is pending when any argument is.
Every invalid expression in a string is reported (file, location, message) and the string yields no result. The evaluator also reports the `env.<path>` values a string read, because a resolved value no longer shows where it came from; the grant check ([Engine rules](#engine-rules-generic-code-written-once)) needs them.

**Built-ins** (the only functions; an unknown function or wrong arity is an error):

- `name('<kind>')` applies the `Naming` rule for the kind: substitute `{workload}`, `{id}` (the requirement's
  effective id, or the node name for nodes not tied to a requirement), `{env}` and `{hash}`; lowercase; drop
  characters not matching `allowed`; if longer than `maxLength`, keep the first `maxLength - 6` characters and append
  the hash (so `allowed` must accept every hex digit `0-9a-f`; the loader enforces it). `{hash}` is the first 6 lowercase hex characters of SHA-256 over `workload|env|id|kind`. An unknown kind
  is an error.
- `guid(arg, …)` is a UUIDv5 (RFC 4122) of the arguments joined with `|`, in the fixed namespace
  `8d6c1f0e-5b3a-4c7e-9a21-7e4f0b2d6c35`. Changing the namespace changes every generated id.

## Engine rules (generic code, written once)

| Rule | Behaviour |
|---|---|
| Matching | A mapping matches when every `match` key equals the requirement's value. A requirement provides only `type`, `class` (default `standard`) and `tier` (from the environment); a mapping using any other key (`kind`, `runtime`) never matches a requirement. Specificity = number of `match` keys. A tie at the top is an error naming the tied files, never file order; no match is an error naming the requirement's type, class and tier. **Runtime:** each workload gets exactly one runtime mapping by the same rules, providing only `kind: runtime` and `tier`; a tie names the files, none names the tier; an environment definition (substrate owner) gets none. |
| Type contract | A mapping's `exports` keys must equal its type's `exports` exactly; the catalog loader rejects a mismatch (missing or extra) at the mapping file, location `exports`. |
| Expansion | Each node of the selected mapping becomes an expanded node (name, template, kind, config); config strings and mapping `exports` are evaluated with the effective requirement id as the current id and the mapping's node names in scope, other scalars pass through, objects and arrays recurse, and catalog tokens are cloned. Result per requirement: id, type, class, mapping file, nodes, exports (`Resolved` or `Pending`). Errors from all requirements are collected; a requirement with an error is left out of the result. |
| Policy scope | **Node scope:** `match` uses only `type`, `class`, `tier`, `template`; a policy applies to a node when every key equals the node's value (`type`/`class` from its requirement, `tier` from the environment, `template` from the node). A node added by a policy has no type or class. All matching policies apply (no specificity). A `match` with `kind` or `runtime` is never node scope. **Workload scope:** `match` has `kind: runtime`, optionally `runtime` and `tier`; its `add` creates its nodes once per workload, and its `set`/`default` entries target `runtime.<path>`, a field of the runtime node (`runtime.variables.<NAME>` sets or defaults one workload variable), with the existing layer, conflict and provenance rules: `set` wins over a workload variable of that name, `default` only fills a variable the workload does not define. Their values may read the nodes workload-scope policies add (`${appinsights.connectionString}`), which makes the runtime node wait for them; a value reading `${resource.…}` stays pending until the second pass even when the export is static (only the workload variables see static exports resolved at expansion). The `runtime` key is not matched until a workload names a runtime. The catalog loader rejects a policy that fits neither scope, since it would silently do nothing: `runtime` without `kind: runtime`; a `kind` other than `runtime`; `kind: runtime` together with `type`, `class` or `template`; `add` on a node-scope policy; a workload-scope `set`/`default` path not starting with `runtime.` (a bare `runtime` with no dot is rejected too). |
| Single pass | Requirement nodes, the runtime mapping's nodes and workload-scope added nodes receive node-scope policies once; an added node never triggers further adds. Added nodes are evaluated with their own name as the current id and the same policy's added nodes in scope; so is every runtime mapping node (the second pass uses the node's own name too, so `name()` agrees between the passes). |
| Layering | Template default (not known to the resolver) < mapping < team override (only `overridable` fields, validated; post-MVP) < policy `set`. A policy `default` fills a field only when neither the mapping nor any `set` provides it. Fixes the old algorithm applying overrides after policies. |
| Policy paths | `set`/`default` keys are dotted paths into a node's config; missing intermediate objects are created; a parent that exists but is not an object is an error. Values are evaluated like mapping config. A `set` of an object replaces the whole subtree: the old leaves' provenance is dropped and the policy becomes the source of every leaf of the new value. Config keys that contain `.` cannot be addressed by a policy path. |
| Policy conflicts | Within the `set` layer, and separately within `default`, these are errors naming the policies (and their files): two policies giving the same field of the same node different values (compared as written); two policies whose paths overlap, where one is a strict prefix of the other (`a` and `a.b`), regardless of values, because the result would depend on application order. The same value on the same path is fine; provenance names the first policy in name order. Inside a single policy, a `set` or `default` path that is a strict prefix of another of its own paths (`a: {b: 1}` with `a.b: 2`) is rejected at load for the same reason. Two workload-scope policies adding a node with the same name is also an error, as is one adding a node the runtime mapping declares. A requirement dropped by expansion keeps its errors in the policy result. |
| Provenance | Every leaf config field of every node carries `{source file, rule, layer}`: rule is the mapping file or the policy `name`; layer is `mapping`, `policy-set`, `policy-default` or `policy-add`. Keyed by dotted path; an array is one leaf. The result carries the catalog `version`. The result is usable only when its error list is empty. |
| References | Static values resolve immediately. `${node.output}` stays a pending reference ([Expressions](#expressions)) and becomes a graph edge. |
| Phases | Derived from edges, never listed per template: `runtime` for the runtime node itself; `after-runtime` when a node depends on the runtime node (it references `runtime.*`) directly or through a node that does; `infrastructure` otherwise. |
| Built-ins | `name(kind)` and `guid(...)` as defined in [Expressions](#expressions) ([C14](../open-questions.md)). Role GUIDs come from `roles.yaml`. Nothing else. |
| Security | References may target only the workload's own nodes and `env.*` resources the [environment descriptor](#environment-descriptor) lists as grantable. Cross-workload references are rejected by the engine. The graph builder enforces it statically ([D19](../open-questions.md)): every `env.<path>` a `grant` node's config reads, including inside functions and added or overridden by a policy, must be in `grantable`, otherwise an error names the node, the field and the path. Other nodes may read any `env.*` value. **Only the node named `runtime` of the runtime mapping may read `${resource.<id>.<export>}`** (its workload variables), and it is never a grant. Every other node (a requirement's, a policy-added one, grant or not, another node of the runtime mapping, a grant there) gets an error naming the node and field, whether the export is static or pending: requirement scopes must not read each other, and an export can carry `env.*` values the grantable check on a grant's own fields would not see. A mapping's `exports` cannot contain `${resource.…}` either (the catalog loader rejects it at `exports.<name>`); an export may read `env.*`, its own nodes' outputs and `runtime.*`. Edges in a requirement's scope come only from references to nodes in the same scope. |

## Output: ResolvedGraph

`GraphBuilder` turns the policy result into the graph (`jd.resolver.graph`). Pure and deterministic: the same inputs
give a byte-identical graph. It carries the catalog version, environment name, workload name, the nodes, and each
requirement's exports (by requirement id in ordinal order, `Resolved` or `Pending`). Its `Errors` are the policy step's errors plus
graph errors; the graph is usable only when `Errors` is empty.

Per node:

| Field | Meaning |
|---|---|
| id | `<workload>/<env>/<scope>/<node>`. Scope is the requirement's effective id, or `@workload` for the runtime mapping's nodes and the nodes added by workload-scope policies (requirement ids cannot contain `@`); the runtime node is `<workload>/<env>/@workload/runtime`. |
| stack | The id with `/` replaced by `.` and `@` by `_` (Pulumi stack names allow `[A-Za-z0-9_.-]`), for example `shop.dev.orders.database` and `shop.dev._workload.appinsights`: one backend stack per node, so workloads never share a stack. Unique by construction: ids contain no `.` or `_`, and `_workload` cannot be a requirement id. Node names (mapping `nodes:` keys and policy `add:` keys) must match `^[a-z]([a-z0-9-]*[a-z0-9])?$`; the catalog loader enforces it in code, since the schema validator ignores `propertyNames`. Length limits of the backend are the adapter's concern. |
| scope, name, template, kind | As expanded (`kind`: `create` or `grant`). |
| config, provenance | Values (`Resolved` or `Pending`) and where each leaf came from. |
| depends-on | Ids of nodes in the same scope referenced by a pending `${node.output}`, the runtime node's id when the config references `runtime.*`, and, for the runtime node reading `${resource.<id>.<export>}`, the nodes that export waits on (its own `${node.output}` references, in the requirement's scope); sorted. |
| phase | `infrastructure`, `runtime` or `after-runtime`, derived (see [Engine rules](#engine-rules-generic-code-written-once)). |
| probe | On the runtime node only: the runtime mapping's `probe` (`path`, `expectedStatus`). Not part of the hash. |
| hash | Lowercase hex SHA-256 of the UTF-8 bytes of the JSON object `{"template":…,"kind":"create"\|"grant","config":…}` in that key order, written by Newtonsoft `JToken.ToString(Formatting.None)` with default string escaping; config is the canonical form below. YAML date-like values stay strings, so the hash does not depend on the machine's time zone. |

**Canonical config** (`ConfigJson`): objects with keys in ordinal order, arrays in order, non-string scalars as
written, a resolved string as its value, a pending string as its original text, an `fn::entries` map as its sorted list of `{name, value}` items. Changing this form changes every
hash, so it is part of the contract. The hash lets the orchestrator skip unchanged stacks ([H47](../open-questions.md)).

**Order:** topological (dependencies first), always taking the smallest ready id, so it never depends on input order.
A cycle is an error naming the nodes in it (`a -> b -> a`); a node referencing itself is a cycle.

- Preview fills references to existing nodes from the last deployment's stack outputs; genuinely new
  values are marked `unknown`, never faked.
- Not yet in the graph: protection class ([ADR 0010](../decisions/0010-replacement-protection-classes.md)) and the
  template content digest, which belong to the slices that need them.

## Catalog CI — what makes it maintainable

1. **Static checks:** every type has a mapping per environment tier; no ambiguous matches; every
   `${…}` resolves to a real template config key or output; required template config supplied;
   mapping exports equal the type's exports exactly; naming fits the longest legal workload name.
   The template part is implemented offline by `TemplateLibrary.Check` in `jd.bp.pulumi` (templates are
   Pulumi-specific, so the resolver stays backend-agnostic). It checks a `ResolvedGraph` against the template
   directory: every node's template exists; every config key a node sets is declared in the template's
   `configuration:`; every input without a default is set; every `${node.output}` reference (in node config and in
   exports) names an output the referenced node's template declares. Errors carry node id, template and key. The
   seed catalog and sample workload are checked in the test suite.
2. **Golden tests:** sample workloads resolved and snapshotted; PR diffs show resolution changes.
3. **Fleet dry-run:** resolve every registered workload against the PR's catalog and report which change.
4. **`pulumi preview` in a sandbox** for changed templates.
5. **`jd resolve --explain`:** provenance rendered for teams asking why a setting has its value.

Adding a resource type = one type file + one template + one mapping + one golden test.

## Engine components (C#, backend-agnostic, ~1–1.5k LOC)

`CatalogParser` + `CatalogDirectory` (parser over in-memory files, thin directory reader; reuse `YamlSchemaValidator`) · `Matcher` · `Expander` · `PolicyApplier` (policies, layering, provenance) ·
`ExpressionEvaluator` · `Namer` · `GraphBuilder` (cycle detection, topological sort, phases).
`Resolver.Resolve(workload, file, catalog, environment)` runs expand → policies → graph in one call (used by the CLI and the orchestrator). The orchestrator (`jd.orchestrator`) evaluates pending config again with deployed outputs: see [provisioning.md](provisioning.md#orchestrator).
`TemplateLibrary` (in `jd.bp.pulumi`) checks the graph against the templates. `DeploymentPackage` carries the stack name (`StackName`) separately from the template (`DeploymentContent`).

## Alternatives considered

- **CUE / KCL instead of custom YAML.** CUE unification would detect conflicting policies natively,
  but it is a new language for a small team. Revisit if the format starts growing logic.
- **One generated Pulumi program per workload.** Fewer stacks and faster, but rejected by
  [ADR 0003](../decisions/0003-pulumi-yaml-template-library.md); per-node hash skipping covers most of
  the cost.

## Dependencies

- Workload schema changes ([H50](../open-questions.md)): `id`, `class`, drop `metadata.environment`;
  rename `database` to protocol-named types.
- **Environment descriptor:** defined above; the substrate publishes it from its deployment outputs (S12).
- [D18](../open-questions.md): SQL/Postgres grants need data-plane SQL, not ARM — modelled as a
  `grant` node using a job-backed template; where that job runs is open.
