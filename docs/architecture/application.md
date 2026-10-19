# A thin CLI over an application layer

> Status: design note for S25, **phase 1.5**, next to S24 ([documents.md](documents.md)); pending the user's review. The user (2026-10-19):
> *"keep in mind that the CLI is just a starting app. The northstar for this is JD will be hosted as an API. There will be CLI, web app that
> will use the API for everything. So make sure don't leak any logic to the CLI and keep it thin"*. Serves [ADR 0001](../decisions/0001-custom-build-api-and-clients.md)
> ("each client is a thin layer over the API"), principles 6–14 ([context.md](../context.md#design-principles)) and the non-negotiable on clean
> architecture. Makes a hosted API possible and builds none of it: no HTTP, no hosting, no DTOs. Line numbers are `main` at `b9d5531`, after
> S24a1 (one document loader: `.Value`, `EnvironmentParser.LoadAsync`, `ReleaseHash.Of`, `DocumentLoader`), whose names this note uses.

## 1. Problem

`jd.cli` is the composition root (standards §3: "only wiring, argument parsing, output"), but about half of its ~1,500 production lines
are the platform's rules: the order of a recorded run, the gate refusals, the approval checks, record writing, the exit-code decisions. A web
client or an API host would have to copy them, and a copy of a security check is a second place for it to be wrong. The resolver also reads
files itself (`DocumentLoader.LoadFileAsync(path)`, `CatalogDirectory`, `ReleaseBuilder.BuildAsync(path)`), so its inputs are paths, which no
API request carries.

## 2. Inventory: logic in jd.cli that is not parsing or formatting

Paths under `src/jd.cli/`. **Goes to**: UC = a use case (section 3), Root = the composition root (section 5), CLI = stays (section 4).

| File:line | What it does | Kind | Goes to | Why |
|---|---|---|---|---|
| `Cli.cs:145-164`, `EnvCli.cs:53-60`, `ApproveCli.cs:48-53`, `PromoteCli.cs:42-47`, `ReleaseDeployCli.cs:90-99` | load + validate workload, catalog, descriptor, definition, base, set from **paths** | files | read: CLI; load: UC | the CLI does I/O; loading and validating is the platform's (section 6) |
| `Cli.cs:166`, `:210-220`; `EnvCli.cs:62-69`; `ReleaseDeployCli.cs:101-104` | environment type is the catalog's; a gated type carries its block (boundary 13) | check | UC | a security rule; every client must hit it |
| `Cli.cs:171`, `:197-208`; `ReleaseDeployCli.cs:107-110` | a gated environment refuses `deploy`, `release deploy`, `release qualify` (not `--preview`) | refusal | UC | approvals.md §2: deployed to only by promotion |
| `Cli.cs:176-189` | resolve; deploy, or preview (text or JSON) | orchestration | UC; JSON/text: CLI | |
| `Cli.cs:227-240`; `ReleaseDeployCli.cs:127-130` | templates loaded from `<catalog>/templates`, every graph checked against them before anything is created | files, check | read: CLI; check: UC through a port | the check is the backend's protocol, the order is the platform's |
| `Cli.cs:242-248`, `:273-319` | default probe, orchestrator, records, identity, live revisions, ARM credential, Pulumi backend + settings | composition, env vars, data dir | Root | built once, the same for every host |
| `Cli.cs:250-271` | provision: templates, orchestrator, preview vs deploy, progress lines, "stopped at" | orchestration, exit code | UC; lines: CLI from progress | |
| `Cli.cs:279-292` | identity: `InvalidOperationException` → refusal, before any record or backend | identity, order | UC (order), Root (building) | release-record.md §1 order |
| `Cli.cs:222-225` | catalog errors renamed to full paths when the file exists (`File.Exists`) | formatting | CLI, from the names it read | presentation; no file test needed |
| `Cli.cs:131-140`, `EnvCli.cs:39-47`, `ReleaseCli.cs:74-78`, `ReleaseDeployCli.cs:72-81` | `IOException`/`UnauthorizedAccessException` → exit 2 | exit code | CLI, once (dispatch) | the host maps what escapes; four copies today; none in approve, nor in promote before its walk (its walk has `ReleaseDeployCli.cs:77`) |
| `EnvCli.cs:62-93` | definition over base, substrate graph, descriptor composed dry first, provision, compose from outputs | orchestration, checks | UC | "everything knowable is checked before anything is created" |
| `EnvCli.cs:95-99`, `:123` | write `--out` (`CreateNew`, or `Create` with `--force`); refuse an existing `--out` up front | files | CLI | the output file is the CLI's; the descriptor bytes are the result |
| `ReleaseCli.cs:50-52`, `:70` | build the set from a manifest path (reads the definitions it names) at `DateTimeOffset.UtcNow`; load a set; write the set file | files, clock | UC (build/load, clock from `Time`); write: CLI | the real clock is debt (Appendix A) |
| `ReleaseDeployCli.cs:75`, `:88`, `:133-141`, `:227-252` | a preview records nothing; start time; identity, then `set.yaml` kept or compared, before the backend | orchestration, order | UC | release-record.md §1 |
| `ReleaseDeployCli.cs:113-117` | a promotion's kept bytes must hash to the judged set | check | UC | trust boundary (approvals.md §5) |
| `ReleaseDeployCli.cs:143-157` | backend, orchestrator, qualifier built together, before the first backend call; a failure stops the run | composition, order | Root builds, UC orders (section 5) | |
| `ReleaseDeployCli.cs:159-213` | walk; IQ only when every workload succeeded; record built and written; exit 1 on a failed workload, IQ or record | orchestration, exit code | UC | `Outcome` (section 3) |
| `ReleaseDeployCli.cs:255-269` | a record write failure after a deploy: "the release was deployed, but …", exit 1 | refusal | UC (`RecordNotWritten`) | |
| `ReleaseDeployCli.cs:25-32`, `:55-59` | `Promotion` smuggled into the argv options to reuse the walk; the kept set named by `KeptSetName` | orchestration | UC: `Promote` calls the release walk | |
| `Arguments.cs:88` | label must be a valid path segment (`ReleaseRecordFile.LabelProblem`) | check | UC (`InvalidInput`, still exit 2) | a domain rule on an input |
| `ApproveCli.cs:22-23`, `:145-147` | `--meaning` 1–500 characters, no control characters (the record schema's limit) | check | UC (`InvalidInput`, exit 2) | the API must apply it too |
| `ApproveCli.cs:55-128` | not gated; identity; qualification; approver listed; separate from deployer; ignored files; repeat; build, write, count | checks, order, files | UC | approvals.md §4 |
| `PromoteCli.cs:49-83` | not gated; identity; `PromotionCheck`; ignored files; then the walk on the kept set | checks, order | UC | approvals.md §5 |
| `DataDirectory.cs:13-65` | `JD_DATA_DIR` or the local application data folder; state folder; random key, mode 600 | data dir, env var | Root (stays) | connections.md |
| `HttpRevisionProbe.cs` | the HTTPS probe adapter | adapter | Root (stays, unedited) | direct-Azure code: untouched, so no gate |
| `ReleaseDeployCli.cs:272-283`, `:216-224`; `ReleaseCli.cs:122-143`; `DeployFormatter.cs`; `PreviewFormatter.cs` | summary, IQ verdict, set listing, node lines, graph listing | formatting | CLI | presentation of result data |

`jd.resolver/release/LocalRecordStore.cs` is a file adapter inside the resolver; it moves to the root beside `DataDirectory` (S25a), so the
inner layers do no I/O (rule R3, section 4).

## 3. The application layer

**Project: `jd.orchestrator`**, the layer standards §3 already names *Application* (`jd.core`, `jd.resolver`; no Pulumi, Azure, adapters,
CLI). The use cases go in `src/jd.orchestrator/usecases/` (namespace `jd.orchestrator.usecases`); the walk engine stays where it is. No new
project: a new one would have the same dependencies and mark no boundary (standards §2; section 9 compares).

**One shape for every use case:** `static Task<TResult> RunAsync(TInput input, Connections connections, Action<Progress>? progress, CancellationToken)`
(`Validate` and `Preview` take no connections). Inputs are plain values and documents, so an API request maps to one input one to one. A use
case checks its plain values first, before it loads any document: the label and the meaning are refused before the environment is parsed.

| Input piece | Type | Where |
|---|---|---|
| A document | `SourceDocument(string Name, ReadOnlyMemory<byte> Content)`: the name errors print (the CLI passes the argument as typed, so messages stay the same), the exact bytes (hashes and the strict UTF-8 check need bytes) | `jd.resolver`, beside `LoadError` |
| Decoding | `SourceDocument.Text()`: UTF-8 with the byte-order mark detected, the decoder `File.ReadAllText` (`DocumentLoader.cs:29`, `CatalogDirectory.cs:26`, `TemplateLibrary.cs:49`) and `ReleaseSetFile.cs:72` use today. Each kind's loader decodes; the CLI never does. Every kind keeps its decoding: the definitions a manifest names stay strict UTF-8 with the mark kept (`ReleaseBuilder.cs:31,60`), decoded by the builder from `Content` | `jd.resolver` |
| A catalog | `SourceCatalog(string Name, IReadOnlyList<SourceDocument> Documents)`: the catalog argument as typed, and every `*.yaml` under it, named relative to it (`templates/…` included) | the catalog loader splits off the templates by the one layout rule (`CatalogDirectory.cs:7`, moved to `CatalogParser`) into a `SourceCatalog` of their own, named `Path.Combine(<catalog>, "templates")` as `Cli.cs:231` names it, documents relative to it |
| Documents a manifest names | `DocumentReader`: `delegate Task<(string Name, ReadOnlyMemory<byte>? Content)> DocumentReader(string reference, CancellationToken)`, no content when nothing is there; the name is what errors print | `jd.resolver`; the CLI's resolves against the manifest's directory |

| Action | Use case | Input | Result (besides `Errors`, `Problem`, `Outcome`) |
|---|---|---|---|
| `validate` | `Validate` | `Workload` | — |
| `preview` | `Preview` | `Workload`, `Environment`, `Catalog` | `Graph: ResolvedGraph?` |
| `deploy` | `Deploy` | `Workload`, `Environment`, `Catalog`, `Preview: bool` | `Run: RunReport?` |
| `env up` | `EnvUp` | `Definition`, `Catalog`, `Base: SourceDocument?` | `Run`, `Environment`, `Descriptor: ReadOnlyMemory<byte>?` |
| `release create` | `CreateRelease` | `Manifest`, `Definitions: DocumentReader` | `Set: ReleaseSet?`, `Document: ReadOnlyMemory<byte>?` (the set file's bytes) |
| `release show` | `ShowRelease` | `Set` | `Set`, `CreatedAt: string` (the document's own value, not presentation; *not in the brief's list; it validates the set, so it is a use case too*) |
| `release deploy` | `DeployRelease` | `Set`, `Environment`, `Catalog`, `Preview` | `Environment`, `Set`, `Runs: IReadOnlyList<WorkloadRun>`, `NotStarted`, `Iq: IqResult?`, `Record: string?` |
| `release qualify` | `QualifyRelease` | `Set`, `Environment`, `Catalog` | `Environment`, `Set`, `Iq`, `Record` |
| `approve` | `Approve` | `Label`, `Environment`, `Meaning` | `Environment`, `Approver`, `Counted`, `Record` |
| `promote` | `Promote` | `Label`, `Target`, `Catalog` | `Environment`, `SetSha256`, `Release: DeployReleaseResult?` |

**Results are data.** Each holds what happened (the existing data types: `ResolvedGraph`, `RunReport`, `WorkloadRun`, `IqResult`,
`ReleaseSet`, `EnvironmentDescriptor`; no copies), the evidence (record name, hashes, principal), and the errors:

- `Environment: EnvironmentDescriptor?`: the loaded environment (the target, for approve and promote). Its name, region, type and policy
  (`QualifiedOn`, `Required`) are what the output prints (`ReleaseDeployCli.cs:219,281`, `ApproveCli.cs:81,127`, `PromoteCli.cs:81`, `EnvCli.cs:101`).
- `CreatedAt` of `release show` is the document's own value, not presentation: the set's time as the set document holds it. The use case applies `ReleaseRecordFile.DocumentTimeFormat`,
  as `ReleaseSetFile.cs:43` does on write, so jd.cli names no format (`ReleaseCli.cs:125` today).
- `Errors: IReadOnlyList<LoadError>`: located errors, unchanged (section 7).
- `Problem: Problem?`: an unlocated refusal. `Problem` is a closed set of records, one per refusal, each holding the values its message
  needs, as `Progress` does: `Gated(Environment)` (`Cli.cs:206`), `NotGated(Environment)` (`ApproveCli.cs:57`, `PromoteCli.cs:51`),
  `IdentityRefused(Reason)` (`Cli.cs:289`), `LabelInvalid(Reason)` (`Arguments.cs:88`), `MeaningInvalid(MaxLength)` (`ApproveCli.cs:146`),
  `RecordingRefused(Reason)` (`ReleaseDeployCli.cs:244,248`), `KeptSetChanged(Set, ExpectedSha256)` (`:115`), `ConnectionFailed(Reason)`
  (`:155`), `RecordNotWritten(Reason)` (`:266`, `ApproveCli.cs:122`), `UnknownLabel(Label, Reasons)` (`:75`), `NotQualified(Label,
  QualifiedOn, Reasons)` (`:81-85`), `NotApprover(Approver, Environment)` (`:91`), `NotSeparate(Approver, Label, QualifiedOn, Environment)`
  (`:92`), `AlreadyApproved(Approver, Label, Environment)` (`:109`), `NotPromotable(Label, Environment, Reasons)` (`PromoteCli.cs:72-76`).
  A `Reason` is a lower layer's text (the identity adapter's message, `KeepSetAsync`'s conflict). The words around the values, and any command
  or option name ("use `jd promote`", "run the command again"), are the client's. The kept set's name is the use case's: `Promote` names the
  set document it reads `ReleaseRecordFile.KeptSetName(qualifiedOn, label)` (`ReleaseDeployCli.cs:59` today), so its errors and
  `KeptSetChanged.Set` carry it.
- `Outcome`: `Succeeded`, `InvalidInput` (`LabelInvalid`, `MeaningInvalid`), `Failed` (errors, a problem, a failed workload or IQ). The CLI maps
  0 / 2 / 1 and prints Usage after an `InvalidInput`, as today (`ApproveCli.cs:38`); an API would map 200 / 400 / 422. The decision is the use
  case's (`ReleaseDeployCli.cs:200,212` today), the number the client's.

**Progress.** One generic sink, `Action<Progress>?`, where `Progress` is a closed set of records: `WorkloadStarted(Workload)`,
`NodeFinished(NodeReport)`, `Promoting(Label, From, SetSha256)`, `Ignored(What)` (an approval file a reader skipped). **Rule: what a client
shows before the use case returns is progress; everything else is the result.** The use case adapts the orchestrator's existing callbacks
(`Orchestrator.cs:28,44`), so the orchestrator does not change. The CLI writes each event as a line; an API host writes each to its stream
(server-sent events, JSON lines), which is why it is data, not text. Today's stdout and stderr orders are kept: the walk lines, `promoting …`
and `jd: … ignored …` are printed as they happen, the summary, IQ verdict, refusals and `record: …` from the result.

## 4. What the CLI keeps, and the rule

The CLI keeps: argument parsing and usage; the usage errors (exit 2: unknown option, missing file, existing `--out`); reading the files named
on the command line into inputs (a catalog directory: every `*.yaml` under it, recursively); writing `--out`; one use-case call per command;
formatting progress and result (`DeployFormatter`, `PreviewFormatter`, the summary and messages); the exit code from `Outcome`; the composition
root (section 5). Per command, about 40–80 lines.

**Composition types:** `Composition`, `DataDirectory`, `LocalRecordStore`, `PulumiTemplates`, `HttpRevisionProbe`. **Command code:** every
other jd.cli type. **Inner assemblies:** `jd.core`, `jd.definitionvalidator`, `jd.resolver`, `jd.orchestrator`.

| # | Rule the reviewer checks | Enforced by |
|---|---|---|
| R1 | Command code reaches the inner assemblies only through: a use case's `RunAsync`; the constructors of input types; the property getters of result, progress and problem types and of the data types they hold (all computed from the use cases' signatures, not listed); and `GraphJson.Serialize` (the JSON form of a result, documents.md choice 2 b). It reads no member of `Connections` or of a port: it may only hold a `Connections` and pass it on | `ThinCliTests` (jd.cli.tests, in `tools/verify.sh`'s test step): decodes every method body of `jd.dll` with `System.Reflection.Metadata` (BCL), attributes each call, `newobj` and field access to its declaring type, and fails on a reference outside the allowed set, naming type and member. A by-type allowlist of today's references shrinks each slice and is empty at S25f2, as S24's literal guard does |
| R2 | Ports (the interfaces `Connections` exposes) are named, implemented and called only in the composition types, which reach the inner assemblies only through ports, the types in the ports' signatures (`Probe`, `ProbeResult`, `ResolvedGraph`, `LoadError`; computed) and `Connections`. Only their files name an adapter (`jd.azure`, `jd.bp.`) or the DI container | the same test for the first sentence; `tools/verify.sh`, "Architecture boundaries": `grep -rl -E 'jd\.azure|jd\.bp\.|Microsoft\.Extensions\.DependencyInjection'` over `src/jd.cli` (`--include=*.cs`) must list only the composition files, in the style of the inner-layer `grep` |
| R3 | The inner assemblies touch no file, directory, environment variable, current directory or console | the same IL scan over the four inner assemblies: fails on any member of `File`, `Directory`, `FileInfo`, `DirectoryInfo`, `FileSystemInfo`, `FileStream`, a `StreamReader`/`StreamWriter` constructor taking a path, `Path.GetFullPath`, `Environment.GetEnvironmentVariable(s)`/`GetFolderPath`/`CurrentDirectory`, `Console`. A `using static` compiles to the same references, so it is seen. Today it finds the path loaders S25 replaces (`DocumentLoader.cs:24,29`, `CatalogDirectory.cs:12-26`, `ReleaseBuilder.cs:43-56`, `ReleaseSetFile.cs:52-66`), `LocalRecordStore` and `YamlSchemaValidator.cs:122-138`: its allowlist empties at S25g |

R1 is a test rather than a `grep` because the names a CLI may use (`NodeReport`, `ResolvedGraph`) and may not (`Orchestrator`, `Resolver`)
share namespaces; a list of forbidden names would drift. R1 cannot see a rule re-implemented in plain C# over result data (a loop that
re-checks approvers); that stays the reviewer's call. R1–R3 apply to every host assembly: an API host is scanned as `jd.dll` is.

## 5. Composition root

`Connections` (in `jd.orchestrator/usecases`, ports only) is what a use case reaches outside jd through. Every member is a factory, so a run
builds only what it reaches. **A use case builds every factory it needs at the step where today's code builds it**: identity first, then
`set.yaml`; then backend, orchestrator and, for a recorded run, live revisions together, before the first backend call
(`ReleaseDeployCli.cs:143-157`), so a reader that cannot be built stops the run before anything is deployed (`ConnectionFailed`). Lazy means
only that a run that stops earlier, or a preview, builds none of them.

| Member | Port | Built by the CLI's root (`Composition.Connect(environmentVariable)`) from |
|---|---|---|
| `Records` | `IRecordStore` | `LocalRecordStore` on `<data>/records` |
| `Identity` | `IDeployerIdentity` | `ArmDeployerIdentity` over `ARM_*` or the Azure CLI sign-in (`Cli.cs:277,297-298`) |
| `LiveRevisions` | `ILiveRevisions` | `ArmLiveRevisions`, same credential (`Cli.cs:295`) |
| `Backend` | `IBackEndProvider` | the Pulumi backend on `<data>/state` and its key, `PULUMI_HOME`, `JD_SCRATCH_DIR` (`Cli.cs:300-319`) |
| `Probe` | `IRevisionProbe` | `HttpRevisionProbe` (`Cli.cs:243`) |
| `Templates` | `Func<SourceCatalog, ITemplates>`, per request: `ITemplates` (new, `jd.orchestrator`) is `ITemplateStore` + `Check(ResolvedGraph)`, from the catalog's templates part (section 3) | `PulumiTemplates`, a ~20-line root adapter over `TemplateLibrary.Parse`: no documents gives today's one error, `<catalog>/templates: : template directory not found.` (`TemplateLibrary.cs:37-41`), from the part's name. The adapter project may not reference `jd.orchestrator` (standards §3; `HttpRevisionProbe` is the precedent) |
| `Time` | `TimeProvider` | `TimeProvider.System` |

`<data>` is resolved once, by `DataDirectory` (S23 unchanged: nothing set on the machine; `JD_DATA_DIR` the one optional override). The six
nullable seams of `Cli.RunAsync` (`Cli.cs:85-87`) become one `Connections?`; tests pass one with fakes (`with` on the built one). A future API
host builds the same `Connections` from its own configuration; whether `Composition` moves to a shared project now is choice 2.

## 6. Files

The CLI reads; the use cases take and return documents; nothing inside reads a path.

| File | Today | After S25 |
|---|---|---|
| workload, descriptor, definition, base, set (inputs) | `*.LoadAsync(path)` over `DocumentLoader.LoadFileAsync` (`DocumentLoader.cs:22-30`), `ReleaseSetFile.LoadAsync` | CLI reads bytes → `SourceDocument`; the loaders take documents (`DocumentLoader.LoadAsync(source, text)` over `Text()`); each path form is deleted in the slice that moves its last caller (section 10) |
| catalog directory | `CatalogDirectory.LoadAsync(root)`, `TemplateLibrary.LoadAsync(root/templates)` | CLI reads every `*.yaml` into a `SourceCatalog`; the catalog loader and `PulumiTemplates` each take their part (section 3); `CatalogDirectory` and `TemplateLibrary.LoadAsync` deleted in S25e1 |
| manifest + the definitions it names | `ReleaseBuilder.BuildAsync(path)` opens them | `CreateRelease(Manifest, DocumentReader)`: the builder awaits the reader for each `definition`; the CLI never reads the manifest's fields |
| release set written (`release create --out`) | `ReleaseSetFile.WriteAsync(set, path)` | result `Document` (bytes); CLI writes with `CreateNew` |
| descriptor written (`env up --out`) | composed text, written by `EnvCli` | result `Descriptor` (bytes); CLI writes, `--force` its choice |
| records, `set.yaml` | through `IRecordStore` | unchanged; the store is a `Connections` member |

**S24f fit.** After S25, `DescriptorComposer` and `ReleaseSetFile` are called only by use cases, and only their insides know the layout. S24f
replaces those insides with the binder and touches no CLI file. The CLI never needs a field of any document (R1 makes it structural), so
S24's literal guard finds nothing in `jd.cli` except its commands and options (documents.md §9).

## 7. Errors

- `LoadError` (`file: location: message`) is unchanged. It is produced where it is today (loaders, resolver, template check), returned in
  `Errors`, and printed by the CLI one per line on stderr, in the same order. Catalog error names are made full paths by the CLI from the
  names it read (`Cli.cs:222-225` without `File.Exists`); the template errors already carry their names (section 5), as today.
- Expected failures inside a run are `Problem`s at the step where they arise today: the identity adapter's `InvalidOperationException`
  (`Cli.cs:287`), a store's `ArgumentException` (`ReleaseDeployCli.cs:246`), a reader that cannot be built (`:153`), a record write
  (`:264`). The CLI prints `jd: <its words>: <values>` as today.
- An `IOException` or `UnauthorizedAccessException` that escapes (a data directory that cannot be written) is mapped by the CLI's dispatch to
  exit 2 once (S25f2), as the four copies do now.

## 8. Behaviour preservation

**Byte-identical**: stdout, stderr and exit codes of every CLI test; the deployed-config snapshot; the graph, release-set, record and
approval goldens. Three risks, each with its design line and proof:

| Risk | Design line | Proof (slice) |
|---|---|---|
| a. A missing `templates/` gives `<catalog>/templates: : template directory not found.`, once (`TemplateLibrary.cs:37-41`, `Cli.cs:232`); documents have no path | the catalog loader names the templates part `Path.Combine(<catalog>, "templates")` (section 3); `PulumiTemplates` gives the one error for a part with no documents; the use case keeps `Distinct` (`Cli.cs:232`) | `ReleaseDeployCliTests.cs:157-171` tightened to the whole line, before the move, and a `deploy` twin (S25b2) |
| b. Decoding differs by kind: `File.ReadAllText` (workload, catalog, templates, descriptor, definition, manifest), `StreamReader` with mark detection (sets, `ReleaseSetFile.cs:72`), strict UTF-8 (definitions a manifest names, `ReleaseBuilder.cs:31,60`) | the first two are one decoder, `SourceDocument.Text()`; the builder keeps its strict decoder over `Content`; each loader decodes its kind (section 3) | `ReleaseTests.cs:283,290` kept; new CLI tests, green before and after: a workload with a mark and one with an invalid byte (S25b1), a set with a mark (S25d) |
| c. Every reader is built before the first backend call (`ReleaseDeployCli.cs:143-157`); lazy factories could build one later | the use case builds them together at that step (section 5) | a CLI test today (S25a): a `LiveRevisions` factory that throws `InvalidOperationException` gives exit 1, `jd: <message>`, and no backend call; its use-case twin (S25e1) |

Three known edges, each listed by its slice: (1) a usage error met together with another may report in another order: the label and the
meaning are checked in the use case, after the CLI checks that `--env` exists (S25f1, S25f2); (2) `approve`, and `promote` before its walk,
have no I/O catch today: if an `IOException` can reach them (S25f2 shows it with a test), it becomes exit 2 with its message instead of an
unhandled exception; (3) an empty `templates/` directory reads as no template documents, so it is reported as not found, as a missing one is;
today it gives only each node's "not in the template library" (S25b2 pins it with a test).

**Tests**: the CLI tests (`Cli.RunAsync`, ~3,000 lines) stay: they are the byte-identity proof; their seam arguments become a `Connections`
(mechanical). Tests that load seeds through a deleted path form read the file into a `SourceDocument` with a test helper. `LocalRecordStore`
leaves the resolver, so the five test files that build one (`ApprovalRecordTests`, `ReleaseRecordFileTests`, `OrchestratorTests.Promotion`,
`OrchestratorTests.Qualification`, and `LocalRecordStoreTests`, which moves to jd.cli.tests) change: the first four use `MemoryRecordStore`,
a test fake of `IRecordStore` over a dictionary (refuses an existing name as `CreateNew` does), one file in jd.resolver.tests linked into
jd.orchestrator.tests as the schemas are (S25a). Each rule that moves gets a use-case test in `jd.orchestrator.tests` at its boundary: gate
refusal before identity; identity before `set.yaml` before the backend factory is called; the kept set re-hash; nothing written on any
refusal; `MeaningInvalid` and `LabelInvalid` are `InvalidInput`, before any document is parsed; progress order (`Promoting` before the first
`WorkloadStarted`).

**Azure gate** (lead ruling, 2026-10-19): the snapshot does not change in any slice. Editing an offline test under `src/backend-providers`
does not change what reaches Azure, so it triggers no gate; deleting production code there does. S25e1 and S25f2 each edit
`jd.bp.pulumi.tests/TemplateLibraryTests.cs` (`:40` loses `CatalogDirectory.LoadAsync` in S25e1, `:41` loses `EnvironmentParser.LoadAsync(path)` in
S25f2): offline, no gate for the edit. `TemplateLibrary.LoadAsync` loses its last caller in S25e1, which deletes it with its path-form tests
(`TemplateLibraryTests.cs:44,53,67,83,215`) and fixes that file's broad `catch (Exception)`; this is production code in
`src/backend-providers`, so **S25e1 runs the gate**, once. No other S25 slice edits production code in `src/backend-providers` or
`src/jd.azure` (`HttpRevisionProbe.cs`, `ReleaseFlow.cs` and `Qualifier.cs` are not edited). S24e finds the method gone; documents.md §9 is unchanged.

## 9. Simplicity guard

| Question | Design | For | Against | Verdict |
|---|---|---|---|---|
| Where the use cases live | **A. `jd.orchestrator/usecases`** | the layer is already *Application* (standards §3); no project | the walk engine and the use cases share a project | **recommended** |
| | B. new `jd.application` above `jd.orchestrator` | a project per concept | same dependencies, so no boundary (standards §2); CLI still sees resolver types through results | rejected |
| Where the seams are built | **A. `Composition` in jd.cli; `Connections` type in the application layer** | no speculative project; the type is the shared contract | an API host writes its own ~40-line builder, or moves it | **recommended** (choice 2) |
| | B. `jd.host` project now | one builder for two hosts | one host exists; a project for a second that is not planned (standards §2) | rejected for now |
| Progress | **A. `Action<Progress>`, closed records** | the orchestrator's existing callback style; ordered; serialisable | a switch in each client | **recommended** |
| | B. `IProgress<T>` | BCL | its `Progress<T>` posts asynchronously and can reorder lines; the CLI needs its own implementation anyway | rejected |
| | C. events / observers | familiar | subscriptions and lifetimes for one listener | rejected |
| | D. `IAsyncEnumerable<Progress>` plus a result | natural for HTTP streaming | two return channels in every use case and client | rejected |
| | E. no progress, result only | fewest types | a minutes-long deploy prints nothing until the end; output order changes | rejected |
| Refusals | **A. one record per refusal, with its values** | data; an API maps each to a problem type; command names stay the client's; the shape `Progress` has | 15 one-line records | **recommended** |
| | B. an enum plus one `Detail` string | fewer types | `NotSeparate` needs four values; the client would parse them back out | rejected |
| | C. message strings from the use case | no types | "use `jd promote`" is CLI text inside the platform; an API would parse prose | rejected |

Left out on purpose: HTTP, hosting, request/response DTOs (inputs and results are the API's types when it comes), a mediator or command bus, a
DI container in the use cases, per-use-case interfaces.

## 10. Slices and exit criteria

Each slice: `tools/verify.sh` green; CLI tests byte-identical (section 8); snapshot unchanged; **every touched file at the full quality bar**
(conventions, Appendix A's row for the slice fixed in it). Field-structure knowledge in touched resolver files is S24's scope by its slices,
not S25 debt. Each path form is deleted in the slice that moves its last caller. A step that a later slice's command still uses moves once,
into the use-case layer, and that command calls it through R1's allowlist until its own slice. Lines are production lines (C#, `verify.sh`),
estimated, added / removed; cap ~250 changed. Only S25e1 runs the Azure gate (section 8).

| Slice | Content | Lines | Acceptance |
|---|---|---|---|
| **S25a** Connections | `Connections` (factories), `Composition.Connect` replacing `Cli.cs:242-248,273-319`; `RunAsync`'s six seams → one; `LocalRecordStore` and its tests to jd.cli, its segment guard to `IRecordStore`; `MemoryRecordStore`; `ThinCliTests` (R1, R2's first sentence, R3) with today's allowlists; R2's `grep` in `verify.sh` | ~+100 / −80 | a test per lazy seam (not built when not reached); risk c's CLI test; R1–R3 fail on a planted reference |
| **S25b1** Frame, validate | `SourceDocument` (`Text()`), `SourceCatalog`, `Problem`, `Outcome`, `Progress`; `ExitCodes` from `Outcome`; `Validate`, `jd validate` reads its file; `Arguments.cs`'s two scan loops as one | ~+110 / −45 | risk b's workload tests green before and after; allowlist shrinks by `validate` |
| **S25b2** Workload commands | `Preview`, `Deploy`; `ITemplates`, `PulumiTemplates`; CLI reads a catalog into a `SourceCatalog`, `CatalogParser` takes it (`CatalogSource` removed); `WorkloadFile.LoadAsync` deleted; the type and gate checks as use-case steps | ~+135 / −95 | risk a's tests; edge 3 pinned; gate and type refusals as use-case tests |
| **S25c** Env up | `EnvUp`; `EnvCli` parse/read/write/format only, on the one scanner; `EnvironmentDefinitionFile.LoadAsync` and `Cli.ProvisionAsync` deleted | ~+90 / −95 | descriptor golden identical; `--force` and existing `--out` still CLI |
| **S25d** Release create and show | `CreateRelease` (`DocumentReader`), `ShowRelease`; `ReleaseBuilder.BuildAsync` and `ReleaseSetFile.WriteAsync` take and return documents; `Time` replaces `DateTimeOffset.UtcNow`; `ReleaseCli` on the one scanner | ~+95 / −85 | release-set golden identical; a missing definition names the resolved path as today; risk b's set test |
| **S25e1** Release run start | the shared start of `release deploy` and `qualify` (load, type and gate checks, resolve, templates, recording, connections built together) as one use-case step that `ReleaseDeployCli` calls; `ReleaseSetFile.LoadAsync` and `CatalogDirectory` deleted (`TemplatesDirectory` to `CatalogParser`); `TemplateLibrary.LoadAsync` deleted with its path-form tests (`TemplateLibraryTests.cs:44,53,67,83` read the `Pulumi.yaml` files themselves and call `Parse`; `:215`, the missing-directory message, goes: risk a's CLI test covers it), `:40` off `CatalogDirectory.LoadAsync`; the `catch (Exception)` narrowed to what `ParseYaml` throws | ~+112 / −132 (244, under the cap; if it measures over 250, the `TemplateLibrary` deletion splits off as its own slice) | order tests (section 8); kept-set re-hash; risk c's use-case test; **Azure gate** (production code deleted in `src/backend-providers`) |
| **S25e2** Release deploy and qualify | `DeployRelease`, `QualifyRelease` on S25e1's step (walk, IQ, record); `ReleaseDeployCli` parse/read/format only; its `PromoteAsync` hands the kept set to `DeployRelease` | ~+95 / −115 | `RecordNotWritten`; qualify never calls `DeployAsync` (test) |
| **S25f1** Approve | `Approve` (label and meaning as `InvalidInput`, before the environment is parsed); `ApproveCli` parse/read/format only | ~+75 / −70 | every approvals.md §7 approve test kept; nothing written on refusal; edge 1 listed |
| **S25f2** Promote | `Promote` (the kept set named `KeptSetName`, then `DeployRelease`'s walk; `Promotion` leaves the argv options); `EnvironmentParser.LoadAsync` and `DocumentLoader.LoadFileAsync` deleted (`TemplateLibraryTests.cs:41` reads the file itself: an offline test edit, no gate); the I/O catch once, at the dispatch | ~+80 / −100 | every §7 promote test kept; `Promoting` before the walk; edge 2's test; R1 allowlist empty |
| **S25g** Close | R3 allowlist empty: `YamlSchemaValidator.ValidateAsync(paths)` deleted (tests only); standards §3 (jd.orchestrator: "use cases and the walk"; jd.cli: "parsing, file I/O, formatting, composition"), conventions "wiring only in jd.cli", components/provisioning pointers | ~+5 / −20, docs | R1, R2, R3 pass with no exception |

**Order:** after S24a1 (merged) and **before S24a2** (recommended, choice 1): S24c1, S24c2, S24e and S24f each edit lines S25 moves out of the
CLI, so S25 first means they edit use cases once instead of CLI code twice. At the latest before S24f, as agreed. Total about **+897 / −837
over 10 slices**, one Azure gate (S25e1, which deletes `TemplateLibrary.LoadAsync`) plus phase 1.5's exit gate.
S24e finds that method gone; documents.md §9 stays as approved.

**Exit criteria (added to phase 1.5's):** (1) R1, R2 and R3 pass with empty allowlists; (2) every user action is one use case whose input
holds no path or argv and whose result holds no formatted text; (3) CLI output, exit codes, the snapshot and all goldens byte-identical
except the edges of section 8, each listed; (4) every moved rule has a use-case test at its boundary.

**Roadmap (proposed, the user's decision):** phase 1.5's goal gains "and jd's rules live behind use cases a CLI or an API calls the same
way" (serves ADR 0001); its exit criteria gain the four above. The hosted API stays off the track (roadmap "Not on this track"); its first
slice would be a host project calling these use cases, plus choice 2's move.

**Later phases stay open:** phase 2's `jd catalog new|check|show` are three more use cases; N6's registry and the Azure-backed stores are
new `Connections` members; a per-request identity in an API is a different `Identity` factory, not a code change in any use case.

## 11. Open choices for the user

| # | Choice | Options | Recommendation |
|---|---|---|---|
| 1 | When S25 runs | (a) now, after S24a1, before S24a2; (b) after S24e, before S24f | **(a)**: four S24 slices then edit use cases, not CLI code that S25 moves again; and S25e1 takes the one gate (it deletes `TemplateLibrary.LoadAsync`; S24e finds it gone) |
| 2 | Where the composition builder lives | (a) jd.cli now, moved to a host project when the API arrives; (b) a `jd.host` project now | **(a)**: one host today (standards §2); the move is one file |
| 3 | Refusals as data | (a) one record per refusal, the client words it; (b) an enum plus a detail string; (c) message strings from the use case | **(a)**: each refusal carries its own values; an API should not parse prose, and CLI command names stay in the CLI |
| 4 | Use-case project | (a) `jd.orchestrator/usecases`; (b) new `jd.application` | **(a)**: standards §3 already calls `jd.orchestrator` the application layer |

## Appendix A. Quality debt in jd.cli and the files S25 touches

| Slice | File | Debt (rule) |
|---|---|---|
| S25a | `Cli.cs` and the commands | six nullable seam parameters threaded through every command (`Cli.cs:85-87`, `ReleaseCli.cs:20`, `ReleaseDeployCli.cs:39,56,62,85`, `PromoteCli.cs:24`, `ApproveCli.cs:32`) (long parameter list); `Cli` as a static helper bag every command calls (`Print`, `RefuseType`, `ProvisionAsync`, `Records`, …) (one responsibility: its composition leaves now, each step as its last caller moves, the bag is gone in S25e1) |
| S25a | `LocalRecordStore.cs` | its guard calls the resolver's internal `ReleaseRecordFile.SegmentProblem` (`:38`), which jd.cli cannot reach: the rule is the port's contract (`IRecordStore.cs:5-6`), so it becomes a static member of `IRecordStore`, message unchanged, and `LabelProblem` calls it; otherwise none |
| S25a | `DataDirectory.cs` | none |
| S25b1 | `Cli.cs`, `Arguments.cs` | the exit codes declared in six files (`Cli.cs:24-26`, `EnvCli.cs:14-16`, `ReleaseCli.cs:14-16`, `ReleaseDeployCli.cs:21-23`, `ApproveCli.cs:15-17`, `PromoteCli.cs:17-18`) (literals: named once in `ExitCodes`, each file's copy leaving with its slice); four option-scanning loops (`Arguments.cs:12-52,58-89`, `EnvCli.cs:133-158`, `ReleaseCli.cs:89-110`) and three file-existence checks (`Arguments.cs:92-105`, `EnvCli.cs:106-124`, `ReleaseCli.cs:36-46`) (duplication; S25c/d switch to the one scanner and check); `Cli`, `DeployFormatter`, `PreviewFormatter` public where internal suffices (`InternalsVisibleTo` exists) (API and contracts) |
| S25b2 | `WorkloadFile.cs` | rule errors parsed back out of `"<location>: <message>"` strings (`:30-35`) (stringly typed: `WorkloadRules.Check` returns location and message, two callers); `ParseAsync`'s `path` names a source, not a path (`:20`) (naming) |
| S25b2 | `CatalogSource.cs`, `CatalogParser.cs` | `CatalogSource` duplicates `SourceDocument` (removed); `CatalogParser`: only its entry point changes, field structure is S24's |
| S25c | `EnvironmentDefinition.cs` | none beyond the path form (`:21-23`) |
| S25d | `ReleaseCli.cs` | `DateTimeOffset.UtcNow` where the injected clock belongs (`:51`) (resources: a real clock); `RunAsync` builds, loads, writes and formats in one method (`:19-79`) (structure) |
| S25d | `ReleaseBuilder.cs` | file reads in the resolver (`:36`, `:43-56`): the slice's subject; field reads are S24f's; none else |
| S25d | `ReleaseSetFile.cs` | `ParseAsync`'s `path` names a source, not a path (`:70`) (naming: it takes a `SourceDocument`); the write's file I/O (`:52-53`) is the slice's subject; field structure S24f's |
| S25e1 | `ReleaseDeployCli.cs` | `DeployAsync` 117 lines doing load, checks, recording, walk, IQ and record (`:84-201`) (one thing per method: start in S25e1, walk in S25e2) |
| S25e1 | `TemplateLibrary.cs` | `LoadAsync` (`:34-53`) loses its last caller: deleted, with its path-form tests; the broad `catch (Exception)` (`:76`) (catch what you handle) |
| S25f1 | `ApproveCli.cs` | `MeaningMaxLength` copying the schema's limit (`:22-23`) (named once, in the use case beside a pointer to the schema); `ApproveAsync` 63 lines with a nested-ternary refusal chain (`:67-130`, `:91-93`) (structure) |
| S25f2 | `ReleaseDeployCli.cs`, `PromoteCli.cs` | `Promotion` set by `init` on an argv options record to reuse the walk (`ReleaseDeployCli.cs:25-29,55-59`) (structure); the I/O catch copied four times (`Cli.cs:135`, `EnvCli.cs:43`, `ReleaseCli.cs:74`, `ReleaseDeployCli.cs:77`) (duplication: three leave with their files in S25c, S25d, S25e2; the last becomes the dispatch's) |
| S25f2 | `EnvironmentParser.cs`, `DocumentLoader.cs` | none beyond the path forms (`EnvironmentParser.cs:14-15`, `DocumentLoader.cs:21-30`) |
| S25g | `YamlSchemaValidator.cs` | `ValidateAsync(schemaFilePath, yamlFilePath)` used only by tests (`:122-138`) (unneeded code: deleted, its tests read the files) |
| S24e | `HttpRevisionProbe.cs` | public where internal suffices: not edited by S25, so no gate (S24e edits it for `probe.scheme`) |
