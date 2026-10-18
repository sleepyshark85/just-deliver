# The code knows no file structure

> Status: design note for S24, **proposed phase 1.5** (between phase 1 and S22/phase 2; [roadmap](../plans/roadmap.md) change proposed in
> [section 9](#9-slices-and-exit-criteria)). Serves the non-negotiable "nothing hard-coded", principles 6–14 ([context.md](../context.md#design-principles)),
> N1 (one shape) and N10 (cheap for the platform team), and makes phase 2's "added as components only, no C# change" structural. The user
> (2026-10-19): *"in general, the code shouldn't know the structure of any yaml file"*; *"check the whole solution for hard-code like that"*;
> *"applying the quality bar along the refactoring"*. The meaning of the S21 model (kinds, scopes, offers, grants) does not change; only where
> that meaning lives does. The user's open choices: [section 10](#10-open-choices-for-the-user).

## 1. Problem

The C# mirrors every document format. Each file kind has a model (`Catalog`, `Component`, `EnvironmentDefinition`, `ApprovalRecord.Stored`,
…), a loader (`*File.cs`, `*Parser.cs`) returning its own result type (`*LoadResult`), and field names spelled in code
(`body["environmentTypes"]`, `workload["container"]["image"]`, locations like `$"implementations.{key}.classes"`). Appendix A lists every place,
in every project under `src/`. So a renamed field, a new kind or a new component shape is a C# change, and the schemas in `schemas/` are only
a first gate in front of a second, hand-written model of the same shape. Standards §1 even allows it ("Allowed in code: the catalog/schema
*formats*"); the user does not.

## 2. The line: what the engine core still knows

The engine knows **concepts**, never **fields**. A concept is something an algorithm consumes; a field is where a document keeps it. Schemas
(data) say which field holds which concept. The closed list:

| # | Concept | What the engine does with it | Why it cannot be data |
|---|---|---|---|
| 1 | **Document**: a file, its `kind`, its tree; errors at file + location | loads, picks the schema by `kind`, validates, locates errors | something must read a file before data can say anything; `kind` is the one bootstrap field |
| 2 | **Name and reference**: a namespace, a name defined in it, a reference to it; a requirement's effective id (its id, else its type, unique per workload, ADR 0011) | load integrity | it is the rule itself ("a reference must resolve"); data says which fields define and refer |
| 3 | **Part, node, export**: nodes (`template`, `kind` create\|grant, `config`) and the exports evaluated from them | expansion, graph, ids, stacks, hashes (H47), the backend package | the graph is the engine's output; its node shape and canonical hash are the deploy contract |
| 4 | **Expression**: the `${…}` grammar, its **closed root list** `env.name`, `env.region`, `env.type`, `workload.name`, `workload.team`, `workload.image`, `workload.port`, `role.<name>`, `resource.<id>.<export>`, `runtime.<output>`, `<node>.<output>`, `<part>.<export>`; the built-ins `name()`, `guid()`; the form `fn::entries`, whose items are `{name, value}` | two-phase evaluation, edges | a grammar is an interpreter. Binding a field to a role says where a root's value lives; **it never creates a root** (boundary 4) |
| 5 | **Scope**: the layers in deploy order, and the workload deploy | who deploys what (boundaries 7, 8), `uses` direction, in-run reads | ownership and deploy order are the engine's lifecycle. The layer names and their order are the `layer` enum of the jd-shipped `part.schema.json` (enum order = deploy order), not editable catalog data |
| 6 | **Selection**: a map entry chosen by a context value, with a fallback key | implementation by environment type, class by requirement class | exact lookup is an algorithm; the map, the value and the fallback key are data |
| 7 | **Context value**: a named value an algorithm reads or writes (Appendix B) | the binder fills concept records from them | an algorithm needs named inputs; data says where each lives |
| 8 | **Contract**: a declared export list every selected part exports exactly | load check | a cross-document equality rule |
| 9 | **Grant**: a node kind whose target input is named by data and must be the workload's own node | boundaries 1, 2 | a security rule must not be switchable off by data |
| 10 | **Policy**: match, `set`, `default`, `require`, its two scopes and their shapes, layering, conflicts | the policy step | an ordering and conflict algorithm; the shape rules need messages JSON Schema cannot give (section 3) |
| 11 | **Release, gate and evidence**: dark/probe/shift, IQ/OQ, approval counting, records written once | release flow, qualification, promotion | algorithms with security weight (approvals.md §9) |

**Where "the code knows no structure" stops being literal**, and why:

- **`kind` and `x-jd`.** The loader reads `kind` to pick a schema and the annotation keyword to learn anything; both are named once, in the loader.
- **Concept and root names.** The roots of concept 4 and the role names of Appendix B are the engine's interface, code like the grammar.
  Some equal a field name (`kind`, `name`, `image`, `port`, `runtime`, `release`): the literal guard allows them by name (section 9).
- **External protocols** (class b): Pulumi's `Pulumi.yaml` (`configuration`, `outputs`), Entra claims, ARM SDK shapes, `ARM_*`/`PULUMI_*`,
  HTTP's URI form (`<scheme>://<host><path>`). `Pulumi.yaml` is a YAML file whose structure the code reads: the one stated exception (choice 6).
- **jd's identifiers**: the id and stack scheme (`@workload`, `substrate`, `@<env>`), `name()` placeholders and hash, the `guid()` namespace,
  the canonical config and node hash. Changing one changes deployed state, so they are concepts 3–4, not formats.
- **jd's evidence formats** (release record, approval record, graph JSON) if the user takes choice 2 (b): jd's own JSON output contract,
  typed internal code beside its schema, listed in the guard's allowlist by file.

## 3. How meaning attaches: annotated JSON Schema

Each kind's schema stays the shape (plain JSON Schema, draft-07) and gains `x-jd` annotations: **nine keywords**, each with one meaning, checked
by a meta-schema (`schemas/x-jd.schema.json`): an unknown keyword or role is an error at the schema file.

| Keyword | On | Meaning |
|---|---|---|
| `is: <role>` | any field | the field holds the role's value (Appendix B). Read on load, written when jd writes the kind. A role is `one` or `many` (Appendix B): a `many` role under an array takes every item, a `one` role the first item (`workload.port`, as `Expander.cs:38` does today) |
| `define: <ns>` \| `[<ns>, …]` | a string, or a map (its keys) | defines names; the same name twice, in any files of the kind's set, is an error at the second |
| `ref: <ns>` | a string, string array or map keys | each must be a defined name in `<ns>` |
| `select: <role>` | a map | at resolve, the entry keyed by the role's value |
| `fallback: <key>` | a `select` map | the entry used when no key equals the value; neither: an error naming file, map and value |
| `scope: <layer \| workload>` | a part | the part's scope, a literal; a field holding it is `is: part.scope` |
| `match: <role>` | a policy criterion | the criterion holds; its value equals the role's value at the node |
| `one: true` | a kind's root | exactly one document of the kind |
| `message: <text>` | any subschema | the error text when that subschema fails |

**The YAML files do not change**, except the runtime file in S24e (section 9). The binder also honours two plain JSON Schema annotations: `default` (an absent field binds the default: `class: standard`, node
`kind: create`) and `enum` order on the `layer` definition (deploy order).

**Validator (NJsonSchema 11.0.2), reproduced in a spike, deleted.** It keeps unknown `x-*` keywords as `ExtensionData` on every subschema, also
through `$ref`. Each `ValidationError.Schema` is the failing subschema, so `message` attaches (shown for `pattern` and for
`additionalProperties: false` on a `patternProperties` object). It **ignores** `if`/`then`/`else`, `const`, `dependencies`, `propertyNames`
and `contains`: the meta-schema refuses them in jd's schemas, `kind` uses `enum`, key patterns use `patternProperties` +
`additionalProperties: false`. A value fitting no `oneOf` branch gives one `NotOneOf` at the parent with every branch's errors, so no rule
is written as `oneOf` + `message`: the policy-scope shapes stay code (concept 10).

**Internal representation.** Three generic pieces, no type per file kind:

| Piece | What it is |
|---|---|
| `Document` | source, kind, the JSON tree (as today's `CatalogParser.Document`, `CatalogParser.cs:43`) |
| `SchemaIndex` | per kind, built once: role → instance paths, with the enclosing `select`s and scopes; namespace → defining and referring paths |
| `Binder` | fills **concept records** from a document's roles, each value with its location; writes a document of a kind from concept records (section 5) |

The concept records stay typed and the algorithms keep using them unchanged: `Node`, `Part` (gains `Scope`, `Reads` and its location),
`PartOwner` (name, source, `uses`, contract, its parts by selection keys; replaces `Component`, `SharedResource`, `Runtime`, `Implementation`,
`RuntimeImplementation`), `Probe`, `RuntimeRelease`, `Policy`, `NamingRule`, `ApprovalPolicy`, `Environment` (one record for a definition and
a descriptor; replaces `EnvironmentDefinition` and `EnvironmentDescriptor`, as `EnvironmentDefinition.Over` already does),
`Requirement`, `ReleaseWorkload`. **Deleted**: the per-file types `Catalog` (its fields are one collection per file kind), `EnvironmentDefinition`,
`CatalogSource`, every `*LoadResult` (one `LoadResult<T>` of value and errors), `ApprovalRecord.Stored*` (choice 2 a only), the `To*`
converters, `ImplementationKey`, `SharedScope`, `MatchCriteria`. `Expander`, `GraphBuilder`, `ReleaseFlow`, `Qualifier` and `PreviewFormatter`
read the same records as today, so their algorithms and output do not change.

### Annotated: one component (`schemas/catalog/component.schema.json`, excerpt)

```jsonc
{ "x-jd": { "is": "owner" }, "properties": {
    "kind":    { "enum": ["Component"] },
    "name":    { "type": "string", "pattern": "^[a-z][a-z0-9-]{1,14}[a-z0-9]$", "x-jd": { "define": ["part-owner", "component"] } },
    "exports": { "type": "array", "items": { "type": "string" }, "x-jd": { "is": "contract" } },
    "uses":    { "type": "array", "items": { "type": "string" }, "x-jd": { "is": "part.reads", "ref": "shared-resource" } },
    "implementations": { "type": "object", "x-jd": { "select": "env.type", "fallback": "default", "ref": "environment-type" },
      "additionalProperties": { "properties": {
        "environment": { "$ref": "part.schema.json", "x-jd": { "scope": "environment" } },
        "classes": { "type": "object", "x-jd": { "select": "requirement.class" },
                     "additionalProperties": { "$ref": "part.schema.json", "x-jd": { "scope": "workload" } } } } } } } }
// part.schema.json, shared by Component, Runtime and SharedResource (today three copies of `node`):
{ "x-jd": { "is": "part" }, "definitions": { "layer": { "enum": ["shared", "environment"], "x-jd": { "is": "layer" } } },
  "properties": {
    "nodes":   { "type": "object", "additionalProperties": false, "x-jd": { "is": "part.nodes",
                 "message": "node names are lowercase letters, digits and hyphens, starting with a letter and not ending in a hyphen" },
                 "patternProperties": { "^[a-z]([a-z0-9-]*[a-z0-9])?$": { "properties": {
                   "template": { "x-jd": { "is": "node.template" } },
                   "kind":     { "enum": ["create", "grant"], "default": "create", "x-jd": { "is": "node.kind" } },
                   "config":   { "x-jd": { "is": "node.config" } } } } } },
    "exports": { "x-jd": { "is": "part.exports" } } } }
```

`database.yaml` is unchanged: the binder finds its class part by `select` (`env.type`, then `requirement.class`), checks it against `contract`,
and lets it read `cosmos-account` because `part.reads` names it. `sharedresource.schema.json` binds `scope` as `is: part.scope` with
`$ref: part.schema.json#/definitions/layer`; `catalog.schema.json` binds each environment type's `layer` the same way. The runtime schema
binds its implementation's part as `is: runtime.part`, its `probe` and `release` fields as `probe.*` / `rollout.*`; `grants.yaml`'s
`targets` is `define: grant-template` (its keys) with each value `is: grant.target-input`.

### Annotated: one environment definition (`schemas/environment-definition.schema.json`, excerpt)

```jsonc
{ "properties": {
    "name":     { "x-jd": { "is": "env.name", "define": "environment" } },
    "region":   { "x-jd": { "is": "env.region" } },
    "type":     { "x-jd": { "is": "env.type", "ref": "environment-type" } },
    "offers":   { "x-jd": { "is": "env.offers", "ref": "part-owner" } },
    "approval": { "$ref": "approval.schema.json", "x-jd": { "is": "env.approval" } } } }   // one copy; today also in environment.schema.json
// approval.schema.json: qualifiedOn approval.qualified-on, required approval.required, approvers approval.approvers,
// separateFromDeployer approval.separate. environment.schema.json (the descriptor) binds the same roles plus values as env.values.
```

`env.offers`, `env.values` and `env.approval` are roles the binder fills; none is an expression root. `${env.values.cosmos-account.id}` and
`${env.offers}` stay errors (`EnvLimit`), each with a negative test (S24c1).

### Annotated: one workload (`schemas/workload.schema.json`, excerpt)

```jsonc
{ "properties": {
    "metadata": { "properties": { "name": { "x-jd": { "is": "workload.name" } }, "team": { "x-jd": { "is": "workload.team" } } } },
    "container": { "properties": {
      "image":     { "x-jd": { "is": "workload.image" } },
      "variables": { "x-jd": { "is": "workload.variables" } },
      "ports":     { "items": { "properties": { "port": { "x-jd": { "is": "workload.port" } } } } } } },  // a `one` role: the first item
    "requires": { "items": { "x-jd": { "is": "requirement" }, "properties": {                         // a `many` role: every item
      "type":  { "x-jd": { "is": "requirement.type", "ref": "component" } },
      "id":    { "x-jd": { "is": "requirement.id" } },
      "class": { "default": "standard", "x-jd": { "is": "requirement.class" } } } } } } }
```

The effective id (id, else type) and its uniqueness are concept 2's rule in code, on the bound values; `default: standard` replaces
`Requirement.DefaultClass`.

## 4. Checks: where each one lives

S = plain JSON Schema, R = generic engine rule driven by annotations, C = still code (a concept's own rule, field names from the binding).
Every check keeps a negative test; a moved check keeps its test.

| Check today | Where | After | Why |
|---|---|---|---|
| YAML, one document, a mapping with `kind`; unknown kind | `CatalogParser` 138–171 and four copies | C (loader) | bootstrap |
| Retired kinds `ResourceType`/`Mapping`, policy `add` | 28–35, 167–179 | deleted (choice 3) | migration aids past S21 |
| Exactly one Catalog | 191–202 | R `one` | |
| Names unique (part owners, policies); naming, roles, grant targets merged once | 206–215, 578–598 | R `define` | |
| Reserved names: a part-owner name equal to a root of concept 4 (`env`, `role`, `workload`, `resource`, `runtime`) | 258–264 | C concept 4 | the roots are the grammar's closed list |
| `uses` names shared resources | 269–293 | R `ref` | |
| `uses` direction, no cycles | 282–301 | C concept 5, from `part.scope` and the `layer` order | |
| Node names: pattern | 39, 324 | S `patternProperties` + `message` | |
| Node names: not a root, `runtime` only in a Runtime; roots and exports a part reads (3, 5) | 323–371 | C concepts 3–4 on `Part.Reads` and the owner | |
| Node names unique per scope; not a shared resource's name | 377–395 | C concept 5 | |
| Grant: where, template listed, target form (1, 2) | 400–423 | C concept 9 | security |
| Policy scope shapes (`kind`, `runtime`, `require` only workload scope, `runtime.` paths) | 426–467 | C concept 10 | `oneOf` cannot give these messages (section 3) |
| Policy `require` names a component | 471–478 | R `ref` | |
| Policy paths overlap in one policy | 218–233 | C concept 10 | |
| Implementation keys, policy `environmentType` declared | 481–498 | R `ref` | |
| Implementation keyed by a shared-layer type | 501–504 | C concept 6 + 5 | |
| Same classes per implementation | 508–519 | R nested `select`s have equal key sets | |
| Class exports equal contract; no `resource.*` in exports | 522–543 | R `contract`; C concept 4 | |
| Probe interval ≤ timeout; runtime node must not set rollout inputs | 237–255 | C concept 11 | cross-field |
| Naming `allowed` accepts hex | 552–575 | C concept 4 (`name()` hash) | |
| Type declared; `approval: true` needs the block (13) | `EnvironmentTypeCheck` 13–20 | R `ref`; C concept 11 | security |
| Descriptor values: no reserved keys | `EnvironmentParser` 37–40 | C or deleted: S24c1 proves it live or dead | |
| Descriptor values: no dotted keys | 66–86 | S `patternProperties` + `message`; flattening C concept 3 | |
| Approval: not qualified on itself, `required` ≤ approvers | 53–64 | C concept 11 | cross-field |
| Offers name owners; at most one runtime offered | `Expander` 60–93 | R `ref`; C concept 5 | |
| Shared parts in the base; no repeated part (8) | 95–115 | C concept 5 | security |
| Requirement offered (11); implementation; class | 178–204 | C offer check; R `select` + `fallback` | |
| The environment offers exactly one runtime | 227–238 | C concept 5 | |
| Effective ids unique | `WorkloadRules` 24–44 | C concept 2 | ADR 0011 fallback |
| Variables read only `${resource.<id>.<export>}` (6) | 46–76 | C on `workload.variables` | security |
| `fn::entries` only as the runtime node's top-level form | `ConfigWalker` 46–57 | C concept 4 | |
| An export reads no `resource.*` (graph) | `GraphBuilder` 33–39 | C concept 4 | |
| Only the runtime node reads `resource.*` (9); the ref is an export of this workload; no export waiting on the runtime | 94–117 | C concept 3 | security |
| `runtime.*` without a runtime | 129–134 | C concept 3 | |
| Grant target after policies (1) | 157–172 | C concept 9 | security |
| Graph cycle | 183–192 | C concept 3 | |
| Policy `require` met by the workload | `PolicyApplier` 49–53 | C concept 10 | |
| Cross-policy overlap and conflicting values; entries misuse | 133–177 | C concept 10 | |
| Secret never in the descriptor (12) | `DescriptorComposer` 25–33 | C concept 3 | security |
| Manifest: definition found, UTF-8 | `ReleaseBuilder` 50–67 | C concept 11 | |
| Image pinned by digest | 98–104 | C concept 11 | trust |
| Workload names unique; `dependsOn` not itself, known; no cycle | 120–154 | C concept 11 | names come from the definitions, so no `define` can see them |
| Set: `createdAt` a UTC time; `definitionSha256` matches | `ReleaseSetFile` 76–96 | C concept 11 (the one time form; the pin) | trust boundary |
| Records: schema + "is what its place says" (approvals.md §9) | `QualificationReader` 99–129, `ApprovalRecord` 107–126 | S + C concept 11 | trust boundary |

### Security boundaries (D19, components.md §3)

The **rule** stays in code; data only says **where** its inputs are. **Binding completeness**: at start, every kind with a part binds
`part.nodes`, `node.kind`, `node.template`, `node.config`; the workload binds `workload.variables`; the runtime binds `runtime.part`; the
catalog binds `environment-type.gated`; else jd stops with "schema X does not bind role Y". Unbound fields are inert: the engine reads nothing
it has not bound, so a new workload field cannot carry an expression into the platform.

| # | Boundary | After S24 | Strength |
|---|---|---|---|
| 1, 2 | Grant targets own nodes; who may add a grant | C concept 9 on `Node.Kind` and `grant.target-input` | as today |
| 3, 5 | What a part reads | C on `Part.Reads` and the owning selection | as today |
| 4 | `env.*` bypass | C concept 4: `env.*` reads only `env.name`, `env.region`, `env.type`; no binding adds a root; tests for `${env.values…}`, `${env.offers}` | as today |
| 6 | Workload input into the platform | C on `workload.variables` + binding completeness + unbound = inert | as today |
| 7, 8 | Who deploys which scope | C concept 5; layer names and order in the jd-shipped schema | as today |
| 9, 10 | Requirement scopes apart; owner prefix | C concept 3 (ids unchanged) | as today |
| 11 | Unoffered component | C offer check | as today |
| 12 | Secret in the descriptor | C concept 3 | as today |
| 13 | Gated type without its gate | C concept 11 on `environment-type.gated` | as today |

**No boundary weakens with choice 1 (a)**: the schemas ship with jd and are reviewed like code. **Blocker if choice 1 (b)** (schemas in the
catalog): rows 1, 2, 6, 7, 8 and 13 would hang on files the platform team edits without a jd release; binding completeness catches a removed
binding, not a moved one (`node.kind` bound to a field no file sets). So (b) needs its own design.

## 5. Files jd writes

The **binder writes** a document of a kind from concept records: it walks the kind's schema in property order and writes each bound field;
a `many` role under an array writes one item per record; a null value is not written; maps keep the order they are given; unbound fields are
not written; a required bound field without a value is an error; the result is validated against the same schema before it is written (as
`DescriptorComposer` re-parses today). The output schema is the allowlist, so "records are built from an allowlist" holds by construction.

| File | Today | After |
|---|---|---|
| Environment descriptor (YAML) | `DescriptorComposer` 56–69 | binder over `environment.schema.json` from the `Environment` record: `env.values` the deployed exports (secrets dropped, concept 3) |
| Release set (YAML) | `ReleaseSetFile` 36–47, read 76–96 | binder over `release-set.schema.json` from the `ReleaseSet` and `ReleaseWorkload` records; read back by the same bindings |
| Release manifest (YAML, read only) | `ReleaseBuilder` 47–75 | binder: `release.workloads`, `release.definition-path`, `release.depends-on`, `release.label` |
| Release record, approval record, graph JSON (`preview --json`, the `graph.sha256` input) | `ReleaseRecordBuilder` 38–178, `ApprovalRecord` 19–46, `GraphJson` 14–75 | **choice 2**: (a) the binder, or (b) unchanged typed code (below) |
| Preview text | `PreviewFormatter` | unchanged: it renders concepts, not a file |

**Choice 2, jd's JSON evidence.** (a) needs what the descriptor and set do not: nested item lists (`workloads[].graph.nodes[]`,
`run.nodes[].steps[]`, `iq.checks[]`), blocks present by a computed condition (`failure`, `oq.detail`, `steps`), status values derived from
outcomes (`ReleaseRecordBuilder.cs:64,167`). The `many` rule and "null is not written" cover the first two once the code computes a record
concept tree; the cost is **~45 more roles** (`record.*`, one per field), a concept tree that mirrors the record field by field, and ~+250 /
−200 lines (S24h1–2). Those roles would only rename today's paths. (b) keeps the three JSON formats as typed internal code: each has its schema as
the published contract and a golden (`OrchestratorTests.Record.cs:96`, `ApprovalRecordTests.cs:42`, `GoldenGraphTests`), the guard allows
their literals by file, and no reader outside jd depends on their code. **Recommended: (b)**: your rule names YAML files, which people write
and read; these are jd's JSON output, and (a) adds roles that are paths under another name, against principle 7.

Records written before stay readable either way: the schemas keep accepting `tier` (persisted-format rule).

## 6. Errors

- Every error stays `file: location: message` (`LoadError`). Schema errors keep today's location extraction (`LoadError.FromSchema`).
- **Locations come from the index**: the binder records where each concept value was read (`implementations.default.classes.standard`,
  `requires[0]`), so no check builds a location from field names (Appendix A, "Locations and messages").
- **A rule that is data carries its message as data**: a subschema with `message` reports that text with the location; a `ref` failure reads
  "'x' is not a <namespace> in the catalog (declared: …)"; a `select` miss "<owner> has no <map> entry for <value> and no '<fallback>' entry".
  Namespace and map names come from the schema, so they read like today's messages.
- **A rule that is code keeps its message in code** (concepts 2–5, 9–11), with each field name taken from the binding, not spelled.
- Error tests pin file, location and the contract part of the message; a slice that changes a message says so in its brief.

## 7. Simplicity guard

Annotations say **where**, never **how**: no conditions, no loops, no computed values, no user-defined roles. **Nine keywords, 73 roles**
(Appendix B; ~118 with choice 2 a), both checked by the meta-schema. Two fallbacks remain and are named as such: a `select` map's `fallback`
key (`default`), and a requirement's id falling back to its type, which is concept 2's rule in code, not an annotation. Every rule is one
generic C# rule or a concept's own.

| Design | Concepts added | For | Against | Verdict |
|---|---|---|---|---|
| **A. Annotated JSON Schema + binder into typed concept records** | 9 keywords, 1 meta-schema, `SchemaIndex`, `Binder` | one source of shape and meaning; schemas already exist and load per kind; algorithms unchanged | schemas get longer; the role list is code | **recommended** |
| B. A declared meta-model file (`model.yaml`: kinds, fields, roles) | a new kind and loader | schemas stay plain | two sources of the same shape that can disagree; a second loader (principle 7) | rejected |
| C. Generate C# models from the schemas | a code generator | type safety | the generated code still knows every field | rejected |
| D. A rule language (CUE/KCL, rules in YAML) | a language | every check is data | a programming language in YAML; security rules become editable data | rejected (resolver.md) |
| E. A generic tree read by role strings in every algorithm | 1 index | no concept records | untyped reads spread through the expander, graph, release flow; more change, same knowledge | rejected (round 1) |

Left out on purpose: user-defined roles, roles bound in the catalog (choice 1), per-environment schemas, versioned meta-schemas.

## 8. Behaviour preservation

As in phase 1: the **deployed-config snapshot** (`src/jd.cli.tests/golden/deployed-config.json`) is byte-identical through every slice; the
**graph goldens** (`GoldenGraphTests`, `GoldenSubstrateTests`) are byte-identical except S24e's additive lines (below); the files jd writes are
byte-identical for the same inputs: the release-set, release-record and approval-record goldens exist (`ReleaseTests.cs:343`,
`OrchestratorTests.Record.cs:96`, `ApprovalRecordTests.cs:42`), a composed-descriptor golden is added in S24a1's first commit. Old records and
sets still read. Error message changes are listed per slice.

**S24e and the graph JSON.** The rollout's suffix rule, full weight and probe scheme become runtime data, so they are release inputs the graph
must carry: `GraphJson` writes them in the runtime node's `probe` and `release` blocks. The graph goldens gain exactly those lines and
`graph.sha256` changes for the same inputs; nothing compares it across jd versions (it is written at `ReleaseRecordBuilder.cs:109` and read by
no code). Leaving them out would let the hash miss a deploy input.

**Azure gate** (conventions): not while the snapshot and the direct-Azure code are unchanged. S24e changes `ReleaseFlow.cs`,
`HttpRevisionProbe.cs` and `Qualifier.cs:73` (values move to data, same values), so it runs the gate; the phase ends with one gate as its exit
evidence.

## 9. Slices and exit criteria

Each slice leaves `tools/verify.sh` green and the goldens as section 8 says. **Quality**: every slice's acceptance has the item "every touched
file at the full quality bar" (conventions "Code quality bar", standards, review checklist): the reviewer judges each touched file whole, not
only the diff, and the debt listed for the slice (Appendix A, last table) is fixed in it, not carried. **Bridge**: what each slice leaves in
place so the next builds on a green tree. Lines are production lines (C#, schemas), estimated, added / removed; the cap is ~250 changed.

| Slice | Content | Bridge | Lines | Azure gate | Acceptance |
|---|---|---|---|---|---|
| **S24a1** One loader | First commit: the composed-descriptor golden. `DocumentLoader` (YAML → `Document`, kind → schema from the schemas' `kind` enum) replaces the five parse-and-validate copies; `LoadResult<T>` replaces the `*LoadResult` types | today's models are built from `Document` by today's code | ~+110 / −140 | no | goldens identical; loader negative tests kept; quality |
| **S24a2** Meta-schema and index | `x-jd.schema.json` (keywords, roles, refused keywords); `SchemaIndex`; `message`; shared `part`/`node`/`approval` schemas; node-name and dotted-key patterns as schema; retired-kind messages deleted. The **literal guard** test, its allowlist of today's literals | annotations are inert except `message` | ~+150 / −90 | no | a negative test per meta-schema refusal and per `message`; quality |
| **S24a3** Names | `define`, `ref`, `one` as generic rules; `CheckSingleCatalog`, `CheckUnique`, `Merge`, the name parts of `CheckUses`, `CheckPolicyRequires`, `CheckEnvironmentTypes` deleted | `Catalog` still built | ~+110 / −130 | no | one negative test per rule; quality |
| **S24b1** Binder and parts | `Binder`; `is` for `part…`, `node.*`, `contract`, `select`, `fallback`, `scope`, `layer`; `PartOwner`/`Part` replace `Component`, `SharedResource`, `Runtime`, `Implementation`, `RuntimeImplementation`, `ImplementationKey`, `SharedScope` | `Catalog` holds `PartOwner`s | ~+140 / −110 | no | goldens identical; `select` miss and `fallback` tests; quality |
| **S24b2** Reads and scopes | read, node-name, scope-node and `uses`-direction checks out of `CatalogParser` into concept rules over `Part`, locations from the index | — | ~+120 / −130 | no | boundaries 3, 5, 7, 8 by their tests; component named `resource` or `role` refused; quality |
| **S24b3** Grants, contracts, completeness | grant, contract, same-classes and runtime checks as concept rules; binding completeness; `Catalog` → the concept collections; `CatalogParser` deleted | — | ~+120 / −130 | no | boundaries 1, 2, 9, 10; a test per missing binding; quality |
| **S24c1** Environment | `Environment` record bound from a definition or a descriptor; `EnvironmentDefinition`, `EnvironmentDescriptor` field knowledge deleted; descriptor reserved-values check proven or deleted | — | ~+100 / −130 | no | boundaries 4, 13; `${env.values…}`, `${env.offers}` refused; quality |
| **S24c2** Workload | workload and requirement roles; `ExpressionContext` takes `Environment` and the workload owner; `WorkloadRules` on bound values, returning `LoadError`s; `ReleaseBuilder` reads the same bindings | — | ~+120 / −130 | no | boundaries 6, 11; an unbound workload field carrying `${cosmos-account.id}` is inert; quality |
| **S24d** Policies | `match:` criteria, `policy.*` roles; `MatchCriteria` deleted; scope-shape rules (concept 10) take field names from the binding | — | ~+80 / −110 | no | every policy negative test kept, messages unchanged; quality |
| **S24e** Rollout data | `probe.*`, `rollout.*` by `is`. **Catalog file change**: `runtimes/container-app.yaml` gains `release.suffix: { prefix: r, configDigits: 8, saltDigits: 6 }`, `release.fullWeight: 100`, `probe.scheme: https`; `runtime.schema.json` requires them with a `message`, so an older catalog fails at load naming the field (catalogs ship beside the jd they run on); `GraphJson` writes them | — | ~+90 / −70 | **yes** | snapshot identical; graph goldens differ by exactly those lines; release-flow tests unchanged; quality |
| **S24f** YAML jd writes | the binder writes the descriptor and the release set and reads the manifest and set; `DescriptorComposer` and `ReleaseSetFile` field names gone | — | ~+100 / −90 | no | descriptor and release-set goldens identical; old sets read; quality |
| **S24g** Docs and standards | standards §1 ("allowed in code: the concepts of documents.md §2; formats are data"); resolver.md, components.md §4, conventions "Data drives the platform"; guard allowlist final; an ADR (Proposed) for the vocabulary | — | docs | **yes**: exit gate | E2E green; allowlist = section 2's exceptions |

With choice 2 (a), **S24h1** release record and **S24h2** approval record and graph JSON (~+250 / −200 together, no gate): the record concept
tree, ~45 roles, the three JSON formats through the binder; their goldens identical. Total (b): about +1,240 / −1,260 production lines over
11 code slices of at most ~250 changed lines each; (a): 13 code slices, about +1,490 / −1,460.

**The literal guard** (S24a2, final in S24g): a test over production C# that fails on any string literal, or text segment of an interpolated
string, without whitespace, whose dot- or bracket-separated parts include a property name declared in `schemas/` (so `"template"`,
`".template"`, `$"{location}.template"`, `"implementations."` all count). Its allowlist is by file and literal: class (b), the concept and root
names of section 2 that equal a property (`kind` `CatalogParser.cs:157`, `release` `Cli.cs:88`, `workload`/`id`/`hash` `BuiltIns.cs:32-35`,
`runtime` `ExpressionEvaluator.cs:16`, `name`/`team`/`image`/`port` `ExpressionEvaluator.cs:23-26`, `name`/`value` of `fn::entries`), and with
choice 2 (b) the three evidence writers. **It proves only that no bare field literal remains**: a role named after a path (`"probe.path"`)
passes it, so the reviewer judges role names against Appendix B, and prose in messages is the reviewer's too (section 6).

**Phase 1.5 exit criteria** (proposed): (1) the literal guard passes with only section 2's exceptions allowed; (2) the same Azure resources
and the same written documents for the same inputs: snapshot and the written-file goldens byte-identical through every slice, the graph
goldens except S24e's lines; (3) every D19 boundary keeps its tests, and binding completeness has its own; (4) E2E green at the end.
**Roadmap change proposed:** insert phase 1.5 "The code knows no file structure" (serves N1, N10) before S22 and phase 2; phase 2's exit
"added as components only, no C# change" gains "and no schema change outside the component's own kinds".

**Later phases stay open:** phase 2's messaging and secrets are data (component files, a `grants.yaml` line); N2 contract versions are a field
bound to a contract version role; N5 offers and N4 bind add roles to the closed list, one design note each; N6's registry is `env.values` read
from another store.

## 10. Open choices for the user

| # | Choice | Options | Recommendation |
|---|---|---|---|
| 1 | Where the annotated schemas live | (a) `schemas/`, shipped with jd: a format change is data, no C#, but a jd release; (b) in the catalog, edited by the platform team | **(a)**: no boundary weakens (section 4); (b) is a blocker until designed |
| 2 | jd's JSON evidence (release record, approval record, graph JSON) | (a) through the binder (S24h1–2, ~45 roles); (b) typed internal code, a stated exception | **(b)**: simpler, and your rule is about YAML files (section 5) |
| 3 | Migration aids in the loader (retired kinds, policy `add`) | delete / keep as data (a retired-kind schema) | **delete**: S21 is done; "unknown kind" suffices |
| 4 | Standards §1 "formats allowed in code" | rewrite (S24g) / keep | **rewrite**, or the rule allows what you ruled out |
| 5 | Container Apps facts in code (suffix rule `r` + 8 + 6 hex for the 54-character revision name, weight 100, `https`) | move to the runtime's data (S24e) / leave as release-flow constants | **move**: resource knowledge (standards §1), kept whole in one place (principle 8) |
| 6 | `Pulumi.yaml` (`TemplateLibrary.cs:67-69`), a YAML file whose structure the code reads | accept as the adapter's protocol (class b) / no exception | **accept**: it is Pulumi's format, not jd's (ADR 0003) |

The lead decided in round 1: the policy-scope shape rules stay code (concept 10), not `oneOf` + `message`.

## 11. ADRs affected

| ADR | Effect |
|---|---|
| [0003](../decisions/0003-pulumi-yaml-template-library.md) | holds: templates and their protocol stay the adapter's (class b) |
| [0004](../decisions/0004-two-layer-definition-model.md) | holds: the layers' formats become annotated schemas |
| [0007](../decisions/0007-definition-upload-snapshot.md) | holds: the set is still embedded as received and hashed; its layout comes from its schema |
| [0011](../decisions/0011-requirement-id.md) | holds: the effective id is concept 2's rule on bound values |

New: one ADR (Proposed) for "the engine knows concepts, schemas bind fields" with the vocabulary, written in S24g.

## Appendix A. Inventory: code that knows a document's structure

Class: **(a)** moves to data; **(b)** an external protocol, an adapter constant (standards §1); **(c)** an engine concept of section 2.
"Goes" names the slice. Paths under `src/`.

### jd.resolver: catalog kinds

| File:line | What it knows | Class | Goes |
|---|---|---|---|
| `catalog/Catalog.cs:10-19` | `Catalog`: one field per catalog file kind | a | S24b3: concept collections |
| `catalog/Catalog.cs:25,32-58` | `EnvironmentType`, `Component`, `Implementation`, `SharedResource`, `Part`, `Runtime`, `RuntimeImplementation` | a | S24b1: `PartOwner`, `Part` |
| `catalog/Catalog.cs:45-49` | `SharedScope` `Shared`, `Environment` (layer names) | a | S24b1: the `layer` enum |
| `catalog/Catalog.cs:61-71` | `ImplementationKey.Default = "default"`, the lookup | a / c | S24b1: `fallback` (key a, lookup c) |
| `catalog/Catalog.cs:73-79` | `NodeKind` `create`/`grant`; `Node` | c | concepts 3, 9 |
| `catalog/Catalog.cs:82-94` | `MatchCriteria` keys `type`, `class`, `environmentType`, `template`, `kind`, `runtime` | a | S24d: `match:` |
| `catalog/Catalog.cs:100,108-118` | `Probe`, `RuntimeRelease` (records c, fields a) | a / c | S24e |
| `catalog/Catalog.cs:121-130` | `Policy`, `NamingRule` (records c) | c | bound S24d, S24a3 |
| `catalog/CatalogParser.cs:16-25` | the eight kind names | a | S24a1: from the schemas |
| `catalog/CatalogParser.cs:28-35` | retired kinds; removed field `add` | a | S24a2: deleted (choice 3) |
| `catalog/CatalogParser.cs:39` | node-name pattern | a | S24a2: schema |
| `catalog/CatalogParser.cs:82,84,85,108,124` | `rules`, `roles`, `targets`, `environmentTypes`, `approval`, `layer`, `version` | a | S24a3, S24b1 |
| `catalog/CatalogParser.cs:157,159` | the `kind` key | c | bootstrap |
| `catalog/CatalogParser.cs:182` | schema file from the kind | a | S24a1 |
| `catalog/CatalogParser.cs:204` | `name` of named kinds | a | S24a3: `define` |
| `catalog/CatalogParser.cs:248-251` | the runtime node, the rollout inputs it must not set | c / a | S24b3 (rule c), fields S24e |
| `catalog/CatalogParser.cs:550` | hex digits a naming rule must allow | c | concept 4 |
| `catalog/CatalogParser.cs:600-651` | `To*`: every field of every catalog kind | a | S24b1, S24d, S24e |
| `catalog/CatalogDirectory.cs:7` | `templates` directory skipped | c | the catalog layout's one rule: templates are the adapter's files |
| `EmbeddedSchema.cs`, `jd.resolver.csproj` | schema resource names | a | S24a1 |

### jd.resolver: environment, workload, expansion, expressions, policies, graph

| File:line | What it knows | Class | Goes |
|---|---|---|---|
| `environment/EnvironmentDefinition.cs:10-18,23,54-55` | the record, its `LoadResult`, schema name, `name`, `region`, `type`, `offers`, `approval` | a | S24a1, S24c1 |
| `environment/EnvironmentDescriptor.cs:11-37` | descriptor fields; `env.*` keys `name`, `region`, `type` | a (fields) / c (roots) | S24c1 |
| `environment/EnvironmentParser.cs:12,35-49` | schema name; `values`, `name`, `region`, `type`, `offers`, `approval` | a | S24a1, S24c1 |
| `environment/EnvironmentParser.cs:66-86` | dotted keys refused; values flattened | a / c | S24a2 schema; flattening concept 3 |
| `environment/EnvironmentFile.cs`, `EnvironmentLoadResult.cs` | a loader and result type per kind | a | S24a1 |
| `environment/ApprovalPolicy.cs:18-24` | block fields = C# names, camel-cased | a | S24c1: `approval.*` |
| `environment/DescriptorComposer.cs:56-69` | descriptor layout | a | S24f |
| `workload/WorkloadFile.cs:7-55` | its loader and `WorkloadLoadResult` | a | S24a1 |
| `expansion/Expander.cs:32-39,153-158,25` | `container`, `metadata.*`, `image`, `ports[0].port`, `variables`; `requires[].type/id/class`; class `standard` | a | S24c2 |
| `expansion/Expander.cs:15,18` | `substrate`, `@` | c | ids |
| `expansion/ConfigWalker.cs:13-18` | `fn::entries`, `workload.variables` (c); location `container.variables` (a) | c / a | location S24c2 |
| `policies/PolicyApplier.cs:20,78-95` | `container.variables`; criteria keys | a | S24c2, S24d |
| `expressions/ExpressionEvaluator.cs:16-26,44` | roots and workload root fields | c | concept 4 (guard allowlist) |
| `expressions/BuiltIns.cs:10-14,32-35` | `name`, `guid`, namespace, placeholders | c | concept 4 |
| `graph/GraphBuilder.cs:22,198-200` | `@workload`; node hash object | c | concept 3 (H47) |
| `graph/ConfigJson.cs:17`, `jd.orchestrator/ConfigFiller.cs:66` | `fn::entries` items `name`, `value` | c | concept 4: the form's documented output, inside the node hash |
| `graph/GraphJson.cs:14-75` | graph JSON layout | a / c | choice 2: S24h2, or (b) kept |
| `LoadError.cs:11` | NJsonSchema's error text | b | library |

### Locations and messages (field names inside errors)

Location: from the index (S24a2 onwards); field names in code messages: from the binding. Each goes in the slice that binds its concept.

| File:line | Spelled | Class | Goes |
|---|---|---|---|
| `catalog/CatalogParser.cs:212,228,245,251,262,290,299` | `name`, `set`/`default`, `implementations.<k>.probe.intervalSeconds`, `….nodes.runtime.config.<i>`, `uses` | a | S24a3, S24b2, S24b3 |
| `catalog/CatalogParser.cs:307-311,330,383,391,413,419,547` | `implementations.<k>.environment.`, `….classes.<c>`, `nodes.<n>`, `.template`, `.config.<i>` | a | S24b2, S24b3 |
| `catalog/CatalogParser.cs:436-476` | `match.kind`, `match.runtime`, `require`, `match`, `set.<p>`, `default.<p>` and the same names in messages | a | S24d |
| `catalog/CatalogParser.cs:515,531,539,588` | `….classes`, `….exports.<e>`, `<property>.<key>` | a | S24a3, S24b3 |
| `policies/PolicyApplier.cs:51,130` | `requires`, `default`/`set` | a | S24c2, S24d |
| `expansion/Expander.cs:72,76,86,92,107,114,159,208,261` | `offers[<i>]`, `offers`, `requires[<i>]`, `implementations.<k>.…` | a | S24b1, S24c1 |
| `graph/GraphBuilder.cs:37,161` | `exports.<n>`, `template` | a | S24b3 |
| `backend-providers/jd.bp.pulumi/TemplateLibrary.cs:104` | `template` (jd's node field, in an adapter) | a | S24b3: the node's catalog location from the binding |
| `release/ReleaseBuilder.cs:53,101,123,138-150` | `workloads[<i>].definition`, `container.image`, `workloads`, `….dependsOn[<j>]` | a | S24c2 |
| `release/ReleaseSetFile.cs:78,92` | `createdAt`, `workloads[<i>].definition` | a | S24f |
| `environment/EnvironmentParser.cs:39,57,62,75` | `values.<k>`, `approval.qualifiedOn`, `approval.required` | a | S24c1 |
| `environment/EnvironmentTypeCheck.cs:16,20` | `type`, `approval` | a | S24c1 |
| `release/ApprovalRecord.cs:116-119` | `environment`, `label`, `set`, `qualifiedOn` | a / c | choice 2 |

### jd.resolver and jd.orchestrator: release manifest, set, records

| File:line | What it knows | Class | Goes |
|---|---|---|---|
| `release/ReleaseDocument.cs:10-13` | four schema names | a | S24a1 |
| `release/ReleaseBuilder.cs:47-75,98-106` | manifest `workloads`, `definition`, `dependsOn`, `label`; workload `container.image`, `metadata.*` | a | S24c2 |
| `release/ReleaseSetFile.cs:36-47,76-100` | set layout | a | S24f |
| `release/ReleaseRecordFile.cs:15-44` | `set.yaml`, activities, outcomes, time formats | c | concept 11, the local store's layout |
| `release/QualificationReader.cs:80-172` | record fields | a / c | choice 2 |
| `release/ApprovalRecord.cs:19-46` | `Stored*` = the approval record layout | a / c | choice 2 |
| `jd.orchestrator/ReleaseRecordBuilder.cs:38-178` | the release-record layout (~45 fields), `"failed"`, `"notRun"` | a / c | choice 2 |
| `jd.orchestrator/ReleaseFlow.cs:27-32` | suffix rule: prefix `r`, 8 + 6 hex digits (Azure's revision-name rule, comment :27-28), full weight `100` | a | S24e: the runtime's data, whole |
| `jd.orchestrator/ReleaseFlow.cs:36-190`, `Qualifier.cs:73` | release block and probe by concept | c | concept 11 |

### jd.cli, jd.core, jd.definitionvalidator, jd.azure, backend providers

| File:line | What it knows | Class | Goes |
|---|---|---|---|
| `jd.cli/HttpRevisionProbe.cs:22` | `https` (a: Container Apps ingress) in `https://{fqdn}{path}` (b: the URI form) | a / b | S24e: `probe.scheme` |
| `jd.cli/Cli.cs:28,88`, `ApproveCli.cs:19-20`, `PromoteCli.cs:20-21` | commands and options | c | the CLI's own surface |
| `jd.cli/DataDirectory.cs:15-20` | `JD_DATA_DIR`, `state`, `state.key`, `records` | c | configuration (connections.md) |
| `jd.cli/EnvCli.cs:101`, `PreviewFormatter.cs`, `DeployFormatter.cs` | messages over concepts | c | — |
| `jd.definitionvalidator/WorkloadRules.cs:16-78` | `requires`, `id`, `type`, `container.variables`, `resource.` | a / c | S24c2 |
| `jd.definitionvalidator/WorkloadSchema.cs:8` | embedded schema name | a | S24a1 |
| `jd.definitionvalidator/YamlSchemaValidator.cs` | YAML → JSON, validation | c | the loader's parser |
| `jd.core/**` | ports and DTOs | c | no document structure |
| `backend-providers/jd.bp.pulumi/TemplateLibrary.cs:21,67-69,88` | `Pulumi.yaml`, `configuration`, `outputs`, `default` | b | stays (choice 6) |
| `backend-providers/jd.bp.pulumi/StackNames.cs:15-17`, `PulumiBackendOptions.cs:8`, `EnvironmentVariableAttribute.cs`, `PulumiResourceChangeParser.cs` | stack name length, `PULUMI_*`, change types | b | stays |
| `jd.azure/ArmCredentials.cs:13-18`, `ArmDeployerIdentity.cs:20-22`, `ArmLiveRevisions.cs` | `ARM_*`, Entra `tid`/`appid`/`oid`, ARM revision shapes | b | stays |

`samples/sample-app` is the workload's own application, not platform code: out of scope.

### Quality debt to fix in the slice that touches it

| Slice | Debt (rule) |
|---|---|
| S24a1 | the parse-and-validate block copied five times (`CatalogParser.cs:138-185`, `EnvironmentDefinition.cs:34-49`, `EnvironmentParser.cs:18-32`, `WorkloadFile.cs:30-45`, `ReleaseDocument.cs:20-33`) (duplication); `catch (Exception)` where `YamlException` is meant (`CatalogParser.cs:145` and the four copies) (errors); `EnvironmentDefinitionFile.Strings` used by `EnvironmentParser` (helper on the wrong type) |
| S24a2 | the `node` definition in three catalog schemas, `approval` in two (duplication in data); `YamlSchemaValidator.ValidateAsync(path)`/`ValidateContentAsync`, used only by tests (unneeded code); its `catch (Exception)` around schema loading turns a broken embedded schema into a validation error (errors) |
| S24b1 | `Reader` with 8 positional fields (`CatalogParser.cs:48-52`) (long parameter list) |
| S24b2 | nested ternary chains in `CheckReads` (`:353-363`) and `CheckNodeNames` (`:323-327`) (structure) |
| S24b3 | `CatalogParser` 659 lines, parsing and fifteen checks in one type (one responsibility) |
| S24c1 | the descriptor reserved-values check (`EnvironmentParser.cs:37-40`): prove with a test or delete (untested branch) |
| S24c2 | `ExpressionContext` with 11 positional arguments (`Expander.cs:172-174`, `DescriptorComposer.cs:34-36`, `PolicyApplier.cs:29-31`); `ExpansionResult` with 11 (`ExpandedModel.cs:62-73`) (long parameter lists); `Expander.ExpandEnvironment` 78 lines (`:49-127`); `WorkloadRules` returns `"location: message"` strings re-split in `WorkloadFile.cs:48-52` (stringly typed errors) |
| S24d | `MatchCriteria.RuntimeKey` and `RuntimeKind` both `"runtime"` with different meanings (names) |
| S24e | `RuntimeRelease` 10 positional strings (long parameter list) |
| S24f / S24h1 | `"failed"` three times in `ReleaseRecordBuilder.cs:64,71,167`, `"notRun"` beside a constant set (literals); null-forgiving chains in `QualificationReader.cs:80-172` (`!` without a reason). With choice 2 (b) no other slice touches these files, so S24f's brief takes both in scope |

## Appendix B. Roles (the closed list)

`one` unless marked *many*. 73 roles; choice 2 (a) adds ~45 `record.*`.

| Group | Roles | # |
|---|---|---|
| Structure | `owner`, `part` *many*, `part.nodes`, `part.exports`, `part.reads`, `part.scope`, `node.template`, `node.kind`, `node.config`, `contract`, `runtime.part`, `grant.target-input`, `naming.rules`, `naming.pattern`, `naming.max-length`, `naming.allowed`, `roles`, `catalog.version`, `environment-type.layer`, `environment-type.gated`, `layer` | 21 |
| Environment | `env.name`, `env.region`, `env.type`, `env.offers`, `env.values`, `env.approval`, `approval.qualified-on`, `approval.required`, `approval.approvers`, `approval.separate` | 10 |
| Workload | `workload.name`, `workload.team`, `workload.image`, `workload.port`, `workload.variables`, `requirement` *many*, `requirement.type`, `requirement.id`, `requirement.class` | 9 |
| Policy | `policy.name`, `policy.reason`, `policy.scope`, `policy.runtime`, `policy.set`, `policy.default`, `policy.require` (criteria use `match:` with the roles above) | 7 |
| Runtime | `probe.path`, `probe.expected-status`, `probe.timeout`, `probe.interval`, `probe.scheme`, `rollout.suffix-input`, `rollout.traffic-input`, `rollout.traffic-output`, `rollout.suffix-output`, `rollout.revision-output`, `rollout.fqdn-output`, `rollout.revision-key`, `rollout.latest-key`, `rollout.weight-key`, `rollout.resource-id-output`, `rollout.suffix-prefix`, `rollout.suffix-config-digits`, `rollout.suffix-salt-digits`, `rollout.full-weight` | 19 |
| Release | `release.label`, `release.created-at`, `release.workloads` *many*, `release.definition`, `release.definition-path`, `release.definition-sha256`, `release.depends-on` | 7 |

Namespaces (`define`/`ref`): `part-owner`, `component`, `shared-resource`, `runtime`, `policy`,
`environment-type`, `naming-kind`, `role`, `grant-template`, `environment`.
