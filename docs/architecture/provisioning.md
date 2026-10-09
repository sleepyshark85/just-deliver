# Provisioning

> Status: design reference — not yet reviewed as decisions. Proposed decisions live in [decisions/](../decisions/).

How a workload definition ([workload-definition.md](workload-definition.md)) becomes Azure resources, and
how the Pulumi Automation API is operated. Proposed resolver design: [resolver.md](resolver.md). Flow
diagram: [workload_deployment_flow.html](workload_deployment_flow.html). Rationale for the tooling and model
is in [0002 Pulumi Automation API](../decisions/0002-pulumi-automation-api.md),
[0003 Pulumi YAML template library](../decisions/0003-pulumi-yaml-template-library.md) and
[0004 two-layer definition model](../decisions/0004-two-layer-definition-model.md) — not repeated here.

## Two-layer model

Provisioning is definition-driven: there is no fixed infrastructure template; what gets built depends on
what the team declares, merged with what IT Ops mandates.

| Layer | Owner | Example | Merged |
|---|---|---|---|
| Team definition | Workload team | `requires: [database, ...]` — the platform provisions exactly what is declared, nothing more | at deploy time |
| Platform mapping | Platform team | `database` → concrete type + blessed properties per environment | at deploy time |
| Global policy | IT Ops | "all workloads receive traffic through Azure Application Gateway"; encryption, monitoring, tags | at deploy time; teams never declare them |

Minimal team definitions, centralised mapping intelligence, policy-enforced standards, a visible resolution chain, and an override escape hatch whose usage drives mapping improvements — scaling to many resource types without teams changing how they write definitions.

## Platform mappings

A mapping is the "blessed" configuration for a resource type. Illustrative values from the original design:

| Type | Target | Properties |
|---|---|---|
| `database` | `azure-sql-database` | sku `Standard_S1`, backup_retention_days 30, encryption_at_rest true, high_availability false, connection_timeout 30, monitoring true |
| `cache` | `azure-redis` | sku `Basic`, eviction_policy `allkeys-lru`, persistence false, monitoring true |
| `queue` | `azure-service-bus` | sku `Standard`, max_message_size 256 KB, default_ttl 14 days, monitoring true |

