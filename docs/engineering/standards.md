# Engineering standards

Binding for everyone writing code here, human or agent. The reviewer checks every rule
([review-checklist.md](review-checklist.md)); a violation of a **must** is a blocking finding.

## 1. Nothing hard-coded

The platform exists to turn data (workload definitions, catalog, environment descriptors) into
infrastructure. Code that bakes in what should be data defeats the MVP.

- **Must not** appear in C#: resource names, regions, SKUs, sizes, role GUIDs, resource-type
  knowledge ("Cosmos needs X"), template names, ordering of resources, environment names, URLs,
  subscription/tenant ids. These live in the catalog, the environment descriptor, or configuration.
- **Must not** branch on resource types or workload names (`if (type == "database")`). Add a
  catalog file or a matching rule instead ([resolver.md](../architecture/resolver.md)).
- Configuration comes from options/environment variables bound at the composition root, never read
  ad hoc deep in the code.
- Allowed in code: the catalog/schema *formats*, the expression syntax, built-in functions, and
  defaults that are part of a format's contract (documented next to the format).
- Tests may contain literal fixtures; production code may not.

## 2. Simplest thing that works

- Build only what the current slice needs. No speculative abstractions, extension points, options
  or "for later" code. An interface needs a second implementation or a test seam to exist.
- Prefer deleting code to adding it. Dead code, commented-out code and unused parameters are removed.
- No new dependency (NuGet package, tool) without a stated reason in the PR description.
- No new project unless it marks a dependency boundary (§3).

## 3. Architecture and separation of concerns

Dependencies point inward. Inner layers know nothing about Pulumi, Azure or the CLI.

| Layer | Project(s) | May depend on | Must not depend on |
|---|---|---|---|
| Domain / contracts | `jd.core` | BCL only | everything else |
| Resolution | `jd.resolver` (catalog + engine) | `jd.core`, `jd.definitionvalidator`, YAML/JSON parsing | Pulumi, Azure SDKs, backend providers, CLI |
| Application | `jd.orchestrator` | `jd.core`, `jd.resolver` | Pulumi, Azure SDKs, backend providers, CLI |
| Validation | `jd.definitionvalidator` | `jd.core`, schema libs | Pulumi, Azure SDKs |
| Infrastructure adapters | `backend-providers/jd.bp.pulumi` | `jd.core`, Pulumi Automation, the resolver's public output contract (`ResolvedGraph`, config values, references, `LoadError`) | resolver internals beyond that contract, CLI |
| Composition root | `jd.cli` | everything | — (only wiring, argument parsing, output) |

- `tools/verify.sh` enforces the inner-layer rule mechanically; the reviewer checks the rest.
- One responsibility per type. Side effects (file system, process, Azure) live behind the adapters,
  so domain and resolution logic is pure and unit-testable.
- Follow ADRs in [decisions/](../decisions/README.md) and designs in [architecture/](../architecture/).
  If the code cannot follow them, **stop and escalate** — do not silently diverge.

## 4. Code

- C# conventions per `.editorconfig`; `dotnet format` clean; warnings are errors.
- Nullable reference types honoured; no `!` suppression without a comment explaining why it is safe.
- Async all the way for I/O; pass `CancellationToken` through public async APIs.
- Errors: fail fast with messages a team member can act on; no swallowed exceptions.
- Comments explain *why*, not *what*. Match the surrounding style.

## 5. Tests

- Every behaviour change ships with tests in the matching `*.tests` project.
- Unit tests are offline and deterministic. Resolver behaviour is covered by golden tests
  (definition + catalog → expected graph snapshot).
- Tests that create Azure resources are tagged `[Trait("Category", "Azure")]`, excluded from the
  default gate, and run with `tools/verify.sh --azure` only when the slice requires it.

## 6. Azure and the free tier

The test subscription is guarded by policy ([tools/sandbox](../../tools/sandbox/README.md)), but
the guardrails are a safety net, not the plan. Every resource a slice creates must fit the free tier:

| Service | Rule |
|---|---|
| Cosmos DB | One free-tier account per subscription (substrate). Sum of RU/s across all databases/containers ≤ 1,000. No serverless, no autoscale above 1,000. The account is created with `capacity.totalThroughputLimit` = 1,000 (the `cosmos-account` template's required `totalThroughputLimit` input), so Azure itself refuses anything beyond the free RU/s: the backstop if a mapping or override gets the budget wrong. |
| Container Apps | Consumption profile only; `minReplicas: 0`; 0.25 vCPU / 0.5 Gi. |
| Log Analytics / App Insights | Daily cap ≤ 0.15 GB; sampling on. |
| Anything else | Must be free (role assignments, managed identities) or explicitly approved by the user first. |

- Region comes from `JD_REGION`; never hard-code it.
- Azure tests run as the sandbox team identity, never a personal login: they require `ARM_CLIENT_ID`, `ARM_CLIENT_SECRET`,
  `ARM_TENANT_ID` and `ARM_SUBSCRIPTION_ID` in the environment (`source ~/.just-deliver/<subscription>.env`), fail clearly if any is missing,
  and log `az` in to a per-test temporary `AZURE_CONFIG_DIR` (see `src/jd.cli.tests/AzureCli.cs`). Never fall back to the ambient `az` login.
- Tag every resource group `project=just-deliver-mvp`; substrate groups also `tier=substrate`.
- Tear down test resources when the slice's Azure test finishes: `tools/azure/cleanup.sh --yes`.
- Never change policies, role definitions, app registrations or the sandbox setup. If a guardrail
  blocks you, the design is wrong or the user must decide — escalate.

## 7. Git and PRs

- One slice = one branch = one PR. Branch: `mvp/<slice-id>-<short-slug>` (e.g. `mvp/s03-catalog-loader`).
- Small PRs: aim for under ~400 changed lines excluding generated snapshots. Larger means the slice
  should be split.
- Conventional commits (`feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `chore:`).
- `tools/verify.sh` must pass; a Claude Code hook runs it on every `git commit`, CI runs it on every PR.
- Never force-push shared branches, never rewrite `main`, never commit secrets or `.env` files.
- Docs change in the same PR as the behaviour they describe. ADR status is changed only by the user.

## 8. Definition of done (per slice)

1. Acceptance criteria in the slice brief met.
2. Tests added; `tools/verify.sh` green (and `--azure` if the slice touches Azure, with cleanup done).
3. Reviewer verdict **APPROVE** with no open blocking findings.
4. Docs updated if behaviour or design changed.
5. [status.md](../plans/status.md) updated by the lead: slice state, PR link, anything learned.
