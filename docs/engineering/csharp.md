# C# conventions

Language-level rules for all C# in this repository: naming, types, methods, errors, async, tests and idioms.
[standards.md](standards.md) holds the project rules (nothing hard-coded, layers, the free tier, git) and
[conventions.md](conventions.md#code-quality-bar) the quality bar; this page does not repeat them. Where a tool
enforces a rule, the tool's settings (`.editorconfig`, `Directory.Build.props`, `tools/verify.sh`) are the source of
truth and this page explains the rest.

**Golden rule:** code is read far more often than it is written. Write for the next reader.

Every rule here is **blocking** in review, except the numbers marked *aim*: going past one needs a reason the reviewer
accepts (a method that reads better whole, a table-driven switch). A deviation is allowed when the PR says why;
repeated deviations mean this page should change.

## 1. Tooling

| Concern | Enforced by | Setting |
|---|---|---|
| Language and runtime | `Directory.Build.props` | .NET 10, latest C#, implicit usings |
| Formatting | `dotnet format --verify-no-changes` in `verify.sh` | `.editorconfig` |
| Style and naming | analyzers in the build (`EnforceCodeStyleInBuild`, `AnalysisLevel` latest) | `.editorconfig` |
| Warnings | `TreatWarningsAsErrors` | NuGet advisories: low and moderate stay warnings, high and critical fail |
| Nullability | `Nullable` enabled | — |
| Layer boundaries | `verify.sh` project-reference checks; `ThinCliTests` IL scan | [standards §3](standards.md#3-architecture-and-separation-of-concerns), [application.md](../architecture/application.md) |
| Secrets in git | `verify.sh` refuses tracked `.env` and `.pulumi` state | [standards §7](standards.md#7-git-and-prs) |
| Local gate | Claude Code hook runs `verify.sh` on every commit; `.githooks/pre-push` rejects pushes to `main` | — |
| CI | `.github/workflows/ci.yml` runs `verify.sh`; `main` requires the `verify` check | — |

A warning is fixed, or suppressed **on its own line** with a reason the reviewer agrees with:
`#pragma warning disable CA1031 // the probe reports every failure as a result`. Never suppress for a whole file or
project to make the build pass.

## 2. Naming

`.editorconfig` enforces PascalCase types, members, constants and static readonly fields, `_camelCase` private
fields and `I`-prefixed interfaces. Beyond that:

- Types are nouns (`ReleaseRecord`); methods are verbs (`ResolveAsync`, `BuildGraph`).
- Booleans read as a question: `IsProtected`, `HasOutputs`, `CanDeploy`.
- Names carry units: `timeoutSeconds`, `sizeBytes`, `TimeSpan` where the type can carry them.
- No abbreviations except well-known ones (`id`, `url`, `json`). No single letters except loop indices, lambdas of one
  short line, and maths.
- Methods returning `Task` or `ValueTask` end in `Async`.
- No type named `Utils`, `Helpers` or `Manager`; name it after what it does (`StackNames`, `DefinitionHash`).
- Tests are named as a sentence stating the behaviour, words joined by `_`:
  `A_release_deploys_the_workloads_in_the_order_given`.

## 3. Types

- **Sealed by default.** A class is `sealed` unless it is designed for inheritance; inheritance at most one level
  deep outside framework base classes. Prefer composition.
- **Immutable by default.** `record` (or `readonly record struct`) for values; `init` over `set`;
  `IReadOnlyList`/`IReadOnlyDictionary` for exposed collections. Mutate only where it is needed.
- **Accept abstract, return concrete:** take `IEnumerable<T>` or `IReadOnlyList<T>`; return the concrete or read-only
  type the caller can use without casting.
- **Closed sets are enums,** never bare strings or ints. Values the data defines (resource types, kinds, output names)
  are not closed sets: they come from the data ([standards §1](standards.md#1-nothing-hard-coded)).
- **No `dynamic`.** `object` only with a comment saying why. Casts only at proven-safe boundaries, with a comment.
- **Nullability is the contract.** No `!` without a comment saying why it is safe; return a result or throw rather
  than `null` where the caller cannot act on `null`.
- **Plain values, not file shapes.** A value with rules (an id, a path inside a root, a UTC time) is parsed once at
  the boundary into a type that keeps them. A file is never mirrored by a C# type: file shapes belong in the schemas
  and the code reads documents by the names the data gives (user's rule).

## 4. Design

The quality bar and the design rules in [conventions.md](conventions.md#design-rules-that-code-must-keep) apply; in C#
that means:

- **Simplest thing that works** ([standards §2](standards.md#2-simplest-thing-that-works)): no speculative
  abstractions, options or extension points; an interface needs a second implementation or a test seam.
- **No duplication.** Duplicated logic is extracted the first time it is seen, not on the third copy; two lines that
  only look alike but change for different reasons stay apart.
- **Functional core, imperative shell.** Parsing, resolution, rules and record building are pure; files, processes,
  Azure, the clock and delays sit at the edges, behind ports.
- **Dependencies are injected** through constructors or parameters. No static mutable state, singletons, or
  collaborators constructed deep inside logic. Composition happens in the composition root only.
- **Command–query separation.** A method either changes state or answers a question, not both.
- **Law of Demeter.** Talk to collaborators, not their internals (`a.B.C.D()` is a smell).
- **Explicit over implicit.** No reflection-driven magic, no hidden defaults; optional parameters only where every
  caller would otherwise pass the same value.

## 5. Methods

- *Aim* ≤ 40 lines per method and ≤ 3 levels of nesting; at most 5 parameters, beyond that a record or a smaller seam.
- Early returns and guard clauses over nested `if`s.
- No boolean parameter that switches behaviour; write two methods or pass a value that names the choice.
- Return values rather than mutating arguments; `out` parameters only in the `TryX` pattern.
- Properties are cheap and side-effect-free; anything that does I/O or real work is a method.

## 6. Errors

- **Expected outcomes are results, not exceptions:** a failed probe, failed IQ, drift, a load error (`LoadError`,
  `LoadResult<T>`). Exceptions are for bugs and for failures the caller cannot handle. There is no exception
  hierarchy per project; add an exception type only when a caller catches it.
- **Catch the narrowest type you handle.** `catch (Exception)` only at an outer boundary that reports, with a reason
  on the line. Use `when` filters rather than catch-and-rethrow.
- **Keep the cause:** wrap with the inner exception, or `throw;` to rethrow; never `throw ex;`.
- **Translate at the boundary:** SDK, HTTP and OS errors become messages or results in the adapter; the CLI maps
  results to exit codes and text.
- Keep `try` blocks around the statement that can fail. Release resources with `using`, not hand-written `finally`.
- `Debug.Assert` only for internal invariants, never to check input.
- Messages say what failed, with which input, and what to do next.

## 7. Data, I/O and resources

- **Time:** `TimeProvider` and `DateTimeOffset` in UTC; never `DateTime.Now`. Format and parse with
  `CultureInfo.InvariantCulture`.
- **Strings:** `StringComparison.Ordinal` (or `OrdinalIgnoreCase`) for identifiers and keys; culture-sensitive
  comparison only for text shown to people.
- **Files:** UTF-8 without BOM; paths from input are resolved and checked to stay inside their root, rejecting `..`
  and separators.
- **Structured formats:** JSON and YAML through their libraries (`System.Text.Json`, YamlDotNet); never built by
  string concatenation.
- **Network and processes:** every call has a timeout and a `CancellationToken`. Retry only transient errors, with
  backoff and a cap, in one place. Processes start with `ArgumentList`, never a shell string.
- **Configuration:** environment variables and options are read once in the composition root, by names declared
  once ([conventions](conventions.md#code-quality-bar), Literals), and passed in as values.

## 8. Async

- Async all the way for I/O; no `.Result`, `.Wait()` or `GetAwaiter().GetResult()`.
- No `async void` (event handlers aside). No fire-and-forget: every task is awaited or owned.
- Pass the `CancellationToken` through every async call that takes one; `Task.WhenAll` for independent work.
- No `Thread.Sleep` or `Task.Delay` on the real clock in logic; inject the delay or the `TimeProvider`.

## 9. Output and logging

- Only `jd.cli` writes to the console. Use cases report through their results and the one progress sink
  ([application.md](../architecture/application.md)).
- An error is reported once, where it is handled, not logged and rethrown.
- Never write secrets, tokens or full untrusted payloads to output, errors or records.

## 10. Tests

[conventions.md, Tests](conventions.md#tests) holds the rules (every test can fail, fakes only at boundaries, error
tests pin the error, offline and fast, goldens on purpose). In C#:

- Arrange, act, assert; one behaviour per test; theories (`[Theory]` with data) for variations of one behaviour.
- Test through public or internal APIs, never private members or reflection on implementation details.
- Fakes (small working implementations) over mocking libraries.
- Every bug fix comes with a test that fails without it.
- Temporary directories per test, deleted after it; tests run in any order.
- Azure tests carry `Category=Azure` and never run without `JD_AZURE_TESTS=1`.

## 11. Documentation and comments

- A public type or member has an XML doc comment saying what it is for (the quality bar; review enforces it while
  `CS1591` stays off).
- Comments explain **why**; if code needs a *what* comment, rename or extract instead.
- No commented-out code; git keeps it.
- A `TODO` names the slice or issue that removes it: `// TODO(S25b2): ...`.

## 12. Performance

- Measure before optimising; readability wins until a measurement says otherwise.
- The right structure first: `HashSet`/`Dictionary` for lookups, not a list scan.
- Hoist invariant work out of loops; `StringBuilder` or `string.Join` instead of `+=` in loops.

## 13. Idioms

- `is null` / `is not null`, never `== null`. `Count`/`Length` or `Any()` on enumerables, not `Count() > 0` on lists.
- Pattern matching and `switch` expressions for dispatch on shape; a plain `if` for one condition.
- Collection expressions (`[]`, `[..a, b]`) and target-typed `new()` where the type is clear.
- `nameof` over string literals for member names.
- LINQ for queries one level deep; a loop when it reads better or mutates.
- File-scoped namespaces, one top-level type per file, file named after it.
- The BCL before a new package ([standards §2](standards.md#2-simplest-thing-that-works)).
