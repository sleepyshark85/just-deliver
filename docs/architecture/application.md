# A thin CLI over use cases

The CLI is a starting app: jd's north star is a hosted API that a CLI and a web app call for everything
([roadmap](../plans/roadmap.md), [ADR 0001](../decisions/0001-custom-build-api-and-clients.md): each client is a thin layer). So every platform
rule (the gate, the identity, the order of a recorded run, the approval checks, records, the outcome) lives in use cases that any host calls
the same way, and `jd.cli` only parses arguments, reads and writes the files they name, formats, and composes. No HTTP, hosting or DTOs are
built yet.

## 1. Use cases

The use cases are in `src/jd.orchestrator/usecases/` (namespace `jd.orchestrator.usecases`), the layer [standards](../engineering/standards.md)
§3 names *application*, beside the walk engine. Every use case has one shape:

```csharp
static Task<TResult> RunAsync(TInput input, Connections connections, Action<Progress>? progress, CancellationToken cancellationToken)
```

It takes `Connections` only when it reaches outside jd and `Action<Progress>?` only when it reports progress: `Validate`, `Preview` and
`ShowRelease` take neither. A use case checks its plain values (a label, a meaning) before it parses any document.

| Action | Use case | Input | Result (besides `Errors`, `Problem`, `Outcome`) |
|---|---|---|---|
| `validate` | `Validate` | `Workload` | — |
| `preview` | `Preview` | `Workload`, `Environment`, `Catalog` | `Graph` |
| `deploy` | `Deploy` | `Workload`, `Environment`, `Catalog`, `Preview` | `Run` |
| `env up` | `EnvUp` | `Definition`, `Catalog`, `Base` | `Run`, `Environment`, `Descriptor` (bytes) |
| `release create` | `CreateRelease` | `Manifest`, `Definitions` (a `DocumentReader`) | `Set`, `Document` (the set file's bytes) |
| `release show` | `ShowRelease` | `Set` | `Set`, `CreatedAt` |
| `release deploy` | `DeployRelease` | `Set`, `Environment`, `Catalog`, `Preview` | `Environment`, `Set`, `Runs`, `Summary`, `Iq`, `Record` |
| `release qualify` | `QualifyRelease` | `Set`, `Environment`, `Catalog` | `Environment`, `Set`, `Iq`, `Record` |
| `approve` | `Approve` | `Label`, `Environment`, `Meaning` | `Environment`, `Approver`, `Counted`, `Record` |
| `promote` | `Promote` | `Label`, `Target`, `Catalog` | `Environment`, `SetSha256`, `Release` (a `DeployReleaseResult`) |

**Inputs are documents, never paths.**

- `SourceDocument(Name, Content)`: the name errors print (the CLI passes the argument as typed) and the exact bytes, since hashes and the strict
  UTF-8 check need bytes. `Text()` decodes UTF-8 with the byte order mark detected, as `File.ReadAllText` does; each kind's loader decodes,
  the CLI never does. The definitions a manifest names stay strict UTF-8 with the mark kept, decoded by the release builder.
- `SourceCatalog(Name, Documents)`: the catalog argument as typed and every `*.yaml` under it, named relative to it. The catalog loader splits
  off the templates part by the layout rule. The CLI reads the catalog before the use case runs, so an unreadable catalog file is the CLI's error.
- `DocumentReader`: how a use case reads a document another one names (the definitions of a manifest); the CLI's resolves a reference against
  the manifest's directory.

**Results are data:** the existing types (`ResolvedGraph`, `RunReport`, `WorkloadRun`, `IqResult`, `ReleaseSet`, `EnvironmentDescriptor`), the
evidence (record name, hashes, principal) and:

- `Errors`: located `LoadError`s (§4).
- `Problem`: an unlocated refusal, a closed set of records each holding the values its message needs: `Gated`, `NotGated`, `IdentityRefused`,
  `LabelInvalid`, `MeaningInvalid`, `RecordingRefused`, `KeptSetChanged`, `ConnectionFailed`, `RecordNotWritten`, `UnknownLabel`,
  `NotQualified`, `NotApprover`, `NotSeparate`, `AlreadyApproved`, `NotPromotable`. The words around the values, and any command or option
  name, are the client's: an API should not parse prose.
- `Outcome`: `Succeeded`, `InvalidInput` (`LabelInvalid`, `MeaningInvalid`) or `Failed`. The decision is the use case's, the number the
  client's: the CLI maps 0 / 2 / 1 (`ExitCodes`), an API would map 200 / 400 / 422.

**Progress** is what a client shows before the use case returns: `Action<Progress>?` with a closed set of records, `WorkloadStarted`,
`NodeFinished`, `Promoting`, `Ignored` (an approval file a reader skipped). The CLI writes each as a line; an API host would stream each.
Everything else is the result.

## 2. What the CLI keeps, and the rules

The CLI keeps argument parsing and usage; the usage errors (unknown option, missing file, existing `--out`); reading the files named on the
command line into inputs; writing `--out`; one use-case call per command; formatting progress and results; the exit code from `Outcome`; and
the composition root (§3). **Composition types:** `Composition`, `DataDirectory`, `LocalRecordStore`, `PulumiTemplates`, `HttpRevisionProbe`.
**Command code:** every other `jd.cli` type. **Inner assemblies:** `jd.core`, `jd.definitionvalidator`, `jd.resolver`, `jd.orchestrator`.

| # | Rule | Enforced by |
|---|---|---|
| R1 | Command code reaches the inner assemblies only through a use case's `RunAsync`, the constructors of input types, the getters of result, progress and problem types and of the data types they hold, and `GraphJson.Serialize`. It may hold a `Connections` and pass it on, never read a member of it or of a port. | `ThinCliTests`: decodes every method body of `jd.dll` with `System.Reflection.Metadata`, attributes each call, `newobj` and field access to its declaring type, and fails on a reference outside the allowed set |
| R2 | Ports are named, implemented and called only in the composition types, which reach the inner assemblies only through ports, the types in their signatures and `Connections`. Only their files name an adapter (`jd.azure`, `jd.bp.`) or the DI container. | the same test; `tools/verify.sh` greps `src/jd.cli` for the adapter and container names outside the composition files |
| R3 | The inner assemblies touch no file, directory, environment variable, current directory or console. | the same IL scan over the four inner assemblies |

R1–R3 hold with empty allowlists. R1 is a test, not a `grep`, because names a CLI may use (`NodeReport`) and may not (`Orchestrator`) share
namespaces. It cannot see a rule re-implemented in plain C# over result data; that stays the reviewer's call. The rules apply to every host
assembly: an API host is scanned as `jd.dll` is.

## 3. Composition root

`Connections` (in `jd.orchestrator/usecases`, ports only) is what a use case reaches outside jd through. `Composition.Connect` in the CLI
builds it from the environment variables and jd's data directory; a future API host builds the same record from its own configuration, and
tests pass fakes.

| Member | Port | The CLI's adapter |
|---|---|---|
| `Records` | `IRecordStore` | `LocalRecordStore` on `<data>/records` |
| `Identity` | `IDeployerIdentity` | `ArmDeployerIdentity` over `ArmCredentials` (`ARM_*`, else the Azure CLI sign-in) |
| `LiveRevisions` | `ILiveRevisions` | `ArmLiveRevisions`, same credential |
| `Backend` | `IBackEndProvider` | the Pulumi backend on `<data>/state` and its key, `PULUMI_HOME`, `JD_SCRATCH_DIR` |
| `Probe` | `IRevisionProbe` | `HttpRevisionProbe` |
| `Time` | `TimeProvider` | `TimeProvider.System` |
| `Templates` | `ITemplates` per catalog (a template store plus `Check(ResolvedGraph)`) | `PulumiTemplates`; a templates part with no documents (missing or empty `templates/`) is one error, `<catalog>/templates: : template directory not found.` |

Every member is a factory, so a run builds only what it reaches. A use case invokes each at the step that needs it: identity first, then
`set.yaml`; then backend, orchestrator and, for a recorded run, live revisions together, before the first backend call, so a reader that cannot
be built stops the run before anything is deployed (`ConnectionFailed`). A run that stops earlier, or a preview, builds none of them.

## 4. Errors

- `LoadError` (`file: location: message`) is produced by the loaders, the resolver and the template check, returned in `Errors`, and printed by
  the CLI one per line on stderr. The use case gives the catalog loader's errors, and the template misfits that name a catalog file, the full
  path under the catalog (`SourceCatalog.Located`); other errors already carry their document's name.
- Expected failures inside a run are `Problem`s at the step where they arise: the identity adapter's `InvalidOperationException`, a store's
  `ArgumentException`, a connection that cannot be built, a record that cannot be written.
- An `IOException` or `UnauthorizedAccessException` that escapes a use case (a data directory that cannot be written) is mapped once, by the
  CLI's dispatch, to exit 2 with its message.
