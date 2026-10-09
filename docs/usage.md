# Using the CLI

`jd` (project `src/jd.cli`) is the composition root: it parses arguments, wires the validator and the
resolver ([resolver.md](architecture/resolver.md)), prints results and sets the exit code. It holds no
resolution logic. Nothing is read from default locations; every input is an argument.

```
dotnet run --project src/jd.cli -- validate <workload.yaml>
dotnet run --project src/jd.cli -- preview <workload.yaml> --env <environment.yaml> --catalog <dir> [--json]
dotnet run --project src/jd.cli -- release create <manifest.yaml> --out <release-set.yaml>
dotnet run --project src/jd.cli -- release show <release-set.yaml>
dotnet run --project src/jd.cli -- --help
```

| Command | What it does |
|---|---|
| `validate` | Checks the workload against `workload.schema.json` (embedded in `jd.definitionvalidator`) and the workload rules. Prints one error per line as `file: location: message`. |
| `preview` | Validates the workload first, then loads the catalog and environment descriptor, expands requirements, applies policies and builds the graph. Prints each node in graph order with id, template, kind, phase, stack, short hash, dependencies and every config field with its value (`pending: <expression>` when it waits for a runtime or dependency output; `(from env.<path>)` when read from the environment) and provenance (layer and rule). `--json` prints the graph as the stable JSON the golden snapshot pins instead. |

Errors from any stage are printed to stderr with file and location; results go to stdout.

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | Validation or resolution errors (workload, catalog, environment or graph) |
| 2 | Usage error: unknown command or option, missing argument, unreadable file or directory |

## Releases

A release is a pinned **set** of workloads under one label, deployed in dependency order
([release.md](architecture/release.md), C53). You author a manifest; the platform generates the set.

```yaml
# samples/releases/sample.manifest.yaml
kind: ReleaseManifest
label: 1.0.0
workloads:
  - definition: ../workloads/sample-app.yaml      # relative to the manifest
    dependsOn: [just-deliver-sample-worker]       # workload names; ordering only
  - definition: ../workloads/sample-worker.yaml
```

| Command | What it does |
|---|---|
| `release create <manifest> --out <set>` | Validates every listed workload definition, requires each image to be pinned by digest (`@sha256:` and 64 hex digits; a tag is rejected), checks names and dependencies, works out the deploy order and writes the release set. Refuses to overwrite an existing file: sets are immutable, so a changed release gets a new file. Prints every problem as `file: location: message`. |
| `release show <set>` | Re-verifies each embedded definition against its recorded SHA-256, then prints the label, the deploy order and, per workload, team, definition SHA-256, image and dependencies. A modified set is an error (exit 1). |

Exit codes are the same as above (a manifest, set or `--out` path problem is 2; invalid content is 1).
Resolving each workload and deploying a set is not part of these commands yet.
