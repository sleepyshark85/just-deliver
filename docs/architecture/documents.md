# The code knows no file structure

> Status: design note for S24, **proposed phase 1.5** (between phase 1 and S22/phase 2; [roadmap](../plans/roadmap.md) change proposed in
> [section 9](#9-slices-and-exit-criteria)). Serves the non-negotiable "nothing hard-coded", principles 6–14 ([context.md](../context.md#design-principles)),
> N1 (one shape) and N10 (cheap for the platform team), and makes phase 2's "added as components only, no C# change" structural. The user
> (2026-10-19): *"in general, the code shouldn't know the structure of any yaml file"*; *"check the whole solution for hard-code like that"*;
> *"applying the quality bar along the refactoring"*. The meaning of the S21 model (kinds, scopes, offers, grants) does not change; only where
> that meaning lives does. The user's open choices: [section 10](#10-open-choices-for-the-user).

## 1. Problem

The C# mirrors every document format. Each file kind has a model (`Catalog`, `Component`, `EnvironmentDefinition`, `EnvironmentDescriptor`,
`ReleaseSet`, `ApprovalRecord.Stored`, …), a loader (`*File.cs`, `*Parser.cs`) returning its own result type (`*LoadResult`), and field
names spelled in code (`body["environmentTypes"]`, `workload["container"]["image"]`, `record["iq"]`). Appendix A lists every place,
in seven projects. So a renamed field, a new kind or a new component shape is a C# change, and the schemas in `schemas/` are only a
first gate in front of a second, hand-written model of the same shape. Standards §1 even allows it ("Allowed in code: the catalog/schema
*formats*"); the user does not.

## 2. The line: what the engine core still knows

The engine knows **concepts**, never **fields**. A concept is something an algorithm in the engine consumes; a field is where a document
keeps it. Schemas (data) say which field holds which concept. The closed list:

| # | Concept | What the engine does with it | Why it cannot be data |
|---|---|---|---|
| 1 | **Document** — a file, its `kind`, its tree; errors at file + location | loads, picks the schema by `kind`, validates, locates errors | something has to read a file before any data can say anything; `kind` is the one bootstrap field (below) |
| 2 | **Name and reference** — a namespace, a name defined in it, a reference to it | load integrity: defined once, every reference resolves | it is the rule itself ("a reference must resolve"); data says which fields define and refer |
| 3 | **Part, node, export** — nodes (`template`, `config`, `create`\|`grant`) and the exports evaluated from them | expansion, graph, stacks, ids, hashes (H47), the backend package | the graph is the engine's output; its node shape and canonical hash are the deploy contract |
| 4 | **Expression** — `${…}` grammar, the built-ins `name()`, `guid()`, `fn::entries`, references and pending values | two-phase evaluation, edges | a grammar is an interpreter; the roots it reads are bound by data (concept 7) |
| 5 | **Scope** — the ordered layers (`shared`, `environment`) and the workload deploy | who deploys what (boundaries 7, 8), `uses` direction, in-run reads | ownership and deploy order are the engine's lifecycle; the layer *names and order* move to the Catalog document |
| 6 | **Selection** — a map entry chosen by a context value, with a fallback key | implementation by environment type, class by requirement class | exact lookup is an algorithm; the map, the value and the fallback are data |
| 7 | **Context value** — a named value the engine reads or writes: `env.region`, `workload.image`, `requirement.class`, `probe.path`, a record fact | expression roots, selection keys, the release flow's inputs, the facts a record holds | an algorithm needs named inputs and outputs; data says where each lives in a document |
| 8 | **Contract** — a declared export list every selected part must export exactly | load check | a cross-document equality rule |
| 9 | **Grant** — a node kind whose target input is fixed by data and must be the workload's own node | boundaries 1, 2 | a security rule must not be switchable off by data |
| 10 | **Policy** — match, `set`, `default`, `require`, layering and conflicts | the policy step | an ordering and conflict algorithm; criteria fields are data |
| 11 | **Release, gate and evidence** — the runtime's dark/probe/shift, IQ/OQ, approval counting, records written once | release flow, qualification, promotion | algorithms with security weight (approvals.md §9); which field holds which fact is data |

**Where "the code knows no structure" stops being literal**, and why:

- **`kind` and `x-jd`.** The loader must read `kind` to pick a schema, and must read the annotation keyword to learn anything; both are
  named once, in the loader. Every other field name leaves the C#.
- **Concept names.** Code says `probe.path`, not `"path"` under `"probe"` under `"implementations.<key>"`. The names of the eleven concepts
  and their context values (Appendix B) are the engine's interface; they are code like the expression grammar is.
- **External protocols.** Pulumi's `Pulumi.yaml` (`configuration`, `outputs`), Entra token claims, ARM SDK shapes, `ARM_*`/`PULUMI_*`
  variables stay adapter constants (standards §1 exception), class (b) in Appendix A.
- **jd's own identifiers.** The id and stack scheme (`@workload`, `substrate`, `@<env>`), the `name()` placeholders and hash, the `guid()`
  namespace, the canonical config and node hash: changing any changes deployed state, so they are concepts 3–4, not formats.

## 3. How meaning attaches: annotated JSON Schema

Each kind's schema stays the shape (plain JSON Schema, draft-07) and gains `x-jd` annotations: **seven keywords**, a closed vocabulary, each
naming a concept of section 2 by its role name. The YAML files do not change.

| Keyword | On | Meaning |
|---|---|---|
| `is: <role>` | any field | the field holds the context value or structural role `<role>` (Appendix B): read when a document is loaded, written when jd composes one |
| `define: <namespace>` \| `[<namespace>, …]` | a string, or a map (its keys) | defines names in the namespace(s); the same name twice, in any files, is an error at the second (replaces every `Merge` and `CheckUnique`) |
| `ref: <namespace>`, `or: [<literal>]` | a string, string array or map keys | each must be a defined name, or one of the `or` literals (`default`) |
| `select: <role>`, `or: <key>` | a map | at resolve, the entry keyed by `<role>`'s value, else the `or` key, else an error naming file, map and value; nested selects have equal key sets |
| `scope: <layer>` \| `scope: {is: <role>}` | a part | the part's scope, literal or read from a field (`SharedResource.scope`) |
| `one: true` | a kind's root | exactly one document of this kind (the Catalog) |
| `message: <text>` | any subschema | the error text when this subschema fails, so a rule written as schema keeps an actionable message (section 6) |

**Spike (reproduced, deleted):** NJsonSchema 11.0.2 (the validator in use) keeps unknown `x-*` keywords as `ExtensionData` on every
subschema, also through `$ref`, and ignores them when validating. The known gap stays: it ignores `propertyNames`, so key patterns use
`patternProperties` with `additionalProperties: false` (as `catalog.schema.json` already does).

**How the engine reads.** One `SchemaIndex` per kind, built once from the schema: role → the instance paths that hold it, with the
enclosing selects. A loaded `Document` answers `Each(role)` with `(value, location, selection keys)`; the expander asks for `part` under the
selection the environment and requirement make, the graph builder for `node.template`, the release flow for `probe.path`. No C# type per kind,
no `*LoadResult`; one `LoadResult` of documents and errors.

### Annotated: one component (`schemas/catalog/component.schema.json`, excerpt)

```jsonc
{ "properties": {
    "kind":    { "enum": ["Component"] },
    "name":    { "type": "string", "pattern": "^[a-z][a-z0-9-]{1,14}[a-z0-9]$", "x-jd": { "define": ["part-owner", "component"] } },
    "exports": { "type": "array", "items": { "type": "string" }, "x-jd": { "is": "contract" } },
    "uses":    { "type": "array", "items": { "type": "string" }, "x-jd": { "is": "part.reads", "ref": "shared-resource" } },
    "implementations": { "type": "object", "x-jd": { "select": "env.type", "or": "default", "ref": "environment-type" },
      "additionalProperties": { "properties": {
        "environment": { "$ref": "part.schema.json", "x-jd": { "scope": "environment" } },
        "classes": { "type": "object", "x-jd": { "select": "requirement.class" },
                     "additionalProperties": { "$ref": "part.schema.json", "x-jd": { "scope": "workload" } } } } } } } }
// part.schema.json, shared by Component, Runtime and SharedResource (today three copies of `node`):
{ "x-jd": { "is": "part" }, "properties": {
    "nodes":   { "x-jd": { "is": "part.nodes" }, "additionalProperties": false,
                 "patternProperties": { "^[a-z]([a-z0-9-]*[a-z0-9])?$": { "properties": {
                   "template": { "x-jd": { "is": "node.template" } },
                   "kind":     { "enum": ["create", "grant"], "default": "create", "x-jd": { "is": "node.kind" } },
                   "config":   { "x-jd": { "is": "node.config" } } } } } },
    "exports": { "x-jd": { "is": "part.exports" } } } }
```

`database.yaml` is unchanged; the engine now finds its class part by `select` (`env.type`, then `requirement.class`), checks it against
`contract`, and lets it read `cosmos-account` because `part.reads` names it. The runtime schema binds `nodes.runtime` as `is: runtime` and its
`probe`/`release` fields as `probe.*`/`release.*`; `grants.yaml`'s `targets` map is `is: grant.targets`, `ref: template`.

### Annotated: one environment definition (`schemas/environment-definition.schema.json`, excerpt)

```jsonc
{ "properties": {
    "name":   { "x-jd": { "is": "env.name" } },
    "region": { "x-jd": { "is": "env.region" } },
    "type":   { "x-jd": { "is": "env.type", "ref": "environment-type" } },
    "offers": { "x-jd": { "is": "env.offers", "ref": "part-owner" } },
    "approval": { "$ref": "approval.schema.json" } } }   // one copy; today duplicated in environment.schema.json
// approval.schema.json: qualifiedOn is approval.qualified-on (ref environment), required approval.required, approvers approval.approvers,
// separateFromDeployer approval.separate. catalog.schema.json: environmentTypes { "define": "environment-type" }, each entry's layer
// { "is": "environment-type.layer", "ref": "layer" }, approval { "is": "environment-type.gated" }; new: layers [shared, environment]
// { "define": "layer", "is": "layers" } (ordered), replacing the SharedScope enum.
```

### Annotated: one workload (`schemas/workload.schema.json`, excerpt)

```jsonc
{ "properties": {
    "metadata": { "properties": { "name": { "x-jd": { "is": "workload.name" } }, "team": { "x-jd": { "is": "workload.team" } } } },
    "container": { "properties": {
      "image":     { "x-jd": { "is": "workload.image" } },
      "variables": { "x-jd": { "is": "workload.variables" } },
      "ports":     { "items": { "properties": { "port": { "x-jd": { "is": "workload.port" } } } } } } },  // an array binds its first item
    "requires": { "items": { "x-jd": { "is": "requirement" }, "properties": {
      "type":  { "x-jd": { "is": "requirement.type", "ref": "component" } },
      "id":    { "x-jd": { "is": "requirement.id", "define": "requirement", "or": { "is": "requirement.type" } } },
      "class": { "default": "standard", "x-jd": { "is": "requirement.class" } } } } } } }
```

`define` with `or` gives the effective id (ADR 0011) and its uniqueness; `default: standard` replaces `Requirement.DefaultClass`.

## 4. Checks: where each one lives

S = plain JSON Schema, R = generic engine rule driven by annotations, C = still code (a concept's own rule). "Today" cites the code
(Appendix A has the lines). Every check keeps a negative test.

| Check today | Where | After | Why |
|---|---|---|---|
| YAML, one document, a mapping with `kind`; unknown kind | `CatalogParser` 138–171, each `*File`/`*Parser` | C (loader) | bootstrap |
| Retired kinds `ResourceType`/`Mapping`, policy `add` messages | `CatalogParser` 28–35, 167–179 | deleted (choice 3) | migration aids past S21 |
| Exactly one Catalog | 191–202 | R `one` | |
| Names unique in the shared namespace; policy names unique | 206–215 | R `define` | |
| Naming, roles, grant targets merged, key once | 578–598 | R `define` (map) | |
| Reserved names (`env`, `role`, …) | 258–264 | R: a `define`d part-owner name may not equal a bound expression root | roots are now bound, not listed |
| `uses` names shared resources | 269–293 | R `ref: shared-resource` | |
| `uses` direction, no cycles | 282–301 | R from `scope` + `layers` order | |
| Node names: pattern | 39, 324 | S `patternProperties` | |
| Node names: not a root, `runtime` only in a Runtime | 325–326 | R (`is: runtime`, `part.reads`) | |
| Roots and exports a part reads (boundaries 3, 5) | 338–371 | R from `part.reads`, `part.exports`, the enclosing `select`'s owner | |
| Node names unique per scope; not a shared resource's name | 377–395 | R from `scope` | |
| Grant: where, template listed, target form (1, 2) | 400–423 | C concept 9, located by `node.kind`, `grant.targets` | security |
| Policy scope shapes (`kind`, `runtime`, `require` only workload scope, `runtime.` paths) | 426–467 | S `oneOf` of two policy shapes, each with `message` | the shapes are static |
| Policy `require` names a component | 471–478 | R `ref: component` | |
| Policy paths overlap within one policy | 218–233 | C concept 10 | |
| Implementation keys and policy `environmentType` declared | 481–498 | R `ref` / `select` + `ref` | |
| Implementation keyed by a shared-layer type | 501–504 | R: a `select` on `env.type` under a part whose scope that layer never deploys | |
| Same classes per implementation | 508–519 | R (nested selects) | |
| Class exports equal contract; no `resource.*` in exports | 522–543 | R `contract`; R expression rule | |
| Probe interval ≤ timeout; runtime node must not set release inputs | 237–255 | C concept 11 | cross-field, release flow |
| Naming `allowed` is a regex accepting hex | 552–575 | C concept 4 (`name()`) | |
| Definition/descriptor type declared; `approval: true` needs the block (13) | `EnvironmentTypeCheck` | R `ref`; C concept 11 for the gate | security |
| Descriptor values: no reserved keys | `EnvironmentParser` 37–40 | deleted if dead (S24c verifies) | `env.*` no longer reads values since S21f |
| Descriptor values: no dotted keys | 68–86 | S `patternProperties` | |
| Approval: not qualified on itself, `required` ≤ approvers | 53–64 | C concept 11 | cross-field |
| Offers name components or runtimes; at most one runtime | `Expander` 60–93 | R `ref`; C concept 5 ("one runtime") | |
| Shared parts in the base; no repeated part (8) | 95–115 | C concept 5 | |
| Requirement offered, implementation, class (11) | 178–204 | R `select`; C offer check | |
| Effective ids unique | `WorkloadRules` 24–44 | R `define … or` | |
| Variables read only `${resource.<id>.<export>}` (6) | 46–76 | C, on the value bound `workload.variables` | security |
| Only the runtime node reads `resource.*`; requirement scopes apart (9) | `GraphBuilder` 94 | C concept 3 | security |
| Grant target after policies (1) | `GraphBuilder` 157–170 | C concept 9 | security |
| Secret never in the descriptor (12) | `DescriptorComposer` 25–33 | C concept 3 | security |
| Release set, records: schema + "is what its place says" (approvals.md §9) | `QualificationReader` 99–129, `ApprovalRecord` 107 | S + C concept 11, facts read by `is` | trust boundary |

### Security boundaries (D19, components.md §3)

The rule for every row: the **rule** stays in code; data only says **where** its inputs are. A schema that drops a binding the rule needs
is refused at start (**binding completeness**: every kind with a part binds `part.nodes`, `node.kind`, `node.template`, `node.config`; the
workload binds `workload.variables`; the runtime binds `runtime`; else jd stops with "schema X does not bind role Y"). Unbound fields are
inert: the engine reads nothing it has not bound, so a new workload field cannot carry an expression into the platform.

| # | Boundary | After S24 | Strength |
|---|---|---|---|
| 1, 2 | Grant targets own nodes; who may add a grant | C concept 9, nodes found by `node.kind`, input by `grant.targets` | as today |
| 3, 5 | What a part reads | R from `part.reads` and the owning `select` | as today |
| 4 | `env.*` bypass | C concept 4: `env.<x>` resolves only bound `env.*` roles | as today |
| 6 | Workload input into the platform | C on `workload.variables` + binding completeness + unbound = inert | as today |
| 7, 8 | Who deploys which scope | C concept 5, scopes from `scope`/`layers` | as today |
| 9, 10 | Requirement scopes apart; owner prefix | C concept 3 (ids unchanged) | as today |
| 11 | Unoffered component | C offer check | as today |
| 12 | Secret in the descriptor | C concept 3 | as today |
| 13 | Gated type without its gate | C concept 11 on `environment-type.gated` | as today |

**No boundary weakens with choice 1 (a)**: the schemas ship with jd, reviewed like code. **Blocker if choice 1 (b)** (schemas in the
catalog): rows 1, 2, 6 and 13 would hang on files the platform team edits without a jd release, e.g. unbinding `environment-type.gated`.
Binding completeness catches a removed binding, not a moved one (binding `node.kind` to a field no file sets). So (b) needs its own design.

## 5. Files jd writes

One generic **composer**: given a kind and a set of context values, it walks the kind's schema in property order and writes every field
bound by `is`; a required bound field without a value is an error; unbound fields are not written; the result is validated against the same
schema before it is written (as `DescriptorComposer` already re-parses today). The output schema is the allowlist, so "records are built from
an allowlist" (conventions) holds by construction.

| File | Today | After |
|---|---|---|
| Environment descriptor | `DescriptorComposer` 57–69 builds `{kind, name, region, type, offers, values, approval}` | composer over `environment.schema.json`: `env.*` copied from the definition's bound values, `env.values` = the deployed exports (secrets dropped, concept 3), `env.approval` the block |
| Release set | `ReleaseSetFile` 38–45, 85–95 | composer over `release-set.schema.json` (`release.label`, `release.created-at`, per workload `release.definition`, `…-sha256`, `…-depends-on`); read back by the same bindings |
| Release record | `ReleaseRecordBuilder` 38–174 | composer over `release-record.schema.json`: one `is: record.<fact>` per field; facts are the run report's (concept 11); `QualificationReader` reads them by role |
| Approval record | `ApprovalRecord` 19–46 (`Stored` types) | composer over `approval-record.schema.json`; `ApprovalRecord.ReadAsync` by role |
| Graph JSON (`preview --json`, the `graph.sha256` input) | `GraphJson` 16–70 | composer over a new `graph.schema.json`; bytes identical (property order = today's), proven by the goldens and record hashes |
| Preview text | `PreviewFormatter` | unchanged: it renders concepts (node, part reads), not a file |

Records written before stay readable: the schemas keep accepting `tier` (persisted-format rule).

## 6. Errors

- Every error stays `file: location: message` (`LoadError`). Schema errors keep today's location extraction (`LoadError.FromSchema`).
- **A rule that is data carries its message as data**: a subschema with `message` reports that text (with the location) instead of the
  validator's; a `ref` failure is generated as "'x' is not a <namespace> in the catalog (declared: …)"; a `select` miss as "<owner> has no
  <map> entry for <value> and no '<or>' entry". The namespace and map names come from the schema (`define`, the property name), so they read
  like today's messages.
- **A rule that is code keeps its message in code** (concepts 9–11), with the field name taken from the binding, not spelled.
- Error tests pin file, location and the contract part of the message, as today; a slice that changes a message says so in its brief.

## 7. Simplicity guard

How this stays a schema, not a language: annotations say **where**, never **how**. No conditions, loops, computed values or user-defined
roles; seven keywords and a closed role list, both checked by a meta-schema (`schemas/x-jd.schema.json`): an unknown keyword or role is an
error at the schema file. Every rule is still one generic C# rule.

| Design | Concepts added | For | Against | Verdict |
|---|---|---|---|---|
| **A. Annotated JSON Schema + generic document tree** | 7 keywords, 1 meta-schema | one source of shape and meaning; schemas already exist and are already loaded per kind; the validator keeps `x-*` (spike) | annotations make schemas longer; the role list is code | **recommended** |
| B. A declared meta-model file (`model.yaml`: kinds, fields, roles) beside the schemas | a new kind and loader | schemas stay plain | two sources of the same shape that can disagree; a second loader; more files (principle 7) | rejected |
| C. Generate C# models from the schemas | a code generator | type safety | the generated code still knows every field; the user's rule is about the code, generated or not | rejected |
| D. A general rule language (CUE/KCL, or rules in YAML) | a language | every check is data | a programming language in YAML; security rules become editable data | rejected (resolver.md already rejected CUE) |

What is left out on purpose: user-defined roles, roles bound in the catalog (choice 1), per-environment schemas, versioned meta-schemas.

## 8. Behaviour preservation

As in phase 1: the **deployed-config snapshot** (`src/jd.cli.tests/golden/deployed-config.json`) and the **graph goldens**
(`GoldenGraphTests`, `GoldenSubstrateTests`) are byte-identical through every slice; records, sets and descriptors written by jd are
byte-identical for the same inputs (a new golden of each, added in S24a's first commit); old records and sets still read. Error message
changes are listed per slice. **Azure gate** (conventions): none while the snapshot and the direct-Azure code are unchanged; S24e changes
`ReleaseFlow.cs` and `HttpRevisionProbe.cs` (values move to data, same values), so it runs the gate; the phase ends with one gate as its exit
evidence.

## 9. Slices and exit criteria

Each slice leaves `tools/verify.sh` green, the snapshot and goldens byte-identical, and **every file it touches at the full quality bar**
(conventions "Code quality bar", standards, review checklist): the reviewer judges the touched files whole, not only the diff, and the
debt listed for the slice in Appendix A is fixed in it, not carried. Lines are production lines (C#, schemas), estimated, added/removed.

| Slice | Content | Lines | Azure gate | Acceptance |
|---|---|---|---|---|
| **S24a** Documents and names | First commit: goldens of a written descriptor, release set, release record, approval record. Meta-schema; `SchemaIndex`; one `DocumentLoader` (kind → schema by the schemas' `kind` enum) replacing `CatalogDirectory`/`ReadDocumentAsync`, `EnvironmentFile`/`Parser`, `EnvironmentDefinitionFile`, `WorkloadFile`, `ReleaseDocument.ParseAsync` and the `*LoadResult` types; `define`, `ref`, `one`, `message`; shared `part`/`node`/`approval` schemas; node-name and dotted-key patterns as schema; retired-kind messages deleted. A **literal guard** test: no production C# string literal equals a property name declared in `schemas/`, with an allowlist that every later slice shrinks | ~+350 / −300 | no | snapshot, goldens, the four new goldens identical; one negative test per `define`/`ref`/`one`/`message` rule and for an unknown `x-jd` keyword or role; quality |
| **S24b** Parts from bindings | `is: part…`, `node.*`, `contract`, `part.reads`, `scope`, `layers`, `select`; the `Catalog` record family, `ImplementationKey`, `SharedScope` deleted; expander and graph builder read roles; the catalog checks of section 4 marked R become generic; binding completeness | ~+300 / −450 | no | goldens identical; boundaries 1–3, 5, 7–10 re-proven by their existing tests plus one per new generic rule; quality |
| **S24c** Context values and the workload | `env.*`, `workload.*`, `requirement.*`, `role.*`, `runtime` roots from `is`; the evaluator's root constants and `EnvironmentDescriptor`'s model deleted; `WorkloadRules` on `workload.variables`; `fn::entries` item keys declared beside the form (`{ fn::entries: workload.variables, keys: [name, value] }`); dead descriptor check removed | ~+150 / −200 | no | goldens identical; boundaries 4, 6, 11 tested, incl. an unbound workload field carrying `${cosmos-account.id}` being inert; quality |
| **S24d** Policies | policy schema `oneOf` (node / workload scope) with `message`; criteria bound (`requirement.type`, `requirement.class`, `env.type`, `node.template`); `MatchCriteria` deleted | ~+60 / −120 | no | goldens identical; every policy-scope negative test kept (messages from data); quality |
| **S24e** Runtime and release flow | `probe.*`, `release.*` by `is`; the Container Apps facts in code move to the runtime's data: suffix prefix `r`, full weight `100`, probe URL `https://{fqdn}{path}`; `Probe`/`RuntimeRelease` records deleted | ~+80 / −90 | **yes** (`ReleaseFlow.cs`, `HttpRevisionProbe.cs`) | snapshot identical; release-flow tests unchanged; quality |
| **S24f** Files jd writes | the composer; descriptor, release set, release and approval records, graph JSON through their schemas; `QualificationReader`, `ApprovalRecord`, `ReleaseRecordBuilder`, `ReleaseSetFile`, `GraphJson` field names gone | ~+200 / −300 | no | the S24a goldens and record hashes identical; old records and sets read; approvals.md §9 tests kept; quality |
| **S24g** Docs and standards | standards §1 rewritten ("allowed in code: the concepts of documents.md §2, the expression grammar; formats are data"); resolver.md, components.md §4, conventions "Data drives the platform"; literal-guard allowlist empty but class (b); an ADR (Proposed) for the vocabulary | docs | **yes**: the phase exit gate | E2E green; guard allowlist = Appendix A class (b) only |

Total about +1,140 / −1,460 production lines: net smaller, since `CatalogParser` (659 lines) shrinks to the concept rules.

**Phase 1.5 exit criteria** (proposed): (1) no production C# names a document field: the literal guard passes with only class (b) allowed;
(2) the same Azure resources and the same written documents for the same inputs: snapshot, graph goldens and S24a's document goldens
byte-identical through every slice; (3) every D19 boundary keeps its tests, and binding completeness has its own; (4) E2E green at the end.
**Roadmap change proposed:** insert phase 1.5 "The code knows no file structure" (serves N1, N10) before S22 and phase 2; phase 2's exit
"added as components only, no C# change" gains "and no schema change outside the component's own kinds".

**Later phases stay open:** phase 2's messaging and secrets are data only (component files, a `grants.yaml` line); N2 contract versions
are a field bound to `contract`'s version; N5 offers and N4 bind add roles to the closed list, one design note each; N6's registry is the
`env.values` role read from another store.

## 10. Open choices for the user

| # | Choice | Options | Recommendation |
|---|---|---|---|
| 1 | Where the annotated schemas live | (a) in `schemas/`, shipped with jd: a format change is data, no C#, but a jd release; (b) in the catalog, edited by the platform team | **(a)**: no boundary weakens (section 4); (b) is a blocker until designed |
| 2 | jd's own evidence (records, sets, graph JSON) in scope | (a) yes, through the composer and `record.*` facts; (b) no: jd's evidence format stays in code as its own contract (ADR 0007) | **(a)**: your rule covers every file; the composer is the same one the descriptor needs |
| 3 | Migration aids in the loader (retired kinds, policy `add` message) | delete / keep as data (a retired-kind schema) | **delete**: S21 is done; the schema's "unknown kind" suffices |
| 4 | Standards §1 "formats allowed in code" | rewrite as in S24g / keep | **rewrite**, or the rule allows what you ruled out |
| 5 | Container Apps facts in the release flow (suffix `r`, weight 100, `https`) | move to the runtime's data (S24e) / leave as release-flow constants | **move**: they are resource knowledge (standards §1) |
| 6 | Rules that become schema (`oneOf`) | messages via `message` / keep those rules in code | **`message`**: one place per rule, actionable text |

## 11. ADRs affected

| ADR | Effect |
|---|---|
| [0003](../decisions/0003-pulumi-yaml-template-library.md) | holds: templates and their protocol stay the adapter's (class b) |
| [0004](../decisions/0004-two-layer-definition-model.md) | holds: the layers' formats become annotated schemas |
| [0007](../decisions/0007-definition-upload-snapshot.md) | holds: the set is still embedded as received and hashed; its layout comes from its schema |
| [0011](../decisions/0011-requirement-id.md) | holds: the effective id is `define … or` |

New: one ADR (Proposed) for "the engine knows concepts, schemas bind fields" with the vocabulary, written in S24g.

## Appendix A. Inventory: code that knows a document's structure

Class: **(a)** must move to data; **(b)** an external protocol, stays an adapter constant (standards §1); **(c)** an engine concept of
section 2. "Goes" names the slice. Paths under `src/`.

### jd.resolver: catalog kinds

| File:line | What it knows | Class | Goes |
|---|---|---|---|
| `catalog/Catalog.cs:10-19` | the `Catalog` record: the catalog's file kinds as fields | a | S24b: `DocumentSet` + namespaces |
| `catalog/Catalog.cs:25,32-58` | `EnvironmentType`, `Component`, `Implementation`, `SharedResource`, `Part`, `Runtime`, `RuntimeImplementation`: each kind's fields | a | S24b (runtime parts S24e) |
| `catalog/Catalog.cs:45-49` | `SharedScope` enum `Shared`, `Environment` (the layer names) | a | S24b: `layers` in the Catalog document |
| `catalog/Catalog.cs:61-71` | `ImplementationKey.Default = "default"`, the type-or-default lookup | a / c | S24b: `select … or` (lookup c, key a) |
| `catalog/Catalog.cs:73-79` | `NodeKind` `create`/`grant`; `Node(Template, Kind, Config)` | c | concept 3/9; location by `is` |
| `catalog/Catalog.cs:82-94` | `MatchCriteria` keys `type`, `class`, `environmentType`, `template`, `kind`, `runtime`; `RuntimeKind` | a | S24d |
| `catalog/Catalog.cs:100,108-118` | `Probe` and `RuntimeRelease` fields (4 + 10) | a (fields) / c (concept 11) | S24e |
| `catalog/Catalog.cs:121-130` | `Policy`, `NamingRule` fields | a | S24d, S24b |
| `catalog/CatalogParser.cs:16-25` | the eight kind names | a | S24a: kinds from the schemas |
| `catalog/CatalogParser.cs:28-35` | retired kinds `ResourceType`, `Mapping`; removed field `add` | a | S24a: deleted (choice 3) |
| `catalog/CatalogParser.cs:39` | node-name pattern | a | S24a: schema `patternProperties` |
| `catalog/CatalogParser.cs:82,84,85` | merged maps `rules`, `roles`, `targets` | a | S24a: `define` |
| `catalog/CatalogParser.cs:108,124` | `environmentTypes`, `approval`, `layer`, `version` | a | S24a/b |
| `catalog/CatalogParser.cs:157,159` | the `kind` key | c | bootstrap (section 2) |
| `catalog/CatalogParser.cs:182` | schema file name built from the kind (`schemas/catalog/{kind}.schema.json`) | a | S24a: kind → schema from the schemas |
| `catalog/CatalogParser.cs:204` | the `name` field of named kinds | a | S24a: `define` |
| `catalog/CatalogParser.cs:248-251` | the runtime node and the release inputs it must not set | a / c | S24e |
| `catalog/CatalogParser.cs:307-311,547` | locations `implementations.<k>.environment.`, `…classes.<c>` | a | S24b: locations from the index |
| `catalog/CatalogParser.cs:550` | hex digits a naming rule must allow | c | concept 4 (`name()` hash) |
| `catalog/CatalogParser.cs:600-651` | `ToComponent` … `ToNode`: every field of every catalog kind (`name`, `exports`, `uses`, `implementations`, `environment`, `classes`, `scope`, `nodes`, `probe.*`, `release.*`, `reason`, `match`, `set`, `default`, `require`, `pattern`, `maxLength`, `allowed`, `template`, `kind`, `config`) | a | S24b/d/e |
| `catalog/CatalogDirectory.cs:7` | `templates` directory skipped | c | the catalog layout's one rule (templates are the adapter's files, not documents) |
| `EmbeddedSchema.cs`, `jd.resolver.csproj` | schema resource names | a | S24a: one schema registry |

### jd.resolver: environment definition and descriptor

| File:line | What it knows | Class | Goes |
|---|---|---|---|
| `environment/EnvironmentDefinition.cs:10-18` | `EnvironmentDefinition` fields; `EnvironmentDefinitionLoadResult` | a | S24a/c |
| `environment/EnvironmentDefinition.cs:23,54-55` | schema name; `name`, `region`, `type`, `offers`, `approval` | a | S24a |
| `environment/EnvironmentDescriptor.cs:11-37` | descriptor fields; `env.*` keys `name`, `region`, `type` | a | S24c: `is: env.*` |
| `environment/EnvironmentParser.cs:12,35-49` | schema name; `values`, `name`, `region`, `type`, `offers`, `approval` | a | S24a |
| `environment/EnvironmentParser.cs:68-86` | dotted keys refused, values flattened | a / c | S24a schema; flattening stays (concept 3) |
| `environment/EnvironmentFile.cs`, `EnvironmentLoadResult.cs` | a loader and result type per kind | a | S24a |
| `environment/ApprovalPolicy.cs:18-24` | block fields = C# property names, camel-cased | a | S24f: `approval.*` |
| `environment/DescriptorComposer.cs:56-69` | descriptor layout `kind: Environment`, `name`, `region`, `type`, `offers`, `values`, `approval` | a | S24f: composer |
| `environment/EnvironmentTypeCheck.cs:16-20` | locations `type`, `approval` | a | S24b (gate rule c) |

### jd.resolver: workload, expansion, expressions, policies, graph

| File:line | What it knows | Class | Goes |
|---|---|---|---|
| `workload/WorkloadFile.cs:7-55` | its own loader and `WorkloadLoadResult` | a | S24a |
| `expansion/Expander.cs:32-39` | `container`, `metadata.name`, `metadata.team`, `image`, `ports[0].port`, `variables` | a | S24c |
| `expansion/Expander.cs:153-158`, `:25` | `requires[].type`, `id`, `class`; default class `standard` | a | S24c: `requirement.*`, schema `default` |
| `expansion/Expander.cs:15,18` | `substrate` scope, `@` owner prefix | c | ids (concept 3) |
| `expansion/Expander.cs:76,208,261` | locations `implementations.<k>.…` | a | S24b |
| `expansion/ConfigWalker.cs:13-18` | `fn::entries`, its source `workload.variables`, location `container.variables` | c / a | form c; source and location S24c |
| `policies/PolicyApplier.cs:20` | rule name `container.variables` | a | S24c |
| `policies/PolicyApplier.cs:78-95` | criteria keys | a | S24d |
| `expressions/ExpressionEvaluator.cs:16-26,44` | roots `env`, `role`, `workload`, `resource`, `runtime`; fields `name`, `team`, `image`, `port` | a (names) / c (grammar) | S24c: roots from `is` |
| `expressions/BuiltIns.cs:10-14,32-35` | `name`, `guid`, namespace GUID, placeholders `{workload}` `{id}` `{env}` `{hash}` | c | concept 4 |
| `graph/GraphBuilder.cs:22` | `@workload` scope | c | ids |
| `graph/GraphBuilder.cs:198-200` | node hash object `template`, `kind`, `config` | c | hash contract (H47) |
| `graph/ConfigJson.cs:17`, `jd.orchestrator/ConfigFiller.cs:66` | `fn::entries` items `name`, `value` (the container-app template's input shape) | a | S24c: `keys:` beside the form |
| `graph/GraphJson.cs:16-70` | graph JSON layout, incl. probe and release fields | a | S24f |
| `LoadError.cs:11` | NJsonSchema's error text form | b | library protocol |

### jd.resolver and jd.orchestrator: release manifest, set, records

| File:line | What it knows | Class | Goes |
|---|---|---|---|
| `release/ReleaseDocument.cs:10-13` | four schema names | a | S24a |
| `release/ReleaseBuilder.cs:47-75` | manifest `workloads`, `definition`, `dependsOn`, `label` | a | S24f |
| `release/ReleaseBuilder.cs:98-106` | workload `container.image`, `metadata.name`, `metadata.team` | a | S24c (same bindings as the expander) |
| `release/ReleaseSetFile.cs:38-45,76-100` | set layout `kind`, `label`, `createdAt`, `workloads[].definitionSha256/dependsOn/definition` | a | S24f |
| `release/ReleaseRecordFile.cs:21-36` | activity and outcome values `deploy`, `qualify`, `approve`, `succeeded`, `passed`, `unchanged` | c (fact values) | concept 11; the fields holding them S24f |
| `release/ReleaseRecordFile.cs:15,39,42` | `set.yaml`, time formats | c | the local store's layout and the one time form |
| `release/QualificationReader.cs:80-172` | record fields `identity.objectId`, `activity`, `environment.name`, `release.label`, `release.setSha256`, `finishedAt`, `outcome`, `workloads[].name`, `oq.status`, `iq.status` | a | S24f: `record.*` |
| `release/ApprovalRecord.cs:19-46` | `Stored*` records = the approval record layout, `kind: ApprovalRecord` | a | S24f |
| `jd.orchestrator/ReleaseRecordBuilder.cs:38-174` | the whole release-record layout (~45 field names), `kind: ReleaseRecord`, `"failed"`, `"notRun"` | a | S24f |
| `jd.orchestrator/ReleaseFlow.cs:29,32` | revision suffix prefix `r`, full traffic weight `100` (Container Apps rules) | a | S24e: runtime data |
| `jd.orchestrator/ReleaseFlow.cs:30-31` | suffix hash lengths | c | jd's suffix scheme |
| `jd.orchestrator/ReleaseFlow.cs:36-190`, `Qualifier.cs:73` | release block and probe by concept | c | concept 11 (already data-named) |

### jd.cli, jd.core, jd.definitionvalidator

| File:line | What it knows | Class | Goes |
|---|---|---|---|
| `jd.cli/HttpRevisionProbe.cs:22` | probe URL `https://{fqdn}{path}` (Container Apps ingress) | a | S24e: probe data |
| `jd.cli/Cli.cs:28`, `ApproveCli.cs:19-20`, `PromoteCli.cs:20-21` | commands and options | c | the CLI's own surface |
| `jd.cli/DataDirectory.cs:15-20` | `JD_DATA_DIR`, `jd`, `state`, `state.key`, `records` | c | configuration (connections.md) |
| `jd.cli/EnvCli.cs:101`, `PreviewFormatter.cs`, `DeployFormatter.cs` | messages over concepts | c | — |
| `jd.definitionvalidator/WorkloadRules.cs:16-78` | `requires`, `id`, `type`, `container.variables`, `resource.` | a / c | S24a (`define`), S24c (row 6 rule c) |
| `jd.definitionvalidator/WorkloadSchema.cs:8` | embedded schema name | a | S24a |
| `jd.definitionvalidator/YamlSchemaValidator.cs` | YAML → JSON, validation | c | the loader's parser |
| `jd.core/**` | ports and DTOs (config entries, packages, changes, record store) | c | no document structure |

### Adapters (class b, stay)

| File:line | What it knows |
|---|---|
| `backend-providers/jd.bp.pulumi/TemplateLibrary.cs:21,67-69,88` | `Pulumi.yaml`, `configuration`, `outputs`, `default` |
| `backend-providers/jd.bp.pulumi/StackNames.cs:15-17` | Pulumi stack name length |
| `backend-providers/jd.bp.pulumi/PulumiBackendOptions.cs:8`, `EnvironmentVariableAttribute.cs` | `PULUMI_*` variables |
| `backend-providers/jd.bp.pulumi/PulumiResourceChangeParser.cs` | Pulumi Automation change types |
| `jd.azure/ArmCredentials.cs:13-18` | `ARM_*` variables |
| `jd.azure/ArmDeployerIdentity.cs:20-22` | Entra claims `tid`, `appid`, `oid` |
| `jd.azure/ArmLiveRevisions.cs` | ARM SDK revision shapes |

`samples/sample-app` is the workload's own application (it reads its variables), not platform code: out of scope.

### Quality debt to fix in the slice that touches it

| Slice | Debt (rule) |
|---|---|
| S24a | the parse-and-validate block copied five times (`CatalogParser.cs:138-185`, `EnvironmentDefinition.cs:34-49`, `EnvironmentParser.cs:18-32`, `WorkloadFile.cs:30-45`, `ReleaseDocument.cs:20-38`) (duplication); `catch (Exception)` around YAML parsing where `YamlException` is meant (`CatalogParser.cs:145` and the four copies) and around schema loading (`YamlSchemaValidator.cs:173-176`, turns a broken embedded schema into a validation error) (errors); `EnvironmentDefinitionFile.Strings` used by `EnvironmentParser` (a helper on the wrong type); the `node` definition copied in three catalog schemas and the `approval` block in two (duplication in data); `YamlSchemaValidator.ValidateAsync(path)`/`ValidateContentAsync`, used only by tests (code no production path needs) |
| S24b | `CatalogParser` 659 lines, one type for parsing and fifteen checks (one responsibility); nested ternary chains in `CheckReads` (`:353-363`) and `CheckNodeNames` (`:323-327`) (structure); `Reader` with 8 positional fields (`:48-52`) and `ExpansionResult` with 11 (`ExpandedModel.cs:62-73`) (long parameter lists); `Expander.ExpandEnvironment` 78 lines (`:49-127`) (method doing several things) |
| S24c | `ExpressionContext` with 11 positional arguments (`Expander.cs:172-174`, `DescriptorComposer.cs:34-36`) (long parameter list); `WorkloadRules` returns `"location: message"` strings re-split in `WorkloadFile.cs:48-52` (stringly typed errors); the descriptor reserved-values check (`EnvironmentParser.cs:37-40`), probably dead since S21f: prove with a test or delete (untested branch) |
| S24d | `MatchCriteria.RuntimeKey` and `RuntimeKind` both `"runtime"` with different meanings (names) |
| S24e | `RuntimeRelease` 10 positional strings (long parameter list) |
| S24f | `"failed"` spelled three times in `ReleaseRecordBuilder.cs:64,71,167` and `"notRun"` beside a constant set (literals); null-forgiving chains in `QualificationReader.cs:80-172` (`!` without a reason) |

## Appendix B. Roles (the closed list)

| Group | Roles |
|---|---|
| Structure | `part`, `part.nodes`, `part.exports`, `part.reads`, `node.template`, `node.kind`, `node.config`, `contract`, `runtime`, `grant.targets`, `naming.rules`, `roles`, `catalog.version`, `layers`, `environment-type.layer`, `environment-type.gated` |
| Environment | `env.name`, `env.region`, `env.type`, `env.offers`, `env.values`, `env.approval`, `approval.qualified-on`, `approval.required`, `approval.approvers`, `approval.separate` |
| Workload | `workload.name`, `workload.team`, `workload.image`, `workload.port`, `workload.variables`, `requirement`, `requirement.type`, `requirement.id`, `requirement.class` |
| Policy | `policy.name`, `policy.reason`, `policy.match`, `policy.set`, `policy.default`, `policy.require` |
| Runtime | `probe.path`, `probe.expected-status`, `probe.timeout`, `probe.interval`, `probe.url`, `release.suffix-input`, `release.traffic-input`, `release.traffic-output`, `release.suffix-output`, `release.revision-output`, `release.fqdn-output`, `release.revision-key`, `release.latest-key`, `release.weight-key`, `release.resource-id-output`, `release.suffix-prefix`, `release.full-weight` |
| Release | `release.label`, `release.created-at`, `release.workloads`, `release.definition`, `release.definition-path`, `release.definition-sha256`, `release.depends-on` |
| Evidence | `record.<fact>`: one role per field of today's `release-record` and `approval-record` schemas, named by today's path (`record.identity.objectId`, `record.iq.status`, …); the facts are the run report's |

Namespaces (`define`/`ref`): `part-owner` (components, runtimes, shared resources), `component`, `shared-resource`, `runtime`, `policy`,
`environment-type`, `layer`, `naming-kind`, `role`, `template` (grant targets), `requirement` (per workload), `environment`.
