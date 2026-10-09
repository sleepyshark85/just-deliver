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

## Current state (to be replaced)

There is no resolver. `samples/provisioner/Program.cs` is a hand-wired script; `workload.yaml` is
never read. Defects found in review that the design must remove:

| # | Defect | Location |
|---|---|---|
| 1 | Stack name = template name (`cosmos-db`), so every workload would share one stack | `PulumiBackendProvider.PrepareStackAsync` |
| 2 | Fixed role-assignment GUIDs collide when a second workload targets the same scope | `cosmos-db-access`, `role-assignment` defaults; `Program.cs` |
| 3 | `${resource.database.endpoint}` matches nothing — template exports `documentEndpoint`; no type contract | `workload.yaml` vs `cosmos-db/Pulumi.yaml` |
| 4 | `enableFreeTier: true` default — one free-tier account per subscription | `cosmos-db/Pulumi.yaml` |
| 5 | Preview feeds `"<unknown-until-deployed>"` strings into downstream stacks | `GetChainedValue` |
| 6 | Resource list, order, names, region, role GUIDs, output wiring and Azure-specific grant logic hard-coded | `Program.cs` |
| 7 | Platform opinions (SKU `F1`, region, free tier) live in template defaults, duplicated in `Pulumi.default.yaml` | templates |

## Catalog layout

```
catalog/
  catalog.yaml                               # catalog version — recorded on every deployment
  types/cosmos-sql.yaml                      # contract teams bind to
  mappings/cosmos-sql/standard.yaml          # type + class → nodes
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
| `Mapping` | `match`, `nodes`, `exports` | `match.type`, when present, names a declared type; `exports` keys equal the type's `exports` |
| `Policy` | `name`, `reason`, `match`, and at least one of `set` / `default` / `add` | `name` unique |
| `Naming` | `rules` (resource kind → `pattern`, `maxLength`, `allowed`) | files merge; a rule is defined once; `allowed` must be a valid regex character class that accepts every hex digit `0-9a-f` |
| `Roles` | `roles` (name → role-definition GUID) | files merge; a role is defined once |

A node is `{ template, kind?: create | grant, config }`; `kind` defaults to `create`. Mapping `match`
keys are `type`, `class`, `tier`, `kind`, `runtime`; a policy may also match on `template`.
Naming `pattern` placeholders are `{workload}`, `{id}`, `{env}` and `{hash}`. The loader collects
every error (file and location inside it) in one pass.

### Type — the contract teams bind to

Source of the workload schema's `type` enum (generated, not hand-maintained). Names the interface
the code binds to (`cosmos-sql`, `postgres`), never an abstraction like `database` (C52 rule).

```yaml
kind: ResourceType
name: cosmos-sql
description: A Cosmos DB database with one container, accessed through the SQL (NoSQL) API.
classes: [standard]
exports: [endpoint, database, container]      # the only valid ${resource.<id>.*}
```

Team overrides are out of the MVP, so the format has no `overridable` block yet.

### Mapping — requirement → graph nodes

Selected by matching criteria, never by conditionals.

```yaml
# mappings/cosmos-sql/standard.yaml (the seed catalog)
kind: Mapping
match: { type: cosmos-sql, class: standard }
nodes:
  database:
    template: azure/cosmos-sql-database
    config:
      resourceGroupName: ${env.resourceGroup}
      accountName: ${env.cosmos.accountName}
      databaseName: ${name('cosmos-database')}
      # Free tier shares 1,000 RU/s across the subscription; each database stays at 400 or below.
      throughput: 400
  container:
    template: azure/cosmos-sql-container
    config:
      resourceGroupName: ${env.resourceGroup}
      accountName: ${env.cosmos.accountName}
      databaseName: ${database.databaseName}
      containerName: ${name('cosmos-container')}
      partitionKeyPath: /id
  access:
    kind: grant                                   # surfaced in the approval diff
    template: azure/cosmos-sql-role-assignment
    config:
      accountId: ${env.cosmos.accountId}
      principalId: ${runtime.principalId}         # this reference alone orders it after the revision step
      roleDefinitionId: ${env.cosmos.accountId}/sqlRoleDefinitions/${role.cosmos-data-contributor}
      roleAssignmentId: ${guid(env.cosmos.accountId, runtime.principalId, 'data-contributor')}
exports:
  endpoint: ${env.cosmos.endpoint}
  database: ${database.databaseName}
  container: ${container.containerName}
```

The Cosmos account is a substrate resource (one free-tier account per subscription), so the mapping reads it from `${env.cosmos.…}` and creates only the workload's database and container plus the grant.

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

### Template — existing Pulumi YAML, mechanics only

- No platform opinions in defaults (SKU, region, free tier move to mappings/policies).
- No fixed GUIDs; the resolver supplies deterministic ones.
- `options: protect: true` per protection class ([ADR 0010](../decisions/0010-replacement-protection-classes.md)).
- The template's own `configuration:` and `outputs:` blocks are its interface; no extra metadata.

### Runtime is a mapping too

`match: { kind: runtime, runtime: container-app }` produces the Container App revision
([ADR 0013](../decisions/0013-container-apps-runtime.md)). The app is not special-cased in the engine.

## Environment descriptor

An environment publishes what the resolver needs as one YAML document, validated by
[`schemas/environment.schema.json`](../../schemas/environment.schema.json) (embedded in `jd.resolver`).
It is written from substrate deployment outputs (slice S12); the resolver only reads it.

```yaml
kind: Environment
name: dev                 # same pattern as workload ids
region: southeastasia
tier: team                # team | protected
values:                   # substrate outputs, addressable as ${env.<path>}
  resourceGroup: rg-…
  cosmos: { accountName: …, accountId: …, endpoint: … }
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

