# Engineering standards

Every binding code rule, for everyone writing code here, human or agent; C# idioms are in [csharp.md](csharp.md). The bar is
good code, not working code: a violation of a rule here is a blocking finding however correct the code is and whatever the
checks say ([review-checklist.md](review-checklist.md)).

## 1. Nothing hard-coded

The platform turns data (workload definitions, catalog, environment descriptors) into infrastructure; code that bakes in what
should be data defeats it.

- **Must not** appear in C#: resource names, regions, SKUs, sizes, role GUIDs, resource-type knowledge ("Cosmos needs X"),
  template or output names, ordering of resources, environment names, URLs, subscription/tenant ids, Azure behaviour. These live
  in the catalog (templates, components, policies), the environment descriptor or configuration, and the code reads them by the
  names the data gives.
- **Must not** branch on a resource type, template or workload name (`if (type == "database")`). Add catalog data or a policy
  instead ([documents.md](../architecture/documents.md)).
- **Formats are data.** No document's structure in C#: no field name, kind name, location built from field names, or type
  mirroring a file. Each kind's schema in `schemas/` is its shape, and its `x-jd` annotations say which field holds which
  concept; a default that is part of a format's contract is the schema's `default`
  ([documents.md](../architecture/documents.md) §3, [ADR 0019](../decisions/0019-engine-knows-concepts.md)). A document's
  error is located where the binder read the value.
- Allowed in code: the engine's **concepts** ([documents.md](../architecture/documents.md) §2): the document and its `kind`, names
  and references, parts, nodes and exports, the expression syntax with its closed root list and built-in functions, scopes and the
  layers' order, selection, contracts, grants, policies, release, gates and evidence; and the exceptions section 2 names: the
  `kind` and `x-jd` keywords, concept and root names that equal a field, external protocols (`Pulumi.yaml`, Entra claims, ARM),
  jd's identifiers and node hash, and jd's JSON evidence (release record, approval record, graph JSON).
- **The literal guard** (`LiteralGuardTests`) fails on a production literal that names a schema field; its allowlist is section
  2's exceptions plus prose a lone word cannot be told apart from. A literal the guard allows can still hide structure: the
  reviewer judges role names (against [documents.md](../architecture/documents.md) Appendix B) and message prose.
- **Literals are named once.** A string the code depends on is a named constant, not spelled inline: an environment variable, a
  CLI command or option, an output or record key written or read in more than one place, an exit code. A one-off message stays
  inline.
- **Configuration** (options, environment variables) is read once in the composition root, by names declared once, and passed in
  as values; never read ad hoc deep in the code.
- Exception: Azure protocol constants (an endpoint, a token scope) may be named constants inside an Azure adapter project
  (`jd.azure`), with a comment saying so. Public cloud only; sovereign clouds need configuration (E53).
- Tests may contain literal fixtures (production code may not) and spell what they expect themselves, never through the
  constants of the code under test.

## 2. Simplest thing that works

- Build only what the current slice needs: no code it does not need, no speculative abstraction, extension point, option, or
  helper or table entry that does nothing yet; no refactor or reformatting bundled in. An interface (port) needs a second
  implementation or a test seam to exist.
- Remove a concept before adding one; prefer an exact lookup to a matching rule, one mechanism to two, deleting code to adding it.
  A design note names the simpler alternative it rejected and why.
- Dead code, commented-out code and unused parameters are removed.
- **Dependencies:** a new package or tool needs a line in the PR saying why the BCL or an existing one is not enough; no large
  library for a small problem.
- No new project unless it marks a dependency boundary (§3).

## 3. Architecture and separation of concerns

Dependencies point inward. Inner layers know nothing about Pulumi, Azure or the CLI.

