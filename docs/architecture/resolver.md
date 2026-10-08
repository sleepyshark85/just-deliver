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

The catalog is versioned as one unit; a deployment records the catalog version next to the
definition SHA ([ADR 0007](../decisions/0007-definition-upload-snapshot.md)).

## Catalog file kinds

Each kind has a JSON Schema, so editors and CI validate it.

### Type — the contract teams bind to

Source of the workload schema's `type` enum (generated, not hand-maintained). Names the interface
the code binds to (`cosmos-sql`, `postgres`), never an abstraction like `database` (C52 rule).

```yaml
kind: ResourceType
name: cosmos-sql
classes: [standard]
exports: [endpoint, database, container]      # the only valid ${resource.<id>.*}
overridable:
  throughput: { type: integer, min: 400, max: 4000, target: account.throughput }
```

### Mapping — requirement → graph nodes

Selected by matching criteria, never by conditionals.

```yaml
kind: Mapping
match: { type: cosmos-sql, class: standard }
nodes:
  account:
    template: azure/cosmos-account
    config:
      resourceGroupName: ${env.resourceGroup}
      location: ${env.region}
      accountName: ${name('cosmos-account')}
      throughput: 400
  access:
    kind: grant                                   # surfaced in the approval diff
    template: azure/cosmos-sql-role-assignment
    config:
      accountId: ${account.accountId}
      principalId: ${runtime.principalId}         # this reference alone orders it after the revision step
      roleDefinitionId: ${account.accountId}/sqlRoleDefinitions/${role.cosmos-data-contributor}
      roleAssignmentId: ${guid(account.accountId, runtime.principalId, 'data-contributor')}
exports:
  endpoint: ${account.documentEndpoint}
```

### Policy — IT Ops layer

Can `set` fields (wins over everything), fill `default`s, or `add` nodes.

```yaml
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
---
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

## Engine rules (generic code, written once)

| Rule | Behaviour |
|---|---|
| Matching | Specificity = number of matched criteria. A tie is an error, never file order. |
| Layering | Template default < mapping < team override (only `overridable` fields, validated) < policy `set`. `default` only fills gaps. Fixes the old algorithm applying overrides after policies. |
| Provenance | Every config field carries `{value, source file, rule, catalog version}`. |
| References | Static values resolve immediately. `${node.output}` stays a typed reference and becomes a graph edge. |
| Phases | Derived from edges, not a hard-coded list: anything referencing `runtime.*` lands after the revision step. |
| Built-ins | `name(kind)` applies `naming.yaml` (pattern, max length, charset, hash suffix — [C14](../open-questions.md)). `guid(...)` is UUIDv5, deterministic and re-run safe. Role GUIDs come from `roles.yaml`. Nothing else. |
| Security | References may target only the workload's own nodes and `env.*` resources the environment descriptor marks grantable. Cross-workload references are rejected by the engine. |

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
   exports cover the type contract; naming fits the longest legal workload name.
2. **Golden tests:** sample workloads resolved and snapshotted; PR diffs show resolution changes.
3. **Fleet dry-run:** resolve every registered workload against the PR's catalog and report which change.
4. **`pulumi preview` in a sandbox** for changed templates.
5. **`jd resolve --explain`:** provenance rendered for teams asking why a setting has its value.

Adding a resource type = one type file + one template + one mapping + one golden test.

## Engine components (C#, backend-agnostic, ~1–1.5k LOC)

`CatalogLoader` (reuses `YamlSchemaValidator`) · `Matcher` · `Expander` · `Merger` (provenance) ·
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
- **Environment descriptor:** the substrate must publish its resources (resource group, region,
  workspace, shared servers, tier, grantable resources) as versioned data. Not yet built.
- [D18](../open-questions.md): SQL/Postgres grants need data-plane SQL, not ARM — modelled as a
  `grant` node using a job-backed template; where that job runs is open.