## Expressions

Config values, exports and workload variables are strings that may mix text and `${…}` expressions
(`${env.cosmos.accountId}/sqlRoleDefinitions/${role.cosmos-data-contributor}`). Non-string values are never
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
| `resource.<id>.<export>` | **pending** reference to a requirement's export |
| `runtime.<output>` | **pending** reference to the runtime node |
| `<node>.<output>` | **pending** reference to an output of a node in scope; unknown node is an error |

**Two phases.** An expression evaluates to `Resolved(value)`, or to `Pending(original, references)` when it needs
values that exist only at deploy time. `references` is the set of `(node, output)` or `(resource id, export)` pairs
it waits on; the graph builder turns them into edges. A reference the context already knows resolves at once, so
the orchestrator re-runs the same evaluator with the stack outputs it has collected, and `Pending` becomes
`Resolved`. A string is pending when any expression in it is; a function is pending when any argument is.
Every invalid expression in a string is reported (file, location, message) and the string yields no result.

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
| Matching | A mapping matches when every `match` key equals the requirement's value. A requirement provides only `type`, `class` (default `standard`) and `tier` (from the environment); a mapping using any other key (`kind`, `runtime`) never matches a requirement. Specificity = number of `match` keys. A tie at the top is an error naming the tied files, never file order; no match is an error naming the requirement's type, class and tier. |
| Type contract | A mapping's `exports` keys must equal its type's `exports` exactly; the catalog loader rejects a mismatch (missing or extra) at the mapping file, location `exports`. |
| Expansion | Each node of the selected mapping becomes an expanded node (name, template, kind, config); config strings and mapping `exports` are evaluated with the effective requirement id as the current id and the mapping's node names in scope, other scalars pass through, objects and arrays recurse, and catalog tokens are cloned. Result per requirement: id, type, class, mapping file, nodes, exports (`Resolved` or `Pending`). Errors from all requirements are collected; a requirement with an error is left out of the result. |
| Layering | Template default < mapping < team override (only `overridable` fields, validated; post-MVP) < policy `set`. `default` only fills gaps. Fixes the old algorithm applying overrides after policies. |
| Provenance | Every config field carries `{value, source file, rule, catalog version}`. |
| References | Static values resolve immediately. `${node.output}` stays a pending reference ([Expressions](#expressions)) and becomes a graph edge. |
| Phases | Derived from edges, not a hard-coded list: anything referencing `runtime.*` lands after the revision step. |
| Built-ins | `name(kind)` and `guid(...)` as defined in [Expressions](#expressions) ([C14](../open-questions.md)). Role GUIDs come from `roles.yaml`. Nothing else. |
| Security | References may target only the workload's own nodes and `env.*` resources the [environment descriptor](#environment-descriptor) lists as grantable. Cross-workload references are rejected by the engine. |

## Output: ResolvedGraph

Per node: stable id (`workload/env/requirementId/nodeName`), **stack name**, template reference +
content digest, config (values and typed references, with provenance), protection class, kind
(`create` | `lookup` | `grant`), edges. Plus the workload's resolved exports for env-var substitution.

- Hash of template + resolved config per node lets the orchestrator skip unchanged stacks
  ([H47](../open-questions.md)).
- Preview fills references to existing nodes from the last deployment's stack outputs; genuinely new
  values are marked `unknown`, never faked.

## Catalog CI — what makes it maintainable

1. **Static checks:** every type has a mapping per environment tier; no ambiguous matches; every
   `${…}` resolves to a real template config key or output; required template config supplied;
   mapping exports equal the type's exports exactly; naming fits the longest legal workload name.
2. **Golden tests:** sample workloads resolved and snapshotted; PR diffs show resolution changes.
3. **Fleet dry-run:** resolve every registered workload against the PR's catalog and report which change.
4. **`pulumi preview` in a sandbox** for changed templates.
5. **`jd resolve --explain`:** provenance rendered for teams asking why a setting has its value.

Adding a resource type = one type file + one template + one mapping + one golden test.

## Engine components (C#, backend-agnostic, ~1–1.5k LOC)

`CatalogParser` + `CatalogDirectory` (parser over in-memory files, thin directory reader; reuse `YamlSchemaValidator`) · `Matcher` · `Expander` · `Merger` (provenance) ·
`ExpressionEvaluator` · `Namer` · `GraphBuilder` (cycle detection, topological sort, phases).
`DeploymentPackage` needs a stack name separate from the template name.

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