| Layer | Project(s) | May depend on | Must not depend on |
|---|---|---|---|
| Domain / contracts | `jd.core` | BCL only | everything else |
| Resolution | `jd.resolver` (catalog + engine) | `jd.core`, `jd.definitionvalidator`, YAML/JSON parsing | Pulumi, Azure SDKs, backend providers, CLI |
| Application (use cases and the walk) | `jd.orchestrator` | `jd.core`, `jd.resolver` | Pulumi, Azure SDKs, backend providers, `jd.azure`, CLI |
| Validation | `jd.definitionvalidator` | `jd.core`, schema libs | Pulumi, Azure SDKs |
| Infrastructure adapters | `backend-providers/jd.bp.pulumi` | `jd.core`, Pulumi Automation, the resolver's public output contract (`ResolvedGraph`, config values, references, `LoadError`) | resolver internals beyond that contract, CLI |
| Infrastructure adapters (Azure) | `jd.azure` | `jd.core` (the ports it implements, e.g. `IDeployerIdentity`), Azure SDKs | `jd.resolver`, `jd.orchestrator`, Pulumi, backend providers, CLI |
| Composition root | `jd.cli` | everything | — (only parsing, file I/O, formatting, composition: [application.md](../architecture/application.md) §4) |

- Pulumi only in `jd.bp.pulumi`, Azure SDKs only in `jd.azure`, wiring only in `jd.cli`, which holds no rule. `tools/verify.sh`
  and `ThinCliTests` enforce part of this mechanically; the reviewer checks the rest.
- Ports in `jd.core`, adapters at the edge; a port is never defined away from its consumer's layer.
- One responsibility per type. Side effects (file system, process, Azure, the clock, delays) live behind the adapters, so
  parsing, resolution, rules and record building are pure and unit-testable.
