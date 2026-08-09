# Pulumi Automation API — Key Decisions & Notes

## Core concepts

- **Project** (`Pulumi.yaml`) = the resource definitions/template. **Stack** (`Pulumi.<stack>.yaml` + state) = one parameterized, independently-deployable instance of a project.
- One project can have many stacks (e.g. one per workload, or per environment). Convention: **one stack per workload** for this platform, not per environment.
- Stack size is arbitrary — one resource or hundreds. Splitting into multiple small stacks (e.g. `resource-group`, `app-service`) trades a bit of orchestration complexity for failure isolation and independent lifecycles.

## Automation API basics

- `LocalWorkspace` shells out to a **locally-installed `pulumi` CLI binary**. This is what we use — no Pulumi Cloud account needed.
- `RemoteWorkspace` exists for **Pulumi Deployments** (Pulumi Cloud's managed remote execution from a git repo) — different product, not used here.
- `InlineProgramArgs` — resources defined as a C# function (`PulumiFn.Create(...)`), no project file on disk.
- `LocalProgramArgs` — resources defined in an actual `Pulumi.yaml` on disk (`WorkDir` points at it).

## Pulumi YAML runtime (`runtime: yaml`) vs C# inline program

Two ways to define resources; **we chose YAML templates + generated temp dirs**, not inline C#:

| | Pulumi YAML | C# inline program (`PulumiFn`) |
|---|---|---|
| Provider SDK coupling | **None** — provider plugins resolved at runtime by the CLI | Must reference every provider's typed NuGet package (Pulumi.AzureNative, Pulumi.Aws, ...) |
| Conditionals/loops/branching | Very limited (`fn::each` only) | Full C# — needed for "if workload needs a database, add X" |
| Best fit | Many resource types/providers, template-per-resource-type library | A single, fixed, always-the-same resource shape |

**Decision:** keep resource *definitions* as static YAML templates (`templates/<provider>/<resource-type>/Pulumi.yaml`, checked into git, reusable across all workloads). Never generate the definition dynamically — only the **config values** are generated per run. This avoids the provider-SDK-coupling problem while keeping templates auditable and version-controlled.

## Config (`Pulumi.<stack>.yaml`)

- `stack.SetConfigAsync` / `SetAllConfigAsync` **writes directly into the file on disk**, overwriting the existing value for that key — same effect as `pulumi config set`. Change persists after the process exits.
- No separate "runtime override" layer — by the time `UpAsync()` runs, the file already reflects whatever the code wrote.
- Can fully drive config from code (from parsed `workload.yaml`) — never need to hand-author this file.

## Chaining resources (resource-group → app-service pattern)

- Provision stack A → read `UpResult.Outputs` → `SetConfigAsync` those values into stack B's config → provision stack B.
- Alternative: `StackReference` — reads another stack's outputs **live**, from inside a Pulumi program itself (no config copy, no staleness risk), via exact lookup `<org>/<project>/<stack>` (`organization` is the fixed org name on local backend). Read-only, cannot trigger provisioning.
- **Chosen approach: config-passing via orchestrator code**, not `StackReference` — because one orchestrator already sequences everything and holds the values in memory; `StackReference` earns its keep when stacks are provisioned independently without a shared orchestrator (not our case yet).

## Backend & state

- Backend (`file://~` for local) is set via `PULUMI_BACKEND_URL` env var **in code**, not hardcoded in `Pulumi.yaml` — keeps templates backend-agnostic.
- Local backend still encrypts secrets in state → requires `PULUMI_CONFIG_PASSPHRASE` even non-interactively (empty string is fine for local/dev only).
- **Local state is incompatible with ephemeral hosting** (Container Apps Jobs, CI runners, anything that doesn't persist disk between runs). Moving execution off a persistent dev machine requires switching to a remote backend (e.g. Azure Blob Storage) first.

## Scaling / performance

- Fixed overhead per stack operation: CLI process startup + provider plugin startup (`azure-native` has a very large schema — noticeably slower to init than smaller providers). No hard limit on stack *count*, but this overhead is linear per stack op, so it adds up across many resource-type stacks × many workloads.
- Mitigate via parallelizing independent workloads, not merging stacks that have genuinely independent lifecycles.
- Azure ARM API rate limits are a separate, non-Pulumi concern once provisioning many workloads concurrently.
- `StackReference`/reading `.Outputs` is cheap (state file read or one small API call) — no provider plugin startup involved, unlike `up`/`refresh`.

## Hosting the orchestrator (future — not needed for local MVP)

- Ranked for this platform's goals (audit trail, approval gates): **CI/CD pipeline first** (built-in approval gates + logs = governance for free) → **Container Apps Jobs** once a self-serve platform API exists. Avoid Azure Functions for this (execution timeout risk with multi-stack sequential provisioning).
- Any of these support a writable temp dir (`/tmp` / `%TEMP%`) — not a blocker. Watch out for **Cloud Run/Cloud Functions** specifically (GCP only): default `/tmp` is memory-backed tmpfs, counts against container memory.
- The real bottleneck on serverless/scale-to-zero compute is **Pulumi's plugin cache** (`PULUMI_HOME`), not the scratch temp dir — re-downloading a 100+MB provider plugin on every cold start is slow. Prefer compute that can keep a warm cache or bake plugins into a custom image (Container Apps Jobs, ACI, CI runner) over scale-to-zero functions.

## Chosen architecture (current direction)

1. **Static YAML template library**, one file per resource-type per cloud provider, checked into git, never generated.
2. **Orchestrator loop** (`BackendProvider.DeployAsync`) iterates the resources a workload needs, for each: stage the static template into a fresh temp dir (`Path.GetTempPath()`-based, provider-agnostic across console/web hosting), set config from workload data + prior resources' outputs, `up`, capture outputs, clean up temp dir.
3. **One Pulumi stack per workload** (stack name = workload name) per resource-type project.
4. No cloud provider SDK referenced in the orchestrator project — only `Pulumi.Automation`.
