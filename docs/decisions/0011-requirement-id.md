# 0011. Resource requirements carry an optional `id`

- **Status:** Proposed — pending review
- **Origin:** open-questions C13

## Context
A workload may need several resources of the same type (e.g. two databases). The `requires` list had no way to distinguish them, and references used `${resource.<type>.<output>}`.

## Decision
Every resource requirement carries an optional `id`, defaulting to the type name.

- Existing single-resource manifests keep working; `${resource.database.host}` still resolves. Reference syntax becomes `${resource.<id>.<output>}`, where `id` defaults to `<type>`.
- Two requirements declared without ids both default to `database`, collide, and fail validation with a message telling the team to name them — the correct failure.

**Riders:**
- **Uniqueness is validated, not conventional** — a schema rule, scoped within the workload, caught pre-upload (H50).
- **`id` is length- and charset-constrained** because it feeds generated resource names. With `metadata.name` allowing 40 characters and storage accounts capped at 24 lowercase alphanumerics with no hyphens, the budget is roughly **12 characters, lowercase alphanumeric**. Tightens C14.
- **`id` is an identity, not a label.** Renaming is delete plus create: `id: db` → `id: primary` drops a database and provisions a fresh empty one. [0010](0010-replacement-protection-classes.md)'s `protect` catches it and C16 turns it into an approval, so it fails safely, but it must be documented rather than discovered.

Isolation is **not** expressed through `id`: `id` says *which* resource, `class` says *how isolated* (C12).

```yaml
requires:
  - type: database                 # id defaults to "database", shared server
  - type: database
    id: reporting                  # second database, same shared server
  - type: database
    id: ledger
    class: dedicated               # its own server - different substrate tier
```

## Consequences
- Resource identity is `(workload, id, type)`, never position in `requires` (reordering must not destroy resources; see C52).
- Pending workload schema change: add `id` and `class` to `requires` items (H50).
- Naming scheme (C14) must be derived from the tightest target; it currently does not close.

## Related
- [0004](0004-two-layer-definition-model.md), [0010](0010-replacement-protection-classes.md), [architecture/workload-definition.md](../architecture/workload-definition.md), [architecture/resolver.md](../architecture/resolver.md)
- [open-questions](../open-questions.md): C12, C14, C16, C52, H50
