# Release set

A release is the unit that is deployed and promoted: **one label over a pinned set of workloads**, deployed in dependency order. A single
workload is a set of one. Addresses [C53](../open-questions.md); the snapshot follows [ADR 0007](../decisions/0007-definition-upload-snapshot.md).
Commands: [usage.md](../usage.md#releases).

```
release manifest  (authored)  --jd release create-->  release set  (generated, immutable)
```

Both are documents with a schema ([`release-manifest.schema.json`](../../schemas/release-manifest.schema.json),
[`release-set.schema.json`](../../schemas/release-set.schema.json)), embedded in `jd.resolver` and loaded by the same validator as the other
formats. The use cases `CreateRelease` and `ShowRelease` run the rules below; the code is `src/jd.resolver/release/`.

## Release manifest

```yaml
kind: ReleaseManifest
label: 1.0.0
workloads:
  - definition: ../workloads/sample-app.yaml    # relative to the manifest
    dependsOn: [just-deliver-sample-worker]     # workload names (metadata.name)
  - definition: ../workloads/sample-worker.yaml
```

The label is a semantic version (the schema's pattern); it also names the records' directory, whose store refuses `..` and separators. Ordering is a release
concern, so `dependsOn` lives here and not in workload definitions. It is ordering only: it never grants access to another workload's
resources (D19).

## Release set

The set stores only what cannot be derived: per workload the definition as received, its SHA-256 (lowercase hex over the exact bytes) and
`dependsOn`, plus the label and `createdAt` (UTC, to the second). A workload's name, team and image, and the deploy order, are derived from
the definitions by the same code that created the set. The golden `src/jd.resolver.tests/golden/sample.release-set.yaml` is an example.

## Rules

`release create` collects every problem before it writes anything:

- every workload definition passes the workload schema and rules (the same loader as `jd validate`);
- the image is pinned by digest: `@sha256:` and 64 hex digits. A tag can move under a release, so it is rejected;
- workload names are unique; every `dependsOn` names a workload in the manifest; none names itself;
- the dependencies have no cycle (the error lists the members, `a -> b -> a`);
- the deploy order is a topological sort, dependencies first, ties broken by name (ordinal).

Name and dependency checks run only when every definition loaded, since an unreadable definition has no name yet.

## Immutability and integrity

- `createdAt` is the only non-deterministic field; everything else follows from the manifest and the files it lists. Creating a set again
  therefore gives other bytes.
- The writer never overwrites a file. A changed release is a new set and a new label.
- Loading checks each embedded definition against its recorded SHA-256, then runs every create rule on the verified definitions and
  `dependsOn`, and derives name, team, image and order. No stored field can disagree with a definition: an edited definition fails its hash,
  an edited `dependsOn` must still pass every rule.
- The hash inside the file is a consistency check only: someone who edits a definition and its hash together produces a set that still
  loads. The external anchor is the set kept beside the release records, whose SHA-256 every record and approval pins
  ([release-record.md](release-record.md#1-where-and-how-it-is-stored)).
- Encoding: definitions must be UTF-8. The SHA-256 covers the raw bytes, a byte order mark included; the YAML parser gets the text without it.
- The embedded text keeps line endings exactly: CRLF text, and text starting with white space or a byte order mark, is written as an
  escaped quoted string, because a block scalar would change it; other definitions are literal blocks.
- The resolved graph is not part of the set: it is resolved per environment when the set is deployed
  ([provisioning.md](provisioning.md)).
