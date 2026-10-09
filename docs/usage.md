# Using the CLI

`jd` (project `src/jd.cli`) is the composition root: it parses arguments, wires the validator and the
resolver ([resolver.md](architecture/resolver.md)), prints results and sets the exit code. It holds no
resolution logic. Nothing is read from default locations; every input is an argument.

```
dotnet run --project src/jd.cli -- validate <workload.yaml>
dotnet run --project src/jd.cli -- preview <workload.yaml> --env <environment.yaml> --catalog <dir> [--json]
dotnet run --project src/jd.cli -- --help
```

| Command | What it does |
|---|---|
| `validate` | Checks the workload against `workload.schema.json` (embedded in `jd.definitionvalidator`) and the workload rules. Prints each error as `file: message`. |
| `preview` | Validates the workload first, then loads the catalog and environment descriptor, expands requirements, applies policies and builds the graph. Prints each node in graph order with id, template, kind, phase, stack, short hash, dependencies and every config field with its value (`pending: <expression>` when it waits for a runtime or dependency output) and provenance (layer and rule). `--json` prints the graph as the stable JSON the golden snapshot pins instead. |

Errors from any stage are printed to stderr with file and location; results go to stdout.

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | Validation or resolution errors (workload, catalog, environment or graph) |
| 2 | Usage error: unknown command or option, missing argument, unreadable file or directory |