The only mapping implemented today resolves `cosmos-sql` to a **Cosmos DB** SQL container (partition key `/id`) in
the environment's shared database, with a container-scoped grant — see `catalog/mappings/cosmos-sql` and
`catalog/templates/azure`. Engine choice per type is still open (see [mvp.md](../plans/mvp.md#needs-decision)).

**Environment-specific values** (illustrative, `database`):

| Env | sku | backup_retention_days | high_availability |
|---|---|---|---|
| dev | Standard_S1 | 7 | false |
| staging | Standard_S2 | 14 | false |
| production | Premium_P2 | 30 | true |

**Governance:** single source of truth (one or few files), version-controlled with reviewed changes,
each mapping documents *why* it is configured that way, and is validated by the platform team before use.
Mappings and policies are **data, versioned independently** of the engine; a deployment records the mapping
version that resolved it alongside the definition SHA (C52, [resolver.md](resolver.md)).

**Shared vs dedicated (C12):** shared resources belong to the environment tier (one database server, one
Azure Monitor workspace per environment so telemetry correlates); each workload gets its own database and
App Insights component inside them. Ownership tiers: landing zone → environment → workload.

## Global IT Ops policies

| Policy | Applies to | Effect | Reason |
|---|---|---|---|
| `enforce-encryption` | database, cache, queue | `encryption_at_rest`, `encryption_in_transit` = true | Compliance: all data encrypted at rest and in transit |
| `enforce-geo-redundancy-prod` | database, when env = production | `high_availability`, `geo_redundancy` = true | SLA: prod databases highly available |
| `enforce-monitoring` | database, cache, queue (and every workload) | `monitoring`, `app_insights` = true | Observability: everything reports to App Insights |
| `enforce-compliance-tags` | `*` | inject tags `cost-center: platform-ops`, `compliance: sox-2-compliant`, `data-classification: internal` | Billing and compliance tracking |

Policies have `applies_to`, optional `condition`, and `enforce` (set fields) or `inject` (merge tags). Platform-side policy is the paved road and UX; real guardrails are Azure Policy/RBAC in the landing zone
([0008](../decisions/0008-enforcement-in-landing-zone.md); F41 split still open).

### Precedence

Intended order, lowest to highest: **team definition → platform mapping (same environment) → global
policy**. Example: team says `database`; dev mapping sets `encryption_at_rest: false` for cost; policy sets
it `true`; result is encrypted — policy wins.

**Known pitfall (C52):** the original algorithm applied team overrides *after* policies, so a whitelisted
override beat a compliance policy, contradicting the stated precedence. Overrides must sit between mapping
and policy (or be rejected when they touch a policy-enforced field). Second pitfall: shallow merge with no
provenance — two policies touching one field conflict silently. Provenance must be per field
(`{value, source, rule}`), not per stage. Both are addressed in [resolver.md](resolver.md).

## Resolution transparency

For each resource the platform shows the chain: (1) team definition, (2) platform mapping for type +
environment, (3) policies applied (e.g. `enforce-encryption: encryption_at_rest = true (enforced)`,
`enforce-monitoring: already enabled (no change)`), (4) team overrides, (5) final configuration with
generated resource id (e.g. `sql-my-service-staging-001`), each value marked with its source, plus outputs
such as the endpoint.

| Where shown | Purpose |
|---|---|
| Deployment preview, before provisioning | Team sees what they will get |
| Deployment status, after provisioning | Ops confirms what was created |
| Release record (permanent) | Audit: workload, environment, timestamp, per resource: type, resourceId, teamDefinition, platformMapping, globalPoliciesApplied, finalConfig |

Why: teams understand why a resource is configured that way; ops see which policies applied and where
conflicts were resolved; debugging; compliance evidence; and teams who disagree with a policy know whom to
talk to. Secret values must never appear in the chain (D23).

## Provisioning flow

Original flow: team submits definition → API parses and validates → global policies merged → dynamic
resource graph constructed → background worker executes via Pulumi Automation API → status streamed back
(never a blocking API call; the API enqueues a job, the worker runs Pulumi in-process).

Refined by settled decisions ([0006](../decisions/0006-infra-deployed-with-app.md), D17, E28, C52):

1. Validate schema (pre-upload too, H50); store the uploaded definition and its SHA ([0007](../decisions/0007-definition-upload-snapshot.md)).
2. Resolve — pure, backend-agnostic `resolve(definition, environment, mappings, policies) → graph + provenance`
   with open references ([resolver.md](resolver.md)). Per requirement: find mapping (env-specific, else
   default; none → error), apply overrides (whitelist, prod approval), apply policies, validate final config,
   generate resource id, record chain.
3. Preview and approval gate on the resolved graph and `PreviewAsync`: sensitive-diff approval, replacement of
   protected resources ([0010](../decisions/0010-replacement-protection-classes.md)), destructive removals (C16).
4. Durable step loop (E29/E30), filling each step's inputs from prior outputs: infra → new app revision with
   no traffic + migration job (identities created) → grants → verify propagation → `preDeploy` (migration) →
   `verify` → shift traffic → `postDeploy` ([0014](../decisions/0014-hook-points.md)).
5. End states: live, degraded (`postDeploy` failed), failed-partial (infra kept, traffic unchanged; retry
   resumes at the failed step). Infra is forward-only; rollback is app-only (A1 rider 2).

**B/C → D:** the resolution chain and override structure stay identical; only the executor changes
([0005](../decisions/0005-build-bc-first-design-for-d.md)). B: platform generates a precise runbook, ops
execute it and can approve/reject overrides, audit compares done vs planned. C: chain becomes a structured
work order, ops system validates overrides, runbooks generated. D: Automation API executes the graph;
overrides auto-approved in dev/staging, IT Ops approval in production.

**Questions carried from the original design**, now tracked in [open-questions.md](../open-questions.md):
how many resource types (database, cache, queue, storage, secrets, cdn, service-bus, app-insights, ...);
inter-resource dependencies such as a read replica (C52 graph); naming (C14); runtime config injection
(D22); references in code (C13); partial failure (E31); scaling a resource without redeploying the app
(no answer yet — under [0006](../decisions/0006-infra-deployed-with-app.md) infra changes ship with a deploy).

## Pulumi operational notes

### Concepts and workspaces

- **Project** (`Pulumi.yaml`) = resource definitions. **Stack** (`Pulumi.<stack>.yaml` + state) = one
  parameterised, independently deployable instance. Stack size is arbitrary; small per-resource-type stacks
  trade orchestration complexity for failure isolation and independent lifecycles.
- Convention: **one stack per graph node**, named from the node id ([resolver.md](resolver.md#output-resolvedgraph));
  the project is the template. Stacks of one template are independent.
- `LocalWorkspace` shells out to a locally installed `pulumi` CLI — used here, no Pulumi Cloud account.
  `RemoteWorkspace` is Pulumi Deployments (managed remote execution from git) — not used.
  `InlineProgramArgs` = program as a C# `PulumiFn`; `LocalProgramArgs` = `Pulumi.yaml` on disk (`WorkDir`) — used.
- **YAML vs inline C#:** YAML has no provider-SDK coupling (plugins resolved at runtime) but only `fn::each`
  for logic; inline C# needs every provider's NuGet package but has full logic. Chosen: static YAML templates,
  only config generated per run (0003). Orchestrator references only `Pulumi.Automation`, no cloud SDK.

### Config file behaviour

`SetConfigAsync` / `SetAllConfigAsync` write straight into `Pulumi.<stack>.yaml` on disk, overwriting the key
(same as `pulumi config set`), and persist after the process exits. There is no separate runtime-override
layer — at `UpAsync()` the file already holds what the code wrote. Config is always driven from code; nobody
hand-authors it. YAML runtime gotcha: numeric-looking strings such as `"1.2"` are coerced to numbers by
`${}` substitution (so a version such as `1.2` is better written as a literal in the template).

### Stack naming, outputs and work directories

- `DeploymentPackage.DeploymentContent` is the template (its `name:` is the Pulumi project); `DeploymentPackage.StackName` is the graph's stack.
- Pulumi limits stack names to 100 characters of `[A-Za-z0-9_.-]`. The provider passes names up to 100 unchanged;
  a longer name becomes its first 83 characters + `-` + the first 16 hex characters of the SHA-256 of the full
  name (`StackNames`): deterministic, 100 characters, distinct for different graph stacks. Callers always use the
  graph name, also with `GetOutputsAsync`. Names that Pulumi or the file system cannot take are rejected.
- `GetOutputsAsync(stackName, deploymentContent)` takes the stack name and template content `DeployAsync` was given
  (so the project always matches; the config is not needed), lists the project's stacks and reads the matching stack's
  outputs from state: no refresh, no preview, no provider plugin.
  It returns null when the stack is "not deployed": it does not exist, or it has no resources recorded
  (`PreviewAsync` creates an empty stack, so a preview alone does not make a node deployed). The orchestrator uses
  it to fill references to already-deployed nodes during preview; there are no fake or placeholder values in the
  provider.
- Refresh runs before every deploy and preview but its events are not recorded: `DeploymentResult.Changes` holds only the deploy or preview changes, so a no-op re-deploy has none.
- Every operation runs in `<ScratchDirectory>/just-deliver/<pulumi stack name>`, created for the operation and
  removed afterwards, on success, failure and cancellation. Everything durable is in the backend, and config is
  rewritten from code on every run, so nothing is kept between operations. Two concurrent operations on one stack
  would share the directory, so callers must not run two operations on one stack at once (the orchestrator is sequential).
- Secret config values are written with `--secret` semantics (`ConfigEntry.IsSecret`), come back as secret
  outputs (`IsSecret`), and do not appear in clear text in state.
- `CancellationToken` flows from every public provider method to the Automation API calls.

### Orchestrator

`jd.orchestrator` (application layer: depends on `jd.core` and `jd.resolver`, never on Pulumi) walks a `ResolvedGraph`
in graph order through `IBackEndProvider`, one node at a time. Template content comes from `ITemplateStore`
(implemented by `TemplateLibrary` over `<catalog>/templates`).

- **Deploy.** Per infrastructure-phase node: evaluate the node's pending config again with the same
  `ExpressionEvaluator` and a context whose known outputs are the outputs captured from the nodes deployed before it (the
  workload name and team, the current id and the node names in scope are the node's scope, as in the first pass).
  Every value must be resolved before the node is deployed; otherwise the walk stops and the error names node, field and
  reference. Config becomes backend entries: strings as they are, numbers and booleans in invariant form (`0.15`,
  `true`), objects and arrays as compact JSON (a Pulumi config value is always a string; templates read maps and lists
  as JSON). A value built from a secret output is a secret. The walk stops at the first failure and reports what was deployed.
- **After-runtime nodes** (phase `after-runtime`) are not deployed by this walk: they are reported "waiting for runtime".
- **Preview.** Same order, nothing is created. A reference to a node this run has not deployed is filled from
  `GetOutputsAsync` (state of an earlier deploy); if the node was never deployed the value is missing and the node is
  reported "pending upstream" and not previewed (never a fake value). Template `fn::invoke`s still run during preview, so
  previewing a new environment's substrate needs its upstream deployed first.
- **Result.** Per node: outcome (`deployed`, `unchanged`, `previewed`, `pending upstream`, `waiting for runtime`,
  `failed`), the provider's change summary and per-resource changes, and the time. A node is `unchanged` when its summary
  has no operation other than `Same`; "re-run = zero changes" means every deployed node is `unchanged`.
- **Configuration.** `jd deploy` reads `PULUMI_BACKEND_URL` and `PULUMI_CONFIG_PASSPHRASE` (or `PULUMI_CONFIG_PASSPHRASE_FILE`)
  in the composition root and refuses to run when they are unset (an empty passphrase is allowed: local/dev only); a default
  state location would lose track of stacks between runs. Optional: `PULUMI_HOME`, `JD_SCRATCH_DIR`. The orchestrator
  refuses a graph whose catalog version or environment differs from the catalog and environment it was given.

### Substrate (`jd env up`)

An environment's substrate is provisioned by the same path as a workload's infrastructure: `Resolver.ResolveSubstrate` resolves an
**environment definition** (substrate requirements, matched by catalog mappings like workload requirements; the owner is
named `@<name>`, so its ids and stacks (`_dev.dev.substrate.group`) can never collide with a workload's, and it has no runtime, so
workload-scope policies add nothing) and the orchestrator deploys the graph.
There is no second resolution or deploy path. Because Azure allows one free-tier Cosmos account per subscription there are two
layers: `shared` (once per subscription: resource group, Log Analytics workspace with a daily cap, the Cosmos account with free
tier and `totalThroughputLimit: 1000`) and one definition per environment on top of the shared descriptor (`--base`): resource
group, Container Apps environment (Consumption) on the shared workspace, and a Cosmos SQL database with 400 RU/s shared throughput
in the shared account. Two environments use 800 of the 1,000 free RU/s. These values live in the catalog mappings, not in code.

After the deploy, `DescriptorComposer` evaluates the definition's `values` (each an expression over `${resource.<id>.<export>}`):
an export is evaluated with its requirement's node outputs (`NodeReport.Outputs` holds only non-secret outputs; null ones are dropped by the composer), which makes
`${resource.…}` resolve. The values are merged into the base descriptor's (new keys only), and the result is validated with the
descriptor loader before it is written. The same composition runs once **before** the deploy with the values still pending, so
every error that does not need an output is found while nothing exists yet.

### Chaining stacks

Provision stack A → read `UpResult.Outputs` → `SetConfigAsync` into stack B → provision B. Alternative:
`StackReference` reads another stack's outputs live from inside a program via `<org>/<project>/<stack>`
(`organization` is fixed on the local backend), read-only, no staleness. **Chosen: config-passing by the
orchestrator**, because one orchestrator already sequences everything and holds the values;
`StackReference` pays off only when stacks are provisioned independently. In preview, references to already-deployed
nodes are filled from `GetOutputsAsync`; references to nodes not yet deployed stay pending (never fake values). See [Orchestrator](#orchestrator).

### Backend and state

- Backend set via `PULUMI_BACKEND_URL` in code (`file://~` locally), never in `Pulumi.yaml`, keeping
  templates backend-agnostic. Target: Azure Blob Storage, self-hosted, no Pulumi Cloud
  (`azblob://pulumi-state`, see [phase-0-bootstrap.md](../plans/phase-0-bootstrap.md)).
- The local backend still encrypts secrets, so `PULUMI_CONFIG_PASSPHRASE` is required non-interactively;
  empty is acceptable for local/dev only (D25).
- Local state is incompatible with ephemeral hosting (Container Apps Jobs, CI runners); switch to a remote
  backend before moving execution off a persistent machine.

### Scaling and performance

- Fixed cost per stack operation: CLI process start + provider plugin start; `azure-native` has a very large
  schema and is slow to initialise. No hard limit on stack count, but cost is linear in stacks × workloads.
- Mitigate by parallelising independent workloads, not by merging stacks with independent lifecycles. Also
  (H47): hash config + template per stack and skip unchanged ones, keep a warm plugin cache.
- Azure ARM rate limits are a separate concern at high concurrency.
- Reading outputs / `StackReference` is cheap (state read or one small API call, no plugin start), unlike
  `up`/`refresh`/`preview`.

### Hosting the orchestrator (future; not needed for local MVP)

Ranked: **CI/CD pipeline first** (approval gates + logs for free) → **Container Apps Jobs** once a self-serve
API exists. Avoid Azure Functions (timeout risk with sequential multi-stack runs). All options give a writable
temp dir; on GCP Cloud Run/Functions `/tmp` is memory-backed and counts against container memory. The real
serverless bottleneck is the plugin cache (`PULUMI_HOME`): re-downloading a 100+ MB provider per cold start is
slow, so prefer compute with a warm cache or plugins baked into a custom image (Container Apps Jobs, ACI, CI
runner).

### Current implementation vs this design

| Design | Code today (`src/backend-providers/jd.bp.pulumi`, `catalog/templates`) |
|---|---|
| Templates at `templates/<provider>/<resource-type>/Pulumi.yaml` | `catalog/templates/<provider>/<resource-type>/Pulumi.yaml`; no per-template default config (the orchestrator sets every input) |
| One stack per graph node | `DeploymentPackage.StackName` (shortened above 100 characters); the project is the template's `name:` |
| Fresh temp dir, cleaned up | `<ScratchDirectory>/just-deliver/<stack>`, removed after every operation |
| — | Every `DeployAsync`/`PreviewAsync` runs `RefreshAsync` first; per-property changes captured from the deploy or preview engine events (not the refresh) into `DeploymentResult.Changes` |
