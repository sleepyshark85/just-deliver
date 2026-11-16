# Documents: concepts and annotated schemas

The engine knows **concepts**, never **fields**. A concept is something an algorithm consumes; a field is where a document keeps it. Each
kind's schema in [`schemas/`](../../schemas/) is the document's shape and says, through `x-jd` annotations, which field holds which concept.
A renamed field or a new component shape is a schema change, not a C# change. The decision: [ADR 0019](../decisions/0019-engine-knows-concepts.md);
the binding rule for code: [standards §1](../engineering/standards.md). What the concepts mean for the model: [model.md](model.md).

## 1. Documents and schemas

Every YAML file jd reads or writes is a **document**: one YAML document per file, its `kind` and its tree. `DocumentLoader` reads it, picks the
schema by `kind` (from the schemas' `kind` enums), validates it and reports every error at file and location. The schemas ship with jd,
embedded in the assemblies, and are reviewed like code: the security boundaries depend on what they bind, so they are not catalog data.

A check is one of three things: plain JSON Schema (shape, patterns, required fields); a generic rule driven by an annotation (`define`,
`ref`, `one`, `select`); or a concept's own rule in code (cross-document, cross-field or security rules), which takes every field name from
the binding.

## 2. Concepts: what the engine knows

The closed list:

| # | Concept | What the engine does with it | Why it cannot be data |
|---|---|---|---|
| 1 | **Document**: a file, its `kind`, its tree; errors at file + location | loads, picks the schema by `kind`, validates, locates errors | something must read a file before data can say anything; `kind` is the one bootstrap field |
| 2 | **Name and reference**: a namespace, a name defined in it, a reference to it; a requirement's effective id (its id, else its type, unique per workload, ADR 0011) | load integrity | it is the rule itself ("a reference must resolve") |
| 3 | **Part, node, export**: nodes (`template`, `kind` create\|grant, `config`) and the exports evaluated from them | expansion, graph, ids, stacks, hashes, the backend package | the graph is the engine's output; its node shape and canonical hash are the deploy contract |
| 4 | **Expression**: the `${…}` grammar, its closed root list ([model.md](model.md#6-expressions)), the built-ins `name()`, `guid()`, the form `fn::entries` | two-phase evaluation, edges | a grammar is an interpreter; binding a field to a role says where a root's value lives, it never creates a root (boundary 4) |
| 5 | **Scope**: the layers in deploy order, and the workload deploy | who deploys what (boundaries 7, 8), `uses` direction, in-run reads | ownership and deploy order are the engine's lifecycle; the layer names and order are the `layer` enum of the jd-shipped `part.schema.json` |
| 6 | **Selection**: a map entry chosen by a context value, with a fallback key | implementation by environment type, class by requirement class | exact lookup is an algorithm; the map, the value and the fallback key are data |
| 7 | **Context value**: a named value an algorithm reads or writes (a role, Appendix B) | the binder fills concept records from them | an algorithm needs named inputs; data says where each lives |
| 8 | **Contract**: a declared export list every selected part exports exactly | load check | a cross-document equality rule |
| 9 | **Grant**: a node kind whose target input is named by data and must be the workload's own node | boundaries 1, 2 | a security rule must not be switchable off by data |
| 10 | **Policy**: match, `set`, `default`, `require`, its two scopes and their shapes, layering, conflicts | the policy step | an ordering and conflict algorithm; the shape rules need messages JSON Schema cannot give (section 3) |
| 11 | **Release, gate and evidence**: dark/probe/shift, IQ/OQ, approval counting, records written once | release flow, qualification, promotion | algorithms with security weight ([approvals.md](approvals.md)) |

**Where the code still reads structure** (these are the literal guard's only exceptions, section 5):

- **`kind` and `x-jd`.** The loader reads `kind` to pick a schema and the annotation keyword to learn anything; both are named once.
- **Concept and root names.** The expression roots and the role names (Appendix B) are the engine's interface, code like the grammar. Some
  equal a field name (`kind`, `name`, `image`, `port`, `runtime`, `release`).
- **External protocols:** Pulumi's `Pulumi.yaml` (`configuration`, `outputs`, `default`), Entra claims, ARM shapes, `ARM_*`/`PULUMI_*`,
  the URI form `<scheme>://<host><path>`. `Pulumi.yaml` is the one YAML file whose structure the code reads: it is Pulumi's format, not
  jd's ([ADR 0003](../decisions/0003-pulumi-yaml-template-library.md)).
- **jd's identifiers:** the id and stack scheme (`@workload`, `substrate`, `@<env>`), `name()` placeholders and hash, the `guid()` namespace,
  the canonical config and node hash. Changing one changes deployed state.
- **jd's evidence formats:** the release record, the approval record and the graph JSON are jd's JSON output, written and read by typed
  code beside their schemas (the published contract) and golden tests; the local record store's `set.yaml` likewise.

## 3. Annotations and the binder

Schemas are plain JSON Schema draft-07 plus `x-jd` annotations: **nine keywords**, checked by the meta-schema
[`x-jd.schema.json`](../../schemas/x-jd.schema.json), so an unknown keyword, role or namespace is an error at the schema file.

| Keyword | On | Meaning |
|---|---|---|
| `is: <role>` | any field | the field holds the role's value (Appendix B); read on load, written when jd writes the kind. A `many` role under an array takes every item, a `one` role the first (`workload.port`) |
| `define: <ns>` \| `[<ns>, …]` | a string, or a map (its keys) | defines names; the same name twice, in any file of the kind's set, is an error at the second |
| `ref: <ns>` | a string, string array or map keys | each must be a name defined in `<ns>` |
| `select: <role>` | a map | at resolve, the entry keyed by the role's value |
| `fallback: <key>` | a `select` map | the entry used when no key equals the value; neither is an error naming file, map and value |
| `scope: <layer \| workload>` | a part | the part's scope, a literal; a field holding it is `is: part.scope` |
| `match: <role>` | a policy criterion | the criterion holds when its value equals the role's value at the node |
| `one: true` | a kind's root | exactly one document of the kind |
| `message: <text>` | any subschema | the error text when that subschema fails |

The binder also honours two plain annotations: `default` (an absent field binds it: `class: standard`, node `kind: create`), and the listed
order of the `layer` enum, which is the deploy order (stated in a `$comment`, since JSON Schema does not promise it).

**The validator** (NJsonSchema) ignores `if`/`then`/`else`, `const`, `dependencies`, `propertyNames` and `contains`, so the meta-schema
refuses them: `kind` uses `enum`, key patterns use `patternProperties` with `additionalProperties: false` and a `message`. A value fitting no
`oneOf` branch gives one error with every branch's, so the policy-scope shapes stay code (concept 10).

**Internal representation.** No type per file kind: a `Document` (source, kind, tree); the `SchemaIndex`, built once per kind (role →
instance paths with the enclosing `select`s and scopes; namespace → defining and referring paths); and the `Binder`, which fills typed
**concept records** (`PartOwner`, `Part`, `Node`, `Rollout`, `Policy`, `NamingRule`, `EnvironmentType`, `EnvironmentDescriptor`,
`Workload`, `Requirement`, `ReleaseSet`, …), each value with the location it was read from.

### Annotated: a component (excerpt of [component.schema.json](../../schemas/catalog/component.schema.json) and [part.schema.json](../../schemas/catalog/part.schema.json))

```jsonc
{ "x-jd": { "is": "owner" }, "properties": {
    "name":    { "x-jd": { "define": ["part owner", "component"] } },
    "exports": { "x-jd": { "is": "contract" } },
    "uses":    { "x-jd": { "is": "part.reads", "ref": "shared resource" } },
    "implementations": { "x-jd": { "ref": "environment type", "select": "env.type", "fallback": "default" },
      "additionalProperties": { "properties": {
        "environment": { "$ref": "part.schema.json", "x-jd": { "scope": "environment" } },
        "classes": { "x-jd": { "select": "requirement.class" },
                     "additionalProperties": { "$ref": "part.schema.json", "x-jd": { "scope": "workload" } } } } } } } }
// part.schema.json, shared by Component, Runtime and SharedResource:
{ "x-jd": { "is": "part" }, "properties": {
    "nodes":   { "x-jd": { "is": "part.nodes" }, "patternProperties": { "<node name>": { "properties": {
                   "template": { "x-jd": { "is": "node.template" } },
                   "kind":     { "enum": ["create", "grant"], "default": "create", "x-jd": { "is": "node.kind" } },
                   "config":   { "x-jd": { "is": "node.config" } } } } } },
    "exports": { "x-jd": { "is": "part.exports" } } } }
```

The binder finds `database.yaml`'s class part by `select` (`env.type`, then `requirement.class`), checks it against `contract`, and lets it
read `cosmos-account` because `part.reads` names it.

### Annotated: an environment definition and a workload

```jsonc
// environment-definition.schema.json (environment.schema.json, the descriptor, binds the same roles plus values as env.values)
{ "properties": {
    "name": { "x-jd": { "is": "env.name" } }, "region": { "x-jd": { "is": "env.region" } }, "type": { "x-jd": { "is": "env.type" } },
    "offers": { "x-jd": { "is": "env.offers" } }, "approval": { "$ref": "approval.schema.json", "x-jd": { "is": "env.approval" } } } }
// workload.schema.json
{ "properties": {
    "metadata":  { "properties": { "name": { "x-jd": { "is": "workload.name" } }, "team": { "x-jd": { "is": "workload.team" } } } },
    "container": { "properties": { "image": { "x-jd": { "is": "workload.image" } }, "variables": { "x-jd": { "is": "workload.variables" } },
                   "ports": { "items": { "properties": { "port": { "x-jd": { "is": "workload.port" } } } } } } },   // a one role: the first item
    "requires":  { "items": { "x-jd": { "is": "requirement" }, "properties": {                                    // a many role: every item
                   "type": { "x-jd": { "is": "requirement.type" } }, "id": { "x-jd": { "is": "requirement.id" } },
                   "class": { "default": "standard", "x-jd": { "is": "requirement.class" } } } } } } }
```

These kinds carry only `is`: they are not catalog documents, so names in them are checked against the catalog in code.
`EnvironmentTypeCheck` checks the type is declared and the approval coupling; the `Expander` checks that offers name components or runtimes
(at most one runtime) and that a requirement's type is an offered component. `env.offers`, `env.values` and `env.approval` are roles, not
expression roots: `${env.values.cosmos-account.id}` and `${env.offers}` are errors.

**Binding completeness.** A security boundary reads only what a schema binds, so `SchemaIndex.CheckBindings` runs when the catalog or a workload is loaded: a kind
with a part binds the node roles, a kind declaring environment types binds `environment-type.gated`, the workload binds
`workload.variables`, and some schema binds `runtime.part`, `environment-type.layer` and `layer`; otherwise jd stops naming every schema and
role it misses. Unbound fields are inert: a new workload field cannot carry an expression into the platform.

**Writing.** The binder writes a document of a kind from concept records by walking the kind's schema in property order: a `many` role
writes one item per record, a null value and unbound fields are not written, a required bound field without a value is an error, and the
result is validated against the same schema before it is written, so the output schema is the allowlist. It writes the environment
descriptor and the release set, and reads the release manifest and set back through the same bindings (`ReleaseRead`). The JSON evidence
formats are typed code (section 2).

## 4. Errors

- Every error is `file: location: message` (`LoadError`). Schema errors take their location from the validator's error path.
- **Locations come from the index:** the binder records where each concept value was read (`implementations.default.classes.standard`,
  `requires[0]`), so no check builds a location from field names.
- **A rule that is data carries its message as data:** a subschema with `message` reports that text; a `ref` failure reads "'x' is not a
  <namespace> in the catalog (declared: …)"; a `select` miss "<owner> has no <map> entry for <value> and no '<fallback>' entry". Namespace and
  map names come from the schema.
- **A rule that is code keeps its message in code**, each field name taken from the binding, not spelled.
- Error tests pin file, location and the contract part of the message.

## 5. The literal guard

`LiteralGuardTests` scans production C# and fails on any string literal, or text segment of an interpolated string, without whitespace, whose
dot- or bracket-separated parts include a property name declared in `schemas/` (`"template"`, `".template"`, `$"{location}.template"`,
`"implementations."` all count). Its allowlist (`LiteralGuardTests.Allowlist.cs`) is by file and literal, in two groups: **section 2's
exceptions** (each entry is one of that section's list) and **prose false positives** (a lone word in a message). A stale entry fails. It
proves only that no bare field literal remains: a role named after a path passes it, so the reviewer judges role names against Appendix B and
prose in messages ([review checklist](../engineering/review-checklist.md)).

## Appendix B. Roles (the closed list)

`one` unless marked *many*; 74 roles, the meta-schema's `role` enum.

| Group | Roles | # |
|---|---|---|
| Structure | `owner`, `part` *many*, `part.nodes`, `part.exports`, `part.reads`, `part.scope`, `node.template`, `node.kind`, `node.config`, `contract`, `runtime.part`, `grant.target-input`, `naming.rules`, `naming.pattern`, `naming.max-length`, `naming.allowed`, `roles`, `catalog.version`, `environment-type.layer`, `environment-type.gated`, `layer` | 21 |
| Environment | `env.name`, `env.region`, `env.type`, `env.offers`, `env.values`, `env.approval`, `approval.qualified-on`, `approval.required`, `approval.approvers`, `approval.separate` | 10 |
| Workload | `workload.name`, `workload.team`, `workload.image`, `workload.port`, `workload.variables`, `requirement` *many*, `requirement.type`, `requirement.id`, `requirement.class` | 9 |
| Policy | `policy.name`, `policy.reason`, `policy.scope`, `policy.runtime`, `policy.set`, `policy.default`, `policy.require` (criteria use `match:` with the roles above) | 7 |
| Runtime | `probe.path`, `probe.expected-status`, `probe.timeout`, `probe.interval`, `probe.scheme`, `rollout.suffix-input`, `rollout.traffic-input`, `rollout.traffic-output`, `rollout.suffix-output`, `rollout.revision-output`, `rollout.fqdn-output`, `rollout.revision-key`, `rollout.latest-key`, `rollout.weight-key`, `rollout.resource-id-output`, `rollout.suffix-prefix`, `rollout.suffix-config-digits`, `rollout.suffix-salt-digits`, `rollout.suffix-max-length`, `rollout.full-weight` | 20 |
| Release | `release.label`, `release.created-at`, `release.workloads` *many*, `release.definition`, `release.definition-path`, `release.definition-sha256`, `release.depends-on` | 7 |

The `layer` role's values are ordered: the enum's listed order is the deploy order (`shared` before `environment`).

Namespaces (`define`/`ref`), spelled as the error messages read them: `part owner`, `component`, `shared resource`, `runtime`, `policy`,
`environment type`, `naming kind`, `role`, `grant template`, `environment`.