- Code follows the ADRs in [decisions/](../decisions/README.md) and the designs in [architecture/](../architecture/); code that
  cannot is escalated ([conventions](conventions.md#escalation)), never merged diverging.

## 4. Design invariants

These hold across slices; a change to one changes its design note or ADR first. The user's design principles
([context.md](../context.md#design-principles)) bind every design: explicit over implicit (6), simplification (7, §2 here),
cohesion by component (8), contract over implementation (9), one definition everywhere (10), one owner per resource (11), least
privilege (12), prove then extend (13), serve the north star (14). In code:

- **Explicit over implicit.** Every resource, grant and setting a workload gets comes from something written down: its
  `requires`, a component's `uses`, an environment's offers. A guardrail policy *requires* (and fails a workload that lacks it);
  it never adds. Implicit, platform-owned resources (resource groups, the Container Apps environment, the shared Cosmos account)
  are a short named list, and workloads are never granted on them. A grant targets only what the workload's own requirements
  created. A design that adds an implicit resource, grant, default or match needs the user's go-ahead.
- **A bad revision never gets traffic.** Dark deploy, grants, probe, then shift; a failed step stops the walk and leaves traffic
  where it was ([provisioning.md](../architecture/provisioning.md)).
- **Evidence is written once.** Release records and `set.yaml` are created with `CreateNew` and never edited; a re-run adds a
  record ([release-record.md](../architecture/release-record.md), ADR 0007).
- **Records are built from an allowlist,** never by serializing a report; no secret can reach them by construction.
- **Secrets and data.** No secret in code, a fixture, a log, an error, the console or a release record; no value the code did not
  need copied into output "for context". External input (definitions, set files, labels, Azure replies) is validated at the
  boundary before use.
- **What runs is observed live.** IQ reads traffic and images from Azure, never from state or the record; a deploy never hides
  drift from `qualify`.
- **The identity is the platform's, not the data's.** Who deployed comes from the credential's token, never from a definition or
  set file.

## 5. Tests

- Every behaviour change ships with tests in the matching `*.tests` project; every bug fix with a test that fails without it.
- **Every test can fail.** For every branch, guard and error path a change adds, break it on purpose (invert the condition, drop
  the call, return the happy value), see a test fail, restore it. A branch no test notices is blocking; a surviving mutation in
  changed code is blocking unless the PR shows why it is equivalent.
- **Required tests prove boundaries.** Each required test in a brief names a boundary (a secret that must not appear, a call that
  must not happen, an order that must hold); the test proves the boundary, not only the happy path around it.
- **Error tests pin the error:** its type and the part of the message that is the contract (the variable it names, the node,
  `pinned …, live … on revision …`), and what did *not* happen (no backend call, no file written).
- **No logic decides what a test asserts.**
- **Fakes only at boundaries:** the backend (`IBackEndProvider`), Azure (identity, live revisions), the HTTP probe, the clock and
  delays, the file system where a temp directory is impractical. Everything inside — resolver, orchestrator, release flow, record
  builder — runs for real.
- **Offline, deterministic and fast.** Unit tests never touch the network or Azure, never sleep, and use a `TimeProvider` and
  injected delays. A flaky or slow test is a bug to fix, never a retry.
- **Goldens.** Resolver behaviour is covered by golden tests (definition + catalog → expected graph). Golden files change only on
  purpose, regenerated by a deliberate step and reviewed in the diff like code; a test run never rewrites them.
- **Azure tests** prove what offline tests cannot — Azure's behaviour — and assert the exact line that shows it (the image-check
  text, the traffic weights), not only an exit code. They leave nothing behind. They are tagged `[Trait("Category", "Azure")]`,
  excluded from the default gate, and run only through `tools/verify.sh --azure` (who runs it and when:
  [conventions](conventions.md#azure-gate)): `AzureCli.Login` throws unless `JD_AZURE_TESTS=1`, which the script sets, so a plain
  `dotnet test` never reaches Azure. The script holds an exclusive per-host, per-user lock
  (`${XDG_RUNTIME_DIR:-/tmp}/just-deliver-azure.lock`) because the subscription has one free-tier Cosmos account; a run from
  another machine on the same subscription is not covered.

## 6. Azure and the free tier

The test subscription is guarded by policy ([tools/sandbox](../../tools/sandbox/README.md)), but the guardrails are a safety net,
not the plan. Every resource a change can create must fit the free tier — blocking even when the guardrail would stop it:

| Service | Rule |
|---|---|
| Cosmos DB | One free-tier account per subscription (the shared `cosmos-account`). Sum of RU/s across all databases/containers ≤ 1,000. No serverless, no autoscale above 1,000. The account is created with `capacity.totalThroughputLimit` = 1,000 (the `cosmos-account` template's required `totalThroughputLimit` input), so Azure itself refuses anything beyond the free RU/s: the backstop if a mapping or override gets the budget wrong. |
| Container Apps | Consumption profile only; `minReplicas: 0`; 0.25 vCPU / 0.5 Gi. |
| Log Analytics / App Insights | Daily cap ≤ 0.15 GB; sampling on. |
| Anything else | Must be free (role assignments, managed identities) or explicitly approved by the user first. |

- Region comes from the environment definition's `region`; never hard-coded.
- Azure tests run as the sandbox team identity, never a personal login: they require `ARM_CLIENT_ID`, `ARM_CLIENT_SECRET`,
  `ARM_TENANT_ID` and `ARM_SUBSCRIPTION_ID` in the environment (`source ~/.just-deliver/<subscription>.env`), fail clearly if any is
  missing, and log `az` in to a per-test temporary `AZURE_CONFIG_DIR` (`src/jd.cli.tests/AzureCli.cs`). Never fall back to the
  ambient `az` login.
- Every resource has a teardown path. Tag every resource group `project=just-deliver-mvp`; the shared and environment groups also
  `tier=substrate`. Test resources are torn down after the Azure run: `tools/azure/cleanup.sh --yes`.
- Never change policies, role definitions, app registrations or the sandbox setup. A guardrail in the way is escalated
  ([conventions](conventions.md#escalation)).

## 7. Git and PRs

- One slice = one branch = one PR. Branch: `mvp/<slice-id>-<short-slug>` (e.g. `mvp/s03-catalog-loader`).
- Conventional commits (`feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `chore:`).
- `tools/verify.sh` passes on every commit: a Claude Code hook runs it on every `git commit`, CI on every PR. What it enforces is
  the script itself ([tools/verify.sh](../../tools/verify.sh)); fix a failure, never bypass it.
- Never force-push shared branches, never rewrite `main`, never commit secrets or `.env` files.
- Docs change in the same PR as the behaviour they describe. A change to a persisted or shared format (catalog and mapping
  schemas, release-record schema, golden files, set files, Pulumi stack names) changes its schema, its goldens, its docs and the
  readers of older files in the same PR, or says why old files stay valid.
