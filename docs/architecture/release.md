# Release set

> Status: implemented offline (S13). Addresses [C53](../open-questions.md); the snapshot follows
> [ADR 0007](../decisions/0007-definition-upload-snapshot.md). Using it: [usage.md](../usage.md#releases).

A release is the unit that is deployed and promoted: **one label over a pinned set of workloads**, deployed in
dependency order. A single workload is a set of one. Two files carry it.

```
release manifest  (authored)  --jd release create-->  release set  (generated, immutable)
```

Both formats have a JSON Schema in [`schemas/`](../../schemas/) (`release-manifest.schema.json`,
`release-set.schema.json`), embedded in `jd.resolver` and validated with the same validator as the other formats.
The code is `src/jd.resolver/release/`; the CLI only wires it.

## Release manifest

```yaml
kind: ReleaseManifest
label: 1.0.0                      # ^[0-9]+\.[0-9]+\.[0-9]+(-[a-z0-9.]+)?$
workloads:
  - definition: ../workloads/sample-app.yaml    # path relative to the manifest
    dependsOn: [just-deliver-sample-worker]     # workload names (metadata.name)
  - definition: ../workloads/sample-worker.yaml
```

Ordering is a release concern, so `dependsOn` lives here and not in workload definitions. It is ordering only:
it never grants access to another workload's resources (D19 still holds).

## Release set

```yaml
kind: ReleaseSet
label: 1.0.0
createdAt: 2026-01-02T03:04:05Z   # UTC, to the second
workloads:
  - definitionSha256: 919b...      # lowercase hex SHA-256 over the exact bytes of the definition file
    dependsOn: [just-deliver-sample-worker]
    definition: |                  # the definition as received: the set is self-contained
      apiVersion: just-deliver/v1
      ...
```

Only what cannot be derived is stored: the definitions, their hashes and `dependsOn`. A workload's name, team and
image come from its definition, and the deploy order from the dependencies, by the same code that created the set
(`ReleaseBuilder.ComposeAsync`). `jd release show` prints the derived values. A committed example is the golden file
`src/jd.resolver.tests/golden/sample.release-set.yaml`.

## Rules

`release create` collects every problem (`file: location: message`) before it writes anything:

- every workload definition passes the workload schema and rules (the same loader as `jd validate`);
- the image is pinned by digest: `@sha256:` and 64 hex digits. A tag can move under a release, so it is rejected;
- workload names are unique; every `dependsOn` names a workload in the manifest; none names itself;
- the dependencies have no cycle (the error lists the members, `a -> b -> a`);
- the deploy order is a topological sort, dependencies first, ties broken by name (ordinal).

Name and dependency checks run only when every definition loaded, since an unreadable definition has no name yet.

## Immutability and integrity

- `createdAt` is the only non-deterministic field; everything else follows from the manifest and the files it lists.
- The writer never overwrites a file. A changed release is a new set (and a new label).
- Loading checks each embedded definition against its recorded SHA-256, then runs every create-time rule on the
  verified definitions and `dependsOn` (valid definition, digest pin, unique names, known dependencies, no cycle) and
  derives name, team, image and order. The in-memory set is built only from that, so no stored field can disagree
  with a definition. Hand-editing a definition fails the hash; an edited `dependsOn` must still pass every rule.
  `label`, `createdAt` and `dependsOn` are not hashed — like the definitions' hashes themselves, they are anchored
  by the release record (S17).
- The hash inside the file is a consistency check: someone who edits a definition and its hash together produces a
  set that still loads. The external anchor is the release record kept per environment (S17), which holds the
  hashes the platform actually received.
- Encoding: definitions must be UTF-8 (anything else is "not valid UTF-8" at that file). The SHA-256 covers the raw
  bytes, including a UTF-8 byte order mark if there is one; the YAML parser gets the text without it.
- The embedded text keeps line endings exactly (CRLF, and text starting with white space or a byte order mark, are
  written as an escaped quoted string because a block scalar would change them); other definitions are readable
  literal blocks.

## Not here yet

The release record per environment (S17). Deploying a set (`jd release deploy`, infrastructure only) is described in
[provisioning.md](provisioning.md#release-sets-jd-release-deploy). The resolved graph is not part of the set, so `GraphJson` does not carry the team.
