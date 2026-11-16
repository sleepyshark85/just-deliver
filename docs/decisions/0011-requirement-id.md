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
- **Uniqueness is validated, not conventional** — the effective id is unique within the workload, checked when the definition is
  loaded (`jd validate`, before upload; H50).
- **`id` is length- and charset-constrained** because it feeds generated resource names: the workload schema
  ([workload.schema.json](../../schemas/workload.schema.json)) gives `type`, `id` and `class` one pattern,
  `^[a-z][a-z0-9-]{1,14}[a-z0-9]$` — 3 to 16 characters, lowercase letters, digits and hyphens, starting with a letter, not ending
  with a hyphen. Hyphens are allowed so a hyphenated type can be its own default id. A target's own limits (length, allowed
  characters) are the catalog's naming rules' job, not the id's.
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

**Changed since first written (for the user's review):** the id was to be about 12 lowercase alphanumerics; the schema allows up to
16 characters with hyphens, and the catalog's naming rules fit each name to its target.

## Consequences
- Resource identity is `(workload, id, type)`, never position in `requires` (reordering must not destroy resources; see C52).
- Each generated name is checked against its target's limits by the catalog's naming rules (C14).

## Related
- [0017](0017-catalog-layer-policies-require.md), [0010](0010-replacement-protection-classes.md), [architecture/model.md](../architecture/model.md)
- [open-questions](../open-questions.md): C12, C14, C16, C52, H50
