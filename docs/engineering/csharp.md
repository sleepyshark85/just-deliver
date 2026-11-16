# C# conventions

C# idioms for all code in this repository: naming, types, methods, errors, resources, async, output, tests and comments.
The project's code rules (nothing hard-coded, simplicity, layers, tests, the free tier) are in [standards.md](standards.md)
and are not repeated here. Where a tool enforces a rule, the tool's settings (`.editorconfig`, `Directory.Build.props`) are the
source of truth and this page explains the rest.

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

A warning is fixed, or suppressed **on its own line** with a reason the reviewer agrees with:
`#pragma warning disable CA1031 // the probe reports every failure as a result`. "To make the check pass" is not a reason, and
nothing is suppressed for a whole file or project to make the build pass.

## 2. Naming

`.editorconfig` enforces PascalCase types, members, constants and static readonly fields, `_camelCase` private
fields and `I`-prefixed interfaces. Beyond that:

- A name says what a thing is or does.
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
- **Smallest visibility.** A type or member is public only when it must be; otherwise internal or private.
- **Nullability is the contract.** No `!` without a comment saying why it is safe; return a result or throw rather
  than `null` where the caller cannot act on `null`.
- **Plain values.** A value with rules (an id, a path inside a root, a UTC time) is parsed once at the boundary into a
  type that keeps them (file shapes: [standards §1](standards.md#1-nothing-hard-coded)).

## 4. Design

- **No duplication.** Duplicated logic is extracted the first time it is seen, not on the third copy; two lines that
  only look alike but change for different reasons stay apart.
- **Dependencies are injected** through constructors or parameters. No static mutable state, singletons, or
  collaborators constructed deep inside logic. Composition happens in the composition root only.
- **Command–query separation.** A method either changes state or answers a question, not both.
- **Law of Demeter.** Talk to collaborators, not their internals (`a.B.C.D()` is a smell).
- **No magic.** No reflection-driven behaviour, no hidden defaults; optional parameters only where every
  caller would otherwise pass the same value.

## 5. Methods

- A method does one thing.
- *Aim* ≤ 40 lines per method and ≤ 3 levels of nesting; at most 5 parameters, beyond that a record or a smaller seam.
- Early returns and guard clauses over nested `if`s.
- No boolean parameter that switches behaviour; write two methods or pass a value that names the choice.
- Return values rather than mutating arguments; `out` parameters only in the `TryX` pattern.
- Properties are cheap and side-effect-free; anything that does I/O or real work is a method.

## 6. Errors

- **Expected outcomes are results with a reason, not exceptions:** a failed probe, failed IQ, drift, a load error
  (`LoadError`, `LoadResult<T>`). Exceptions are for bugs and for failures the caller cannot handle. There is no exception
  hierarchy per project; add an exception type only when a caller catches it.
- **Catch the narrowest type you handle.** `catch (Exception)` only at an outer boundary that reports, with a reason
  on the line. Use `when` filters rather than catch-and-rethrow.
- **Never swallow an error or lose its cause:** wrap with the inner exception, or `throw;` to rethrow; never `throw ex;`.
  A bug is never turned into a silent default.
- **Translate at the boundary:** SDK, HTTP and OS errors become messages or results in the adapter; the CLI maps
  results to exit codes and text. Nothing reaches the user raw (an SDK type, a stack trace, `Retry failed after 4 tries`).
- **Report once,** where the error is handled; never logged and rethrown.
- Keep `try` blocks around the statement that can fail.
- `Debug.Assert` only for internal invariants, never to check input.
- Messages say what failed, with which input, and what to do next, so a team member can act on them.

## 7. Data, I/O and resources

- **Resources:** a stream, file, process or client is released on every path, with `using`, not hand-written `finally`.
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

## 8. Async

- Async all the way for I/O; no `.Result`, `.Wait()` or `GetAwaiter().GetResult()`.
- No `async void` (event handlers aside). No fire-and-forget: every task is awaited or owned.
- Pass the `CancellationToken` through every async call that takes one; `Task.WhenAll` for independent work.
- No `Thread.Sleep` or `Task.Delay` on the real clock in logic; inject the delay or the `TimeProvider`.

## 9. Output and logging

- Only `jd.cli` writes to the console. Use cases report through their results and the one progress sink
  ([application.md](../architecture/application.md)).
- No token or full untrusted payload in output, errors or records (secrets: [standards §4](standards.md#4-design-invariants)).

## 10. Tests

The rules are in [standards §5](standards.md#5-tests); in C#:

- Arrange, act, assert; one behaviour per test; theories (`[Theory]` with data) for variations of one behaviour.
- Test through public or internal APIs, never private members or reflection on implementation details.
- Fakes (small working implementations) over mocking libraries.
- Temporary directories per test, deleted after it; tests run in any order.

## 11. Documentation and comments

- A public type or member of a production project has an XML doc comment saying what it is for (`CS1591`, on for every
  project under `src/` except `*.tests`).
- Comments explain **why** (an Azure rule, a design-note section, a residual risk); a comment that restates the code is
  removed, and code that needs a *what* comment is renamed or extracted instead. Match the surrounding style.
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
