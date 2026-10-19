# A thin CLI over an application layer

> Status: design note for S25, **phase 1.5**, next to S24 ([documents.md](documents.md)); pending the user's review. The user (2026-10-19):
> *"keep in mind that the CLI is just a starting app. The northstar for this is JD will be hosted as an API. There will be CLI, web app that
> will use the API for everything. So make sure don't leak any logic to the CLI and keep it thin"*. Serves [ADR 0001](../decisions/0001-custom-build-api-and-clients.md)
> ("each client is a thin layer over the API"), principles 6–14 ([context.md](../context.md#design-principles)) and the non-negotiable on clean
> architecture. Makes a hosted API possible and builds none of it: no HTTP, no hosting, no DTOs. Line numbers are `main` at `87e2b2e`;
> S24a1 (in review) renames `.Catalog`/`.Descriptor`/`.Workload` to `.Value`, `EnvironmentFile` to `EnvironmentParser.LoadAsync` and
> `ReleaseRecordFile.Sha256` to `ReleaseHash.Of` on the load lines of `Cli.cs`, `EnvCli.cs`, `ApproveCli.cs`, `PromoteCli.cs` and
> `ReleaseDeployCli.cs`: every S25 slice moves those lines, so S25 starts after S24a1 merges and uses its names.

## 1. Problem

`jd.cli` is the composition root (standards §3: "only wiring, argument parsing, output"), but about half of its ~1,400 production lines
are the platform's rules: the order of a recorded run, the gate refusals, the approval checks, record writing, the exit-code decisions. A web
client or an API host would have to copy them, and a copy of a security check is a second place for it to be wrong. The resolver also reads
files itself (`WorkloadFile.LoadAsync(path)`, `CatalogDirectory`, `ReleaseBuilder.BuildAsync(path)`), so its inputs are paths, which no API
request carries.

## 2. Inventory: logic in jd.cli that is not parsing or formatting

Paths under `src/jd.cli/`. **Goes to**: UC = a use case (section 3), Root = the composition root (section 5), CLI = stays (section 4).

| File:line | What it does | Kind | Goes to | Why |
|---|---|---|---|---|
| `Cli.cs:137-156`, `EnvCli.cs:53-60`, `ApproveCli.cs:48-53`, `PromoteCli.cs:42-47`, `ReleaseDeployCli.cs:90-99` | load + validate workload, catalog, descriptor, definition, base, set from **paths** | files | read: CLI; load: UC | the CLI does I/O; loading and validating is the platform's (section 6) |
| `Cli.cs:158`, `:203-212`; `EnvCli.cs:62-69`; `ReleaseDeployCli.cs:101-104` | environment type is the catalog's; a gated type carries its block (boundary 13) | check | UC | a security rule; every client must hit it |
| `Cli.cs:163`, `:191-200`; `ReleaseDeployCli.cs:107-110` | a gated environment refuses `deploy`, `release deploy`, `release qualify` (not `--preview`) | refusal | UC | approvals.md §2: deployed to only by promotion |
| `Cli.cs:168-181` | resolve; deploy, or preview (text or JSON) | orchestration | UC; JSON/text: CLI | |
| `Cli.cs:221-232`; `ReleaseDeployCli.cs:127-130` | templates loaded from `<catalog>/templates`, every graph checked against them before anything is created | files, check | read: CLI; check: UC through a port | the check is the backend's protocol, the order is the platform's |
| `Cli.cs:235-240`, `:266-311` | default probe, orchestrator, records, identity, live revisions, ARM credential, Pulumi backend + settings | composition, env vars, data dir | Root | built once, the same for every host |
| `Cli.cs:242-263` | provision: templates, orchestrator, preview vs deploy, progress lines, "stopped at" | orchestration, exit code | UC; lines: CLI from progress | |
| `Cli.cs:272-284` | identity: `InvalidOperationException` → refusal, before any record or backend | identity, order | UC (order), Root (building) | release-record.md §1 order |
| `Cli.cs:216-217` | catalog errors renamed to full paths when the file exists (`File.Exists`) | formatting | CLI, from the names it read | presentation; no file test needed |
| `Cli.cs:127-132`, `EnvCli.cs:43-47`, `ReleaseCli.cs:74-78`, `ReleaseDeployCli.cs:77-81` | `IOException`/`UnauthorizedAccessException` → exit 2 | exit code | CLI, once (dispatch) | the host maps what escapes; four copies today, none in approve/promote |
| `EnvCli.cs:63-93` | definition over base, substrate graph, descriptor composed dry first, provision, compose from outputs | orchestration, checks | UC | "everything knowable is checked before anything is created" |
| `EnvCli.cs:95-99`, `:123` | write `--out` (`CreateNew`, or `Create` with `--force`); refuse an existing `--out` up front | files | CLI | the output file is the CLI's; the descriptor bytes are the result |
| `ReleaseCli.cs:50-52`, `:70` | build the set from a manifest path (reads the definitions it names) at `DateTimeOffset.UtcNow`; load a set; write the set file | files, clock | UC (build/load, clock from `Time`); write: CLI | the real clock is debt (Appendix A) |
| `ReleaseDeployCli.cs:75`, `:88`, `:133-141`, `:227-252` | a preview records nothing; start time; identity, then `set.yaml` kept or compared, before the backend | orchestration, order | UC | release-record.md §1 |
| `ReleaseDeployCli.cs:113-117` | a promotion's kept bytes must hash to the judged set | check | UC | trust boundary (approvals.md §5) |
| `ReleaseDeployCli.cs:145-157` | backend, orchestrator, qualifier built before the first backend call; a failure stops the run | composition, order | Root builds, UC orders | |
| `ReleaseDeployCli.cs:159-213` | walk; IQ only when every workload succeeded; record built and written; exit 1 on a failed workload, IQ or record | orchestration, exit code | UC | `Outcome` (section 3) |
| `ReleaseDeployCli.cs:255-269` | a record write failure after a deploy: "the release was deployed, but …", exit 1 | refusal | UC (`RecordNotWritten`) | |
| `ReleaseDeployCli.cs:25-32`, `:55-59` | `Promotion` smuggled into the argv options to reuse the walk | orchestration | UC: `Promote` calls the release walk | |
| `Arguments.cs:88` | label must be a valid path segment (`ReleaseRecordFile.LabelProblem`) | check | UC (`InvalidInput`, still exit 2) | a domain rule on an input |
| `ApproveCli.cs:22-23`, `:145-147` | `--meaning` 1–500 characters, no control characters (the record schema's limit) | check | UC (`InvalidInput`, exit 2); missing option: CLI | the API must apply it too |
| `ApproveCli.cs:55-128` | not gated; identity; qualification; approver listed; separate from deployer; ignored files; repeat; build, write, count | checks, order, files | UC | approvals.md §4 |
| `PromoteCli.cs:49-83` | not gated; identity; `PromotionCheck`; ignored files; then the walk on the kept set | checks, order | UC | approvals.md §5 |
| `DataDirectory.cs:15-65` | `JD_DATA_DIR` or the local application data folder; state folder; random key, mode 600 | data dir, env var | Root (stays) | connections.md |
| `HttpRevisionProbe.cs` | the HTTPS probe adapter | adapter | Root (stays, unedited) | direct-Azure code: untouched, so no gate |
| `ReleaseDeployCli.cs:272-283`, `:216-224`; `ReleaseCli.cs:122-143`; `DeployFormatter.cs`; `PreviewFormatter.cs` | summary, IQ verdict, set listing, node lines, graph listing | formatting | CLI | presentation of result data |

`jd.resolver/release/LocalRecordStore.cs` is a file adapter inside the resolver; it moves to the root beside `DataDirectory` (S25a), so the
inner layers do no I/O (rule R3, section 4).

## 3. The application layer

**Project: `jd.orchestrator`**, the layer standards §3 already names *Application* (`jd.core`, `jd.resolver`; no Pulumi, Azure, adapters,
CLI). The use cases go in `src/jd.orchestrator/usecases/` (namespace `jd.orchestrator.usecases`); the walk engine stays where it is. No new
project: a new one would have the same dependencies and mark no boundary (standards §2; section 9 compares).

**One shape for every use case:** `static Task<TResult> RunAsync(TInput input, Connections connections, Action<Progress>? progress, CancellationToken)`
(`Validate` and `Preview` take no connections). Inputs are plain values and documents, so an API request maps to one input one to one.

| Input piece | Type | Where |
|---|---|---|
| A document | `SourceDocument(string Name, ReadOnlyMemory<byte> Content)`: the name errors print (the CLI passes the argument as typed, so messages stay the same), the exact bytes (hashes and the strict UTF-8 check need bytes) | `jd.resolver`, beside `LoadError` |
| A catalog | `IReadOnlyList<SourceDocument>`, names relative to the catalog root, templates included (`templates/…`) | the loader splits them, by the one layout rule `CatalogDirectory.TemplatesDirectory` names today |
| Documents a manifest names | `DocumentReader`: a delegate, reference → `(string Name, ReadOnlyMemory<byte>? Content)`, no content when nothing is there | `jd.resolver`; the CLI's resolves against the manifest's directory |

| Action | Use case | Input | Result (besides `Errors`, `Problem`, `Outcome`) |
|---|---|---|---|
| `validate` | `Validate` | `Workload` | — |
| `preview` | `Preview` | `Workload`, `Environment`, `Catalog` | `Graph: ResolvedGraph?` |
| `deploy` | `Deploy` | `Workload`, `Environment`, `Catalog`, `Preview: bool` | `Run: RunReport?` |
| `env up` | `EnvUp` | `Definition`, `Catalog`, `Base: SourceDocument?` | `Run`, `Environment` (name, region, type), `Descriptor: ReadOnlyMemory<byte>?` |
| `release create` | `CreateRelease` | `Manifest`, `Definitions: DocumentReader` | `Set: ReleaseSet?`, `Document: ReadOnlyMemory<byte>?` (the set file's bytes) |
| `release show` | `ShowRelease` | `Set` | `Set` (*not in the brief's list; it validates the set, so it is a use case too*) |
| `release deploy` | `DeployRelease` | `Set`, `Environment`, `Catalog`, `Preview` | `Set`, `Runs: IReadOnlyList<WorkloadRun>`, `NotStarted`, `Iq: IqResult?`, `Record: string?` |
| `release qualify` | `QualifyRelease` | `Set`, `Environment`, `Catalog` | `Set`, `Iq`, `Record` |
| `approve` | `Approve` | `Label`, `Environment`, `Meaning` | `Approver`, `Counted`, `Required`, `Record` |
| `promote` | `Promote` | `Label`, `Target`, `Catalog` | `From`, `SetSha256`, `Release: DeployReleaseResult?` |

**Results are data.** Each holds what happened (the existing data types: `ResolvedGraph`, `RunReport`, `WorkloadRun`, `IqResult`,
`ReleaseSet`; no copies), the evidence (record name, hashes, principal), and the errors:

- `Errors: IReadOnlyList<LoadError>`: located errors, unchanged (section 7).
- `Problem: Problem?`, `Problem(ProblemKind Kind, string Detail, IReadOnlyList<string> Reasons)`: an unlocated refusal. `ProblemKind` is closed,
  one value per message today: `Gated`, `NotGated`, `IdentityRefused`, `LabelInvalid`, `MeaningInvalid`, `SetConflict`, `KeptSetChanged`,
  `ConnectionFailed`, `RecordNotWritten`, `UnknownLabel`, `NotQualified`, `NotApprover`, `NotSeparate`, `AlreadyApproved`, `NotPromotable`.
  `Detail` carries a lower layer's text (the identity adapter's message, `KeepSetAsync`'s conflict), `Reasons` the listed reasons. The words
  around them, and any command or option name ("use `jd promote`"), are the client's.
- `Outcome`: `Succeeded`, `InvalidInput` (`LabelInvalid`, `MeaningInvalid`), `Failed` (errors, a problem, a failed workload or IQ). The CLI maps
  0 / 2 / 1; an API would map 200 / 400 / 422. The decision is the use case's (`ReleaseDeployCli.cs:200,212` today), the number the client's.

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

| # | Rule the reviewer checks | Enforced by |
|---|---|---|
| R1 | jd.cli calls into `jd.resolver` and `jd.orchestrator` only through a use case's `RunAsync`. Any other type of theirs that it names is reachable from a use case's input, result or progress (to build an input or read a result), or is `GraphJson` (the JSON form of a result, documents.md choice 2 b) | `ThinCliTests` (jd.cli.tests, in the gate's test step): reads `jd.dll`'s type and member references with `System.Reflection.Metadata` (BCL) and fails on any into those two assemblies outside the reachable set, naming the member. An allowlist of today's references, by file, shrinks each slice and is empty at S25g, as S24's literal guard does |
| R2 | Only the composition files (`Composition.cs`, `DataDirectory.cs`, `LocalRecordStore.cs`, `PulumiTemplates.cs`, `HttpRevisionProbe.cs`) name an adapter (`jd.azure`, `jd.bp.pulumi`) or the DI container | `tools/verify.sh`, "Architecture boundaries": `grep -l -E 'jd\.azure|jd\.bp\.|Microsoft\.Extensions\.DependencyInjection'` over `src/jd.cli/*.cs` must list only those files, in the style of the inner-layer `grep` |
| R3 | The inner layers (`jd.core`, `jd.resolver`, `jd.orchestrator`) read no file, environment variable or console | `tools/verify.sh`: `grep -E '\bFile\.|\bDirectory\.|FileStream|GetEnvironmentVariable|Console\.'` over their `*.cs` finds nothing (today it finds exactly the path loaders S25 deletes and `LocalRecordStore`) |

R1 is a test rather than a `grep` because the names a CLI may use (`NodeReport`, `ResolvedGraph`) and may not (`Orchestrator`, `Resolver`)
share namespaces; a list of forbidden names would drift.

## 5. Composition root

`Connections` (in `jd.orchestrator/usecases`, ports only) is what a use case reaches outside jd through. Every adapter is a factory, built on
first use, so a run builds only what it reaches and fails at the step that needs it (the order of release-record.md §1: identity, then
`set.yaml`, then the backend).

| Member | Port | Built by the CLI's root (`Composition.Connect(environmentVariable)`) from |
|---|---|---|
| `Records` | `IRecordStore` | `LocalRecordStore` on `<data>/records` |
| `Identity` | `IDeployerIdentity` | `ArmDeployerIdentity` over `ARM_*` or the Azure CLI sign-in (`Cli.cs:269,289-290`) |
| `LiveRevisions` | `ILiveRevisions` | `ArmLiveRevisions`, same credential (`Cli.cs:287`) |
| `Backend` | `IBackEndProvider` | the Pulumi backend on `<data>/state` and its key, `PULUMI_HOME`, `JD_SCRATCH_DIR` (`Cli.cs:292-311`) |
| `Probe` | `IRevisionProbe` | `HttpRevisionProbe` (`Cli.cs:235`) |
| `Templates` | `ITemplates` (new, `jd.orchestrator`: `ITemplateStore` + `Check(ResolvedGraph)`), from the catalog's template documents | `PulumiTemplates`, a 10-line root adapter over `TemplateLibrary.Parse` (the adapter project may not reference `jd.orchestrator`, standards §3; `HttpRevisionProbe` is the precedent) |
| `Time` | `TimeProvider` | `TimeProvider.System` |

`<data>` is resolved once, by `DataDirectory` (S23 unchanged: nothing set on the machine; `JD_DATA_DIR` the one optional override). The six
nullable seams of `Cli.RunAsync` (`Cli.cs:77-79`) become one `Connections?`; tests pass one with fakes (`with` on the built one). A future API
host builds the same `Connections` from its own configuration; whether `Composition` moves to a shared project now is choice 2.

## 6. Files

The CLI reads; the use cases take and return documents; nothing inside reads a path.

| File | Today | After S25 |
|---|---|---|
| workload, descriptor, definition, base, set (inputs) | `*File.LoadAsync(path)` in the resolver | CLI reads bytes → `SourceDocument`; loaders take documents (S24a1's `DocumentLoader.LoadAsync(source, text)`); the path loaders are deleted |
| catalog directory | `CatalogDirectory.LoadAsync(root)`, `TemplateLibrary.LoadAsync(root/templates)` | CLI reads every `*.yaml`; the catalog loader and `PulumiTemplates` each take their documents; both path loaders deleted |
| manifest + the definitions it names | `ReleaseBuilder.BuildAsync(path)` opens them | `CreateRelease(Manifest, DocumentReader)`: the builder asks the reader for each `definition`; the CLI never reads the manifest's fields |
| release set written (`release create --out`) | `ReleaseSetFile.WriteAsync(set, path)` | result `Document` (bytes); CLI writes with `CreateNew` |
| descriptor written (`env up --out`) | composed text, written by `EnvCli` | result `Descriptor` (bytes); CLI writes, `--force` its choice |
| records, `set.yaml` | through `IRecordStore` | unchanged; the store is a `Connections` member |

**S24f fit.** After S25, `DescriptorComposer` and `ReleaseSetFile` are called only by use cases, and only their insides know the layout. S24f
replaces those insides with the binder and touches no CLI file. The CLI never needs a field of any document (R1 makes it structural), so
S24's literal guard finds nothing in `jd.cli` except its commands and options (documents.md §9).

## 7. Errors

- `LoadError` (`file: location: message`) is unchanged. It is produced where it is today (loaders, resolver, template check), returned in
  `Errors`, and printed by the CLI one per line on stderr, in the same order. Catalog error names are made full paths by the CLI from the
  names it read (`Cli.cs:216-217` without `File.Exists`).
- Expected failures inside a run are `Problem`s at the step where they arise today: the identity adapter's `InvalidOperationException`
  (`Cli.cs:279`), a store's `ArgumentException` (`ReleaseDeployCli.cs:246`), a reader that cannot be built (`:153`), a record write
  (`:264`). The CLI prints `jd: <its words>: <Detail>` as today.
- An `IOException` or `UnauthorizedAccessException` that escapes (a data directory that cannot be written) is mapped by the CLI's dispatch to
  exit 2 once, as the four copies do now.

## 8. Behaviour preservation

- **Byte-identical**: stdout, stderr and exit codes of every CLI test; the deployed-config snapshot; the graph, release-set, record and
  approval goldens. Two known edges, each proven or listed by its slice: a usage error met together with another usage error may report in
  another order (the label is checked in the use case, after the CLI reads `--env`), and `approve`/`promote` have no I/O catch today: if an
  `IOException` can reach them (S25f shows it with a test), it becomes exit 2 with its message instead of an unhandled exception.
- **Tests**: the CLI tests (`Cli.RunAsync`, ~3,000 lines) stay: they are the byte-identity proof; their seam arguments become a `Connections`
  (mechanical). Each rule that moves gets a use-case test in `jd.orchestrator.tests` at its boundary: gate refusal before identity; identity
  before `set.yaml` before the backend factory is called; the kept set re-hash; nothing written on any refusal; `MeaningInvalid` and
  `LabelInvalid` are `InvalidInput`; progress order (`Promoting` before the first `WorkloadStarted`).
- **Azure gate**: the snapshot does not change in any slice. The only direct-Azure file touched is
  `src/backend-providers/jd.bp.pulumi/TemplateLibrary.cs` in **S25b** (`LoadAsync` deleted, `Parse` kept), so S25b runs the gate.
  `HttpRevisionProbe.cs`, `ReleaseFlow.cs`, `Qualifier.cs` and `src/jd.azure` are not edited (`HttpRevisionProbe`'s debt is left to S24e,
  which edits it anyway).

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
| Refusals | **A. `ProblemKind` enum + detail; the client words it** | data; an API maps it to a problem type; command names stay the client's | 15 values | **recommended** |
| | B. message strings from the use case | no enum | "use `jd promote`" is CLI text inside the platform; an API would parse prose | rejected |

Left out on purpose: HTTP, hosting, request/response DTOs (inputs and results are the API's types when it comes), a mediator or command bus, a
DI container in the use cases, per-use-case interfaces.

## 10. Slices and exit criteria

Each slice: `tools/verify.sh` green; CLI tests byte-identical (section 8); snapshot unchanged; **every touched file at the full quality bar**
(conventions, Appendix A's row for the slice fixed in it). Field-structure knowledge in touched resolver files is S24's scope by its slices,
not S25 debt. Lines are production lines (C#, `verify.sh`), estimated, added / removed; cap ~250 changed.

| Slice | Content | Lines | Azure gate | Acceptance |
|---|---|---|---|---|
| **S25a** Connections | `Connections` (type), `Composition.Connect` in jd.cli replacing `Cli.cs:235-311`; `RunAsync`'s six seams → one; `LocalRecordStore` moved to the root; `ThinCliTests` (R1) with today's allowlist; R2 in `verify.sh` | ~+90 / −75 | no | a test per lazy seam (not built when not reached); R1/R2 fail on a planted reference |
| **S25b** Frame, workload commands | `SourceDocument`, `Problem`/`Outcome`, `Progress`, `ITemplates` + `PulumiTemplates`; catalog and workload read by the CLI; path loaders and `TemplateLibrary.LoadAsync` deleted; `Validate`, `Preview`, `Deploy`; one option scanner | ~+170 / −120 | **yes** (`TemplateLibrary.cs`) | gate and type refusals as use-case tests; allowlist shrinks by `Cli.cs` |
| **S25c** Env up | `EnvUp`; `EnvCli` parse/read/write/format only | ~+90 / −70 | no | descriptor golden identical; `--force` and existing `--out` still CLI |
| **S25d** Release create and show | `CreateRelease` (`DocumentReader`), `ShowRelease`; `ReleaseBuilder`, `ReleaseSetFile` take and return documents; `Time` replaces `DateTimeOffset.UtcNow` | ~+90 / −75 | no | release-set golden identical; a missing definition names the resolved path as today |
| **S25e1** Release deploy | `DeployRelease` (preview, recording, walk, IQ, record) | ~+130 / −120 | no | order tests (section 8); record-not-written is `RecordNotWritten` |
| **S25e2** Release qualify | `QualifyRelease` on S25e1's recorded-run steps | ~+50 / −55 | no | never calls `DeployAsync` (test) |
| **S25f** Approve and promote | `Approve` (meaning and label rules as `InvalidInput`), `Promote` (calls `DeployRelease`'s walk with the kept set; `Promotion` leaves the argv options) | ~+120 / −115 | no | every approvals.md §7 test kept; `Promoting` before the walk; nothing written on refusal |
| **S25g** Close | R1 allowlist empty; R3 in `verify.sh`; standards §3 (jd.orchestrator: "use cases and the walk"; jd.cli: "parsing, file I/O, formatting, composition"), conventions "wiring only in jd.cli", components/provisioning pointers | ~+15 / −5, docs | no | R1, R2, R3 pass with no exception |

**Order:** after S24a1 merges and **before S24a2** (recommended, choice 1): S24c1, S24c2, S24e and S24f each edit lines S25 moves out of the
CLI, so S25 first means they edit use cases once instead of CLI code twice. At the latest before S24f, as agreed. Total about **+755 / −635
over 8 slices**, one Azure gate (S25b) plus phase 1.5's exit gate.

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
| 1 | When S25 runs | (a) right after S24a1, before S24a2; (b) after S24e, before S24f | **(a)**: four S24 slices then edit use cases, not CLI code that S25 moves again |
| 2 | Where the composition builder lives | (a) jd.cli now, moved to a host project when the API arrives; (b) a `jd.host` project now | **(a)**: one host today (standards §2); the move is one file |
| 3 | Refusals as data | (a) `ProblemKind` enum, the client words it; (b) message strings from the use case | **(a)**: an API should not parse prose, and CLI command names stay in the CLI |
| 4 | Use-case project | (a) `jd.orchestrator/usecases`; (b) new `jd.application` | **(a)**: standards §3 already calls `jd.orchestrator` the application layer |

## Appendix A. Quality debt in jd.cli and the files S25 touches

| Slice | Debt (rule) |
|---|---|
| S25a | six nullable seam parameters threaded through every command (`Cli.cs:77-79`, `ReleaseCli.cs:20`, `ReleaseDeployCli.cs:39,56,62,85`, `PromoteCli.cs:24`, `ApproveCli.cs:32`) (long parameter list); `Cli` as a static helper bag every command calls (`Print`, `RefuseType`, `ProvisionAsync`, `Records`, …) (one responsibility, finished in S25b) |
| S25b | the exit codes declared in five files (`Cli.cs:24-26`, `EnvCli.cs:14-16`, `ReleaseCli.cs:14-16`, `ReleaseDeployCli.cs:21-23`, `ApproveCli.cs:15-17`, `PromoteCli.cs:17-18`) (literals: an exit code named once); four option-scanning loops (`Arguments.cs:12-52,58-89`, `EnvCli.cs:133-158`, `ReleaseCli.cs:89-110`) and three file-existence checks (`Arguments.cs:92-105`, `EnvCli.cs:106-124`, `ReleaseCli.cs:36-40`) (duplication; S25c/d switch to the one scanner); the I/O catch copied four times (`Cli.cs:127`, `EnvCli.cs:43`, `ReleaseCli.cs:74`, `ReleaseDeployCli.cs:77`) (duplication); `Cli`, `DeployFormatter`, `PreviewFormatter` public where internal suffices (`InternalsVisibleTo` exists) (API and contracts) |
| S25d | `DateTimeOffset.UtcNow` where the injected clock belongs (`ReleaseCli.cs:51`) (resources: a real clock); `ReleaseCli.RunAsync` builds, loads, writes and formats in one method (`:19-79`) (structure) |
| S25e1 | `ReleaseDeployCli.DeployAsync` 117 lines doing load, checks, recording, walk, IQ and record (`:84-201`) (one thing per method) |
| S25f | `Promotion` set by `init` on an argv options record to reuse the walk (`ReleaseDeployCli.cs:25-29,55-59`) (structure); `MeaningMaxLength` copying the schema's limit (`ApproveCli.cs:22-23`) (named once, beside a pointer to the schema); `ApproveAsync` 63 lines with a nested-ternary refusal chain (`ApproveCli.cs:67-130`, `:91-93`) (structure) |
| left to S24e | `HttpRevisionProbe` public where internal suffices: not edited by S25, so no gate (S24e edits it for `probe.scheme`) |
