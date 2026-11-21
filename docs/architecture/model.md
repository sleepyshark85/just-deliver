# The model

How a workload definition becomes a graph of backend-renderable nodes: the workload, its requirements, the catalog's components, the
environment that offers them, policies and grants. Resolution is a pure function, the same inputs give a byte-identical graph, and no Azure
call is made:

```
resolve(workload, environment descriptor, catalog) → ResolvedGraph
```

All Azure knowledge is catalog data. The code knows the engine's concepts and never a file's fields; each file's shape is its schema, and
[documents.md](documents.md) says how schemas bind fields to concepts ([ADR 0019](../decisions/0019-engine-knows-concepts.md)). Field lists
live in the schemas; this page gives meaning and rules. Deploying the graph: [provisioning.md](provisioning.md).

## 1. Workload and requirements

A workload definition ([schema](../../schemas/workload.schema.json), [sample](../../samples/workloads/sample-app.yaml)) names the workload
and its team, its container (image, variables, ports) and what it `requires`. The same definition deploys to every environment; it names no
environment, region or size.

**A requirement** asks for a component by `type` and has two optional fields:

- **`id`**: the requirement's identity; the **effective id** is `id`, else `type`, and must be unique within the workload (two id-less
  requirements of one type fail with "set distinct ids"). An id is an identity, not a label: it is part of node ids, stacks and generated
  names, so renaming it means delete and create ([ADR 0011](../decisions/0011-requirement-id.md)). List position never matters.
- **`class`**: the operational shape (default `standard`), which selects the class part inside the component's implementation (section 3).

**References.** A variable's value is text that may contain only `${resource.<id>.<export>}`: an export of the requirement with that
effective id. Any other `${…}` in a variable is an error naming it (boundary 6), and so is an id no requirement declares; both are checked by
`WorkloadRules` before resolution, the export name at expansion. The export is the component's contract, so `${resource.database.endpoint}`
means the same in every environment.

**What teams do not specify:**

| Concern | Comes from |
|---|---|
| Engine, SKU, size, throughput, replicas | The component's implementation for the environment's type (section 3) |
| Compute | The runtime the environment offers ([ADR 0018](../decisions/0018-environment-offers-runtime.md)) |
| Identity and grants | The runtime's system-assigned identity ([ADR 0012](../decisions/0012-system-assigned-identity.md)) and the grant nodes of the component (section 7) |
| Resource names | The catalog's naming rules (`name()`, section 6) |
| Platform rules | Policies (section 7); hard guardrails in the landing zone ([ADR 0008](../decisions/0008-enforcement-in-landing-zone.md)) |
| Sign-off | The environment's approval block ([approvals.md](approvals.md)) |

What a team gets, it declares: a policy may *require* a component (monitoring today) but never adds one, and the workload maps the export
to its own variable (`APPLICATIONINSIGHTS_CONNECTION_STRING: ${resource.monitoring.connectionString}`).

## 2. The catalog

The catalog is a directory of YAML files, one document per file, versioned as one unit; every graph and record carries its version
([ADR 0007](../decisions/0007-definition-upload-snapshot.md)). The loader reads every `*.yaml` outside `templates/`, picks each file's
schema by its `kind` and collects every error (file and location) in one pass; directories are organisational only.

| Kind | What it is | Seed |
|---|---|---|
| `Catalog` | The version and the environment types; exactly one | [catalog.yaml](../../catalog/catalog.yaml) |
| `Component` | A contract a workload requires by name, and its implementations | [components/](../../catalog/components/) |
| `Runtime` | The compute every workload gets | [runtimes/](../../catalog/runtimes/) |
| `SharedResource` | A platform-owned part other parts read | [shared/](../../catalog/shared/) |
| `Policy` | An IT Ops rule | [policies/](../../catalog/policies/) |
| `Naming` | Name rules per resource kind (`pattern`, `maxLength`, `allowed`) | [naming.yaml](../../catalog/naming.yaml) |
| `Roles` | Role name → role-definition GUID | [roles.yaml](../../catalog/roles.yaml) |
| `Grants` | Grant template → the input naming its target | [grants.yaml](../../catalog/grants.yaml) |

