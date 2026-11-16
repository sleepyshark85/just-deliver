# 0019. The engine knows concepts, schemas bind fields

- **Status:** Proposed — pending review
- **Origin:** S24, phase 1.5 ([documents.md](../architecture/documents.md), approved by the user 2026-10-19); written in S24g

## Context
Until phase 1.5 the C# mirrored every document format: a model per file kind, a loader per kind, field names and locations spelled in
code (`body["environmentTypes"]`, `$"implementations.{key}.classes"`). A renamed field, a new kind or a new component shape was a C#
change, and the schemas in `schemas/` were only a first gate in front of a second, hand-written model of the same shape. The user
(2026-10-19): *"the code shouldn't know the structure of any yaml file"*.

## Decision
- **The engine knows concepts, never fields.** A concept is something an algorithm consumes; a field is where a document keeps it. The
  closed list ([documents.md](../architecture/documents.md) §2): document (`kind` the one bootstrap field), name and reference, part,
  node and export, expression (the grammar and its closed root list), scope (the layers in deploy order), selection, context value,
  contract, grant, policy, and release, gate and evidence.
- **Schemas bind fields.** Each kind's JSON Schema (draft-07, shipped with jd in `schemas/`) is its shape, and its `x-jd` annotations
  say which field holds which concept. The vocabulary, checked by the meta-schema `schemas/x-jd.schema.json`:
  - **nine keywords**, each with one meaning: `is: <role>` (the field holds the role's value), `define: <namespace>` (defines names),
    `ref: <namespace>` (each must be a defined name), `select: <role>` (the map entry keyed by the role's value), `fallback: <key>`
    (the entry used when none matches), `scope: <layer | workload>` (a part's scope), `match: <role>` (a policy criterion), `one: true`
    (exactly one document of the kind), `message: <text>` (the error text when that subschema fails);
  - **a closed list of roles** ([documents.md](../architecture/documents.md) Appendix B), each `one` or `many`, in groups: structure
    (`owner`, `part`, `node.template`, `contract`, `layer`, …), environment (`env.*`, `approval.*`), workload (`workload.*`,
    `requirement.*`), policy (`policy.*`), runtime (`probe.*`, `rollout.*`) and release (`release.*`);
  - **namespaces** for `define`/`ref`, spelled as error messages read them (`component`, `shared resource`, `environment type`, …);
  - two plain JSON Schema annotations the binder honours: `default` (an absent field binds the default) and the order of the `layer`
    enum (the deploy order).
- **Three generic pieces, no type per file kind:** `DocumentLoader` (YAML to a `Document`, schema by `kind`), `SchemaIndex` (per kind:
  role to paths, namespaces), `Binder` (fills typed concept records from a document's roles, each value with where it was read, and
  writes the YAML jd writes from them).
- **Annotations say where, never how:** no conditions, loops, computed values or user-defined roles. A rule that is data carries its
  message as data (`message`, `ref`, `select`); a concept's own rule stays code, with each field name taken from the binding. Security
  boundaries stay code; data only says where their inputs are, and binding completeness stops jd when a schema leaves a boundary's role
  unbound.
- **Stated exceptions** (documents.md §2): the `kind` and `x-jd` keywords; concept and root names that equal a field; external protocols
  (`Pulumi.yaml`, Entra claims, ARM); jd's identifiers and node hash; jd's JSON evidence (release record, approval record, graph JSON) as
  typed internal code beside its schema. The literal guard test allows only these and prose a lone word cannot be told apart from.

## Consequences
- A renamed field or a new component shape is a schema change, not a C# change; phase 2's "added as components only" holds by structure.
- Every error is `file: location: message`, the location where the binder read the value.
- The schemas grow longer, and the role list is code: a new concept (N4, N5) adds roles to the closed list, one design note each.
- The schemas ship with jd and are reviewed like code; moving them into the catalog would put security boundaries on data the platform
  team edits without a jd release, and needs its own design.
- Standards §1 no longer allows formats in code ("formats are data").

## Related
- [architecture/documents.md](../architecture/documents.md) (design, inventory, roles), [architecture/model.md](../architecture/model.md)
- [0003](0003-pulumi-yaml-template-library.md) (templates stay the adapter's), [0007](0007-definition-upload-snapshot.md),
  [0011](0011-requirement-id.md) (the effective id is concept 2's rule on bound values), [0017](0017-catalog-layer-policies-require.md)
