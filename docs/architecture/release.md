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
  - name: just-deliver-sample-app
    team: platform-team
    definitionSha256: 919b...      # lowercase hex SHA-256 over the exact bytes of the definition file
    image: ghcr.io/...@sha256:...  # pinned by digest
    dependsOn: [just-deliver-sample-worker]
    definition: |                  # the definition as received: the set is self-contained
      apiVersion: just-deliver/v1
      ...
order: [just-deliver-sample-worker, just-deliver-sample-app]
```

A committed example is the golden file `src/jd.resolver.tests/golden/sample.release-set.yaml`.

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
- The embedded definition keeps the exact text, including line endings (CRLF is written as an escaped quoted string
  because a block scalar would turn it into LF). Loading a set recomputes each SHA-256 over the embedded text and
  fails on a mismatch: editing a set by hand is an error, not a silent change.
- The recorded `name`, `team` and `image` are copies of what the definition says; the hash covers the definition,
  not these copies, and loading does not cross-check them yet.

## Not here yet

Resolving each workload against the catalog and environment and deploying a set in order (S13b); the release
record per environment (S17). The resolved graph is not part of the set, so `GraphJson` does not carry the team.