Schemas: [`schemas/catalog/`](../../schemas/catalog/). `Naming`, `Roles` and `Grants` files merge, each entry defined once. Components,
runtimes and shared resources are **part owners**: one namespace, the requirement-type pattern, and none may be named after an expression
root (`env`, `role`, `workload`, `resource`, `runtime`).

**Templates** (`templates/<provider>/<name>/Pulumi.yaml`; a node's `template` is `<provider>/<name>`) are mechanics only: their
`configuration:` and `outputs:` are the interface; no defaults, no platform opinions (SKUs, regions, free-tier flags, names), no fixed GUIDs
(the catalog supplies `guid()` ids and role GUIDs). A template that needs part of an ARM id takes the id and splits it. List-shaped inputs are
built by the engine (a template cannot reshape a map). `TemplateLibrary.Check` checks every graph against the templates before anything is
deployed: each template exists, each config key is declared, each input without a default is set, and each `${node.output}` names a declared
output.

## 3. Parts, components and runtimes

A **part** is a set of **nodes** with one scope, plus the **exports** evaluated from them. A node is `template`, `kind` (`create`, the
default, or `grant`) and `config` ([part schema](../../schemas/catalog/part.schema.json)). Node names are unique across the parts that share a
scope, and no node takes a name its part reads as a root, nor a shared resource's name: a node is read before a part of that name, so it would
silently replace what `${<resource>.<export>}` reads. Parts are the shared resources, a component's `environment` part and class parts,
and the runtime.

**Component** ([database](../../catalog/components/database.yaml), [monitoring](../../catalog/components/monitoring.yaml)). Its `exports`
list is the contract; `implementations` is keyed by a platform ([ADR 0021](../decisions/0021-platform-dimension.md)), and each platform
entry holds the component's `uses` on that platform and `types`, keyed by an environment type (of the `environment` layer) or `default`.
Selection is exact lookup, never matching: the platform entry equal to the environment's platform, with no fallback, so one platform's
implementation never reaches another's environment; then the `types` key equal to the environment's type, else `default`; a miss at either
is an error naming the component and the value (a types miss, the platform too). An implementation replaces `default` whole. Inside it,
`classes` is selected the same way by the requirement's class; every implementation declares the same classes, and every class part exports
exactly the contract (load errors otherwise). An implementation may also have an `environment` part, deployed once per environment that
offers the component, which its class parts read by the component's name (`${database.databaseName}`). The implementation chooses the
engine; an `engine` export tells the workload's code which one it got.

**Runtime** ([container-app](../../catalog/runtimes/container-app.yaml)). Not a component: no contract, no classes, never required. Its
`implementations` are keyed by platform, then type, as a component's. Each implementation has one part whose nodes include the node named
`runtime` (the workload's compute, [ADR 0013](../decisions/0013-container-apps-runtime.md)), a `probe` and a `release` block that the
release flow reads ([provisioning.md](provisioning.md#orchestrator)). The `runtime` node must not set the two inputs `release` names (the
flow sets them), the probe interval is at most its timeout, and the revision suffix's parts fit its declared `maxLength`. Only a runtime
declares a node named `runtime`. The workload's variables reach it through the config form `{ fn::entries: workload.variables }`, valid only
as a top-level field of the `runtime` node; the node deploys them as one list ([provisioning.md](provisioning.md#orchestrator), Config
entries), while the graph keeps one leaf per variable (`variables.<NAME>`) with its own provenance.

**Shared resource** ([shared/](../../catalog/shared/)). One part with a `platform`, a `scope` (`shared` or `environment`), `uses`, nodes and
exports; it has no implementations, so it does not vary by environment type. Only parts of its platform may use it (a load error otherwise),
so an environment reaches only its own platform's shared resources.

## 4. Scopes, layers and `uses`

| Scope | Deployed by | Once per | Parts |
|---|---|---|---|
| `shared` | `jd env up` of a definition whose type has `layer: shared` | shared layer (a subscription today) | shared resources of scope `shared` |
| `environment` | `jd env up` of a definition whose type has `layer: environment` | environment | shared resources of scope `environment`; offered components' `environment` parts |
| workload | `jd deploy`, `jd release deploy`, `jd promote` | workload | the class part of each requirement; the runtime |

The layers and their deploy order are the `layer` enum of the jd-shipped part schema ([documents.md](documents.md#3-annotations-and-the-binder));
workloads deploy in a scope that is no layer. **`uses`** (on components, runtimes and shared resources) lists shared resources only: a resource uses only resources
of its own or an earlier layer, and the `uses` graph has no cycles. A workload deploy never expands a shared resource or an `environment`
part, and an environment never deploys a resource of an earlier layer.

## 5. Environments

**Environment types** are declared once in [catalog.yaml](../../catalog/catalog.yaml): each has a `layer` and optionally `approval: true`.
So are the **platforms** (`platforms`). A definition or descriptor of an undeclared type or platform is an error naming the declared ones,
and one whose type has `approval: true` must carry an `approval` block (`EnvironmentTypeCheck`, run before anything resolves or deploys).
Any type may carry a block; the block is what gates.

**Environment definition** ([schema](../../schemas/environment-definition.schema.json), [samples](../../samples/environments/)): name,
region, type, platform, `offers` (component and runtime names, at most one runtime) and optional `approval`. `jd env up` resolves what the
offers reach in the type's layer and deploys it:

| Layer | Deploys | Must already exist |
|---|---|---|
| `shared` | the `shared`-scope resources the offered components and runtime reach through `uses` | — |
| `environment` | the offered components' `environment` parts, and the `environment`-scope resources the offers reach | every reached `shared`-scope resource, in the base descriptor (`--base`), which must be of the same platform; otherwise an error before anything is created |

The substrate follows from the offers: nothing offered uses a resource, it is not deployed. An offer reaches what its platform entry uses. A
part already in the base's values is an error (an environment adds new parts only). All parts of one run expand into one scope, `substrate`;
the owner is `@<name>` (a workload name cannot start with `@`), so ids, stacks and `name()` hashes never collide with a workload's, and it
has no runtime and no team.

**Environment descriptor** ([schema](../../schemas/environment.schema.json)): what `jd env up` writes and every resolution reads. `name`,
`region`, `type`, `platform` and `offers` come from the definition (a descriptor of an earlier layer offers nothing, so no workload deploys
on it); `values` holds each deployed part's exports keyed by part, `values.<part>.<export>` (`values.env-group.name`), merged with the
base's values; `approval` is copied. Values have no dotted keys and no key `name`, `region` or `type`. An export that reads a secret output,
directly or through another part, has no value, so `env up` fails and writes nothing (boundary 12).

**Offers at resolve.** A requirement's component must be among the descriptor's `offers` (`environment 'dev' does not offer database (offers: …)`, the offered names listed); the
workload's runtime is the one runtime offered, and none offered is an error for every workload.

## 6. Expressions

Config values, exports and variables are strings mixing text and `${…}` expressions; other scalars are never touched. No escaping, no
operators:

```
expression := path | function '(' [ argument { ',' argument } ] ')'
argument   := path | "'" literal "'"          # a literal has no quote; valid only as an argument
path       := segment { '.' segment }          # letters, digits, '_' and '-'
```

| Root | Meaning | Where |
|---|---|---|
| `env.name`, `env.region`, `env.type` | the descriptor's own fields; any other `env.<path>` is an error (boundary 4) | everywhere |
| `role.<name>` | the role GUID from `Roles` | everywhere |
| `workload.name`, `workload.team` | the owner (`@<name>` and empty for an environment) | everywhere |
| `workload.image`, `workload.port` | the container image and the first port, as text | meant for runtimes; no port is an error |
| `<node>.<output>` | a node output of the same part: pending, an edge | everywhere |
| `<part>.<export>` | an export of a part named in `uses` or, in a class part, of its own component: evaluated in place when the part is deployed in the same run, else `values.<part>.<export>` of the descriptor | everywhere |
| `runtime.<output>` | an output of the runtime node: pending, an edge | workload parts |
| `resource.<id>.<export>` | an export of a requirement | the `runtime` node only (its variables) |

The loader checks every root a part reads: its own node, a name in its `uses`, its own component, or a reserved root, and `<export>` is
declared. A part never reads another component or runtime, and an `environment` part does not read its own component.

**Two phases.** An expression evaluates to `Resolved(value)` or to `Pending(text, references)` when it needs a node output that exists only
once deployed; a reference becomes a graph edge. The orchestrator evaluates the same text again with the outputs it has collected. A read of
the descriptor's values resolves at once and is recorded as a part read, which preview shows as `(from cosmos-account.id)`.

**Built-ins**, the only functions:
- `name('<kind>')` applies the `Naming` rule: substitutes `{workload}` (the owner's name; an environment's `@<name>` loses its `@` to `allowed`), `{id}` (the requirement's effective id; a runtime node's own
  name; `substrate` in an environment run), `{env}` and `{hash}`, lowercases, drops characters outside `allowed`, and above `maxLength` keeps
  the first `maxLength - 6` characters plus the hash. `{hash}` is the first 6 hex characters of SHA-256 over `workload|env|id|kind`, so
  `allowed` must accept every hex digit (checked at load).
- `guid(arg, …)` is a UUIDv5 of the arguments joined with `|`, in a fixed namespace; changing the namespace changes every generated id.

## 7. Policies and grants

**Policies** ([ADR 0017](../decisions/0017-catalog-layer-policies-require.md); [enforce-monitoring](../../catalog/policies/enforce-monitoring.yaml),
[limit-revisions](../../samples/walkthrough/policies/limit-revisions.yaml)) carry a `name`, a `reason`, a `match` and at least one of:

- **`set`**: dotted config paths → values; wins over every other layer.
- **`default`**: fills a field only where neither the implementation, the workload nor a `set` gives it.
- **`require`**: a component every matched workload must declare (any id, any class); a workload without it fails at its `requires`, naming
  the policy, its file and its reason. A policy never adds nodes.

Two scopes, and the loader refuses a policy that fits neither:
- **Node scope**: `match` uses `type`, `class`, `environmentType`, `template`; applies to every node whose values equal all keys (a runtime
  or substrate node has no type or class). All matching policies apply; there is no specificity.
- **Workload scope**: `match: { kind: runtime }`, optionally `environmentType` (`runtime` is reserved until a workload names a runtime). Its
  `set`/`default` paths start with `runtime.` and address the runtime node, `runtime.variables.<NAME>` one variable (as a string); only it
  may `require`.

**Layering**, lowest to highest: implementation, workload (variables), policy `set`; a policy `default` only fills gaps. A `set` of an object
replaces the subtree. Two policies giving one field different values, or overlapping paths (`a` and `a.b`), in the same layer are an error
naming both; so are overlapping paths inside one policy. **Provenance:** every config leaf records its source file, rule (component, runtime
or policy) and layer.

**Grant target rule** (boundaries 1, 2). A `kind: grant` node sits only in a class part or a runtime; its template is listed in
[grants.yaml](../../catalog/grants.yaml), which names the input holding its target; and that input is exactly `${<node>.<output>}` of a node of
the grant's own part or the runtime: no literal, no text around it, no function, no shared or environment resource, `env.*`, `resource.*` or
`role.*`. Its other inputs may read what its component uses (`accountId: ${cosmos-account.id}`). The loader checks the catalog, and the graph
builder checks again after policies, so no policy can point a grant elsewhere.

## 8. Security boundaries (D19)

A definition must not reach another workload's resources, and the environment's inputs stay the platform's
([D19](../open-questions.md)). Each boundary has negative tests.

| # | Boundary | Enforced by |
|---|---|---|
| 1 | A grant targets only a node of its own part or the runtime | loader (`GrantRules`); graph builder after policies |
| 2 | Only class parts and runtimes hold grants; a policy cannot add one | loader |
| 3 | A part reads only its nodes, its `uses`, its own component's environment part and reserved roots | loader (`ReadRules`) |
| 4 | `env.*` reads only `name`, `region`, `type` | evaluator |
| 5 | A class part does not read another component's environment part | loader |
| 6 | A workload variable reads only `${resource.<id>.<export>}` | `WorkloadRules`; unbound workload fields are inert |
| 7 | A workload deploy creates no shared or environment resource | expander |
| 8 | An environment never deploys a resource of an earlier layer | expander |
| 9 | Requirement scopes do not read each other; only the `runtime` node reads `resource.*`; no export reads `resource.*` | graph builder; loader |
| 10 | The owner prefix `@<name>` keeps environment and workload ids apart | expander |
| 11 | A requirement's component must be offered | expander |
| 12 | A secret output never reaches the descriptor (section 5) | `DescriptorComposer` |
| 13 | A type with `approval: true` is never written without its block | `EnvironmentTypeCheck` |

## 9. The resolved graph

`Resolver.Resolve` (a workload) and `Resolver.ResolveSubstrate` (an environment definition over its base) run the same three steps:
`Expander` (selection, evaluation), `PolicyApplier` (policies, layering, provenance, `require`) and `GraphBuilder` (edges, phases, hashes, the
grant check, order). Errors are collected, each `file: location: message`; the graph is usable only when `Errors` is empty. The graph carries
the catalog version, the environment and workload names, the nodes and each requirement's exports.

| Node field | Meaning |
|---|---|
| id | `<workload>/<env>/<scope>/<node>`; scope is the requirement's effective id, `@workload` for the runtime's nodes, `substrate` in an environment run (`@dev/dev/substrate/group`) |
| stack | The id with `/` → `.` and `@` → `_` (`shop.dev.orders.container`, `shop.dev._workload.runtime`): one backend stack per node, with the environment in the name; unique because ids contain no `.` or `_` |
| scope, name, template, kind | As expanded |
| config, provenance | Each leaf `Resolved` or `Pending`, and where it came from |
| dependsOn | Ids of the nodes its pending references wait on, sorted: same-scope nodes, the runtime node for `runtime.*`, and for the `runtime` node the nodes behind the exports its variables read |
| phase | Derived, never listed: `Runtime` for the runtime node; `AfterRuntime` when it depends on the runtime directly or transitively (the grants); `Infrastructure` otherwise |
| probe, release | The runtime's blocks, on the runtime node only; not hashed |
| hash | SHA-256 (lowercase hex) of the compact JSON `{"template", "kind", "config"}`, config in canonical form |

**Canonical config** (`ConfigJson`): object keys in ordinal order, arrays in order, non-string scalars as written, a resolved string as its
value, a pending one as its text, an `fn::entries` map as its sorted `{name, value}` list. Changing this form changes every hash, so it is
part of the deploy contract.

**Order:** topological, always taking the smallest ready id, so it never depends on input order; a cycle is an error naming its nodes.
Preview, the release record and approvals consume the graph before anything is applied ([release-record.md](release-record.md)).

| Concern | Code |
|---|---|
| Loading | `CatalogLoader`: `DocumentLoader`, `NameRules`, `Binder`, then `ReadRules`, `ScopeRules`, `GrantRules`, `ContractRules`, `PolicyRules`, `RolloutRules` |
| Environment | `EnvironmentParser`, `EnvironmentTypeCheck`, `DescriptorComposer` (`jd.resolver/environment`) |
| Workload | `WorkloadFile`, `WorkloadRules` (`jd.resolver/workload`) |
| Resolution | `Expander`, `ExpressionEvaluator` with `BuiltIns`, `PolicyApplier`, `GraphBuilder`, `ExportValues` |
| Templates | `TemplateLibrary` (`jd.bp.pulumi`) |
