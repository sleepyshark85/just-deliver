# Provisioning

How a resolved graph ([model.md](model.md#9-the-resolved-graph)) becomes Azure resources: the orchestrator's walk, the release flow of the
runtime node, preview, the substrate, and how jd operates Pulumi. Rationale: [ADR 0002](../decisions/0002-pulumi-automation-api.md)
(Automation API), [ADR 0003](../decisions/0003-pulumi-yaml-template-library.md) (static YAML templates, one stack per node),
[ADR 0006](../decisions/0006-infra-deployed-with-app.md) (infrastructure ships with the app).

## Orchestrator

`jd.orchestrator` (application layer: depends on `jd.core` and `jd.resolver`, never on Pulumi) holds the use cases each command calls
([application.md](application.md)) and walks a `ResolvedGraph` through `IBackEndProvider`, one node at a time. Template content comes from
`ITemplateStore` (`TemplateLibrary` over `<catalog>/templates`). It refuses a graph whose catalog version or environment differs from the
catalog and environment it was given.

- **Deploy.** Per infrastructure-phase node, the node's pending config is evaluated again with the same `ExpressionEvaluator`, its known
  outputs being those of the nodes deployed before it (the scope is the node's, as in resolution). Outputs are read by node name only from the
  node's own scope and the runtime; another requirement's values arrive through its exports (`${resource.<id>.<export>}`, read only by the
  runtime node), evaluated by `ExportValues` once that requirement's nodes are deployed. Every value must be resolved before the node deploys;
  otherwise the walk stops and the error names node, field and reference.
- **Config entries.** A Pulumi config value is a string: strings as they are, numbers and booleans in invariant form (`0.15`, `true`),
  objects and arrays as compact JSON, the `fn::entries` variables as a list of `{name, value}` sorted by name. A value built from a secret
  output, directly or through an export, is secret; a secret variable makes the whole `variables` entry secret.
- **Walk order.** The `Infrastructure` nodes first, in graph order; then the runtime node through the release flow; the `AfterRuntime` nodes
  (the grants) inside that flow. Topological order can put the runtime before unrelated infrastructure, so the walk does not rely on it. Each
  node is reported as it completes, the runtime last. The walk stops at the first failure and reports what was deployed.

### Release flow (the runtime node)

The `container-app` template takes the revision suffix and the traffic as inputs and has no opinion on either, so one template deploys every
step. The runtime's `release` block names those inputs, the outputs the flow reads and the shape of a traffic item; its `probe` carries the
retry bound; nothing of them is in code. The engine alone sets the suffix and the traffic: `TemplateLibrary.Check` rejects a runtime node whose
config sets either (a workload-scope policy could), and the engine's values replace any that reach the walk.

1. **Dark:** deploy with the new suffix and all traffic (`fullWeight`) on the live revision, so the new revision exists and serves nothing.
   On a first deploy there is no live revision and the traffic is `[{latestRevision: true, weight: <fullWeight>}]`.
2. **Grants:** the `AfterRuntime` nodes, deployed with the dark deploy's outputs.
3. **Probe:** GET `<probe.scheme>://` + the new revision's FQDN output + `probe.path` through `IRevisionProbe`, retried up to
   `1 + timeoutSeconds / intervalSeconds` times `intervalSeconds` apart, because grants take minutes to propagate. The target is built only from
   the runtime's data and the dark deploy's FQDN output (missing or empty fails the step without a probe).
4. **Shift:** the same suffix with all traffic on the new revision; this changes traffic only. It runs on a first deploy too, so the stack's
   traffic names the revision from then on.

A failed step makes the runtime `failed` with `<step>: <reason>`; later steps do not run and traffic stays on the live revision. **First-deploy
exception:** the dark step already serves the latest revision, so a failed probe is reported but protects no traffic.

- **Live revision:** the last traffic item with a `revisionKey` in the stack's traffic output (the template echoes the traffic it was given);
  none on a first deploy. The app's latest ready revision is the newest one, not the live one.
- **Suffix:** `prefix` + `configDigits` hex digits of the SHA-256 of the template content and the filled runtime parameters (key, value,
  secret flag, in key order; suffix and traffic excluded) + `saltDigits` of a salt; `r` + 8 + 6 for container-app (Azure: lowercase letters,
  digits and `-`, starting with a letter; the declared `maxLength` keeps the revision name within Azure's limit). Azure rejects any change whose
  suffix names an existing revision, so the previous suffix (the suffix output) is reused when it has the same prefix and config part (a re-run
  is `unchanged`; a re-run after a failed probe probes and shifts the same revision); otherwise the salt is the SHA-256 of the previous suffix,
  so A → B → A deploys A as a new revision. Residual: a salt collision on an earlier config is rejected by Azure at the dark step, and a dark
  deploy that fails after Azure created the revision keeps being rejected until the config changes; traffic is unaffected either way.
- **Unchanged:** the dark deploy has no changes and the new revision is the live one: Probe and Shift are `skipped`, the grants still deploy.
- **Known limitation:** old revisions are not deactivated. A 0%-traffic revision stays active, scaled to zero at no cost; deactivation is an
  API action Pulumi YAML cannot express, and `maxInactiveRevisions` bounds only inactive ones (open question E32).

### Preview

Same order, nothing created. A reference to a node this run has not deployed is filled from `GetOutputsAsync` (the state of an earlier
deploy); a node never deployed leaves the value missing and its dependents `pending upstream`, never a fake value. Template `fn::invoke`s still
run, so previewing a new environment's substrate needs its upstream deployed. For the runtime only the dark package is previewed (live revision
from state) and its steps are `planned`; the probe is never called. The grants are previewed with the runtime's outputs from state, which
lets `jd release qualify` see a drifted grant ([release-record.md](release-record.md)).

### Result

Per node: outcome (`deployed`, `unchanged`, `previewed`, `pending upstream`, `failed`), the provider's change summary and per-resource
changes, and the time. A node is `unchanged` when its summary has no operation other than `Same`, so "re-run = zero changes" means every node
`unchanged`. The runtime's report also carries its steps (`dark`, `grants`, `probe`, `shift`, each `done`, `planned`, `skipped` or `failed`).

## Release sets (`jd release deploy`)

A release set ([release.md](release.md)) deploys by the same path as one workload, once per workload. `ReleaseResolver` parses each
embedded definition and resolves it in the set's order, naming errors `<set>#<workload>`; the use case checks every graph against the
templates; then `Orchestrator.DeployReleaseAsync` walks the graphs in that order and stops after the first workload that fails (a failed
probe included), so later workloads are never touched. All resolution and checks come before the first node. Rollback of the workloads
before a failure is not part of the walk (open question E31): the run reports where it stopped and a re-run resumes.

## Substrate (`jd env up`)

An environment's substrate deploys by the same path as a workload's infrastructure: `Resolver.ResolveSubstrate` resolves the environment
definition ([model.md](model.md#5-environments)) and the orchestrator deploys the graph; there is no second path. Azure allows one free-tier
Cosmos account per subscription, hence two layers: `shared` once per subscription (resource group, Log Analytics workspace with a daily cap,
the free-tier Cosmos account with a throughput cap and local authentication disabled), and one definition per environment on top of the
shared descriptor (`--base`): resource group, Container Apps environment (Consumption) whose logs go to the shared workspace, and a Cosmos
SQL database with 400 RU/s shared throughput, so two environments use 800 of the 1,000 free RU/s. These values are catalog data. The
workloads' containers share their environment database's throughput.

`DescriptorComposer` then writes `values.<part>.<export>`: each part's export evaluated with the deployed outputs (`NodeReport.Outputs` holds
non-secret outputs only, so an export reading a secret fails, [model.md](model.md#5-environments)), merged into the base's values (new keys only), and
written by the binder through the descriptor schema. The same composition runs once **before** the deploy with the values pending, so every
error that needs no output is found while nothing exists yet.

## Pulumi operation

- **Workspaces.** `LocalWorkspace` with `LocalProgramArgs`: the template's `Pulumi.yaml` on disk is the program and its `name:` the
  project; the locally installed `pulumi` CLI runs it; no Pulumi Cloud, no `RemoteWorkspace`. The orchestrator references no cloud SDK, and
  only config is generated per run (ADR 0003). One stack per graph node; stacks of one template are independent.
- **Config.** `SetAllConfigAsync` writes into `Pulumi.<stack>.yaml`, overwriting keys; config is always written from code. Secret entries
  (`ConfigEntry.IsSecret`) are written as secrets, come back as secret outputs and are encrypted in state. Pulumi YAML coerces
  numeric-looking strings substituted by `${}` to numbers, so a value such as a version `1.2` is better a literal in the template.
- **Stack names.** `DeploymentPackage` carries the template (`DeploymentContent`) and the graph's stack name (`StackName`). Pulumi allows 100
  characters of `[A-Za-z0-9_.-]`; a longer name becomes its first 83 characters + `-` + 16 hex characters of its SHA-256 (`StackNames`):
  deterministic and distinct. Callers always use the graph's name.
- **Outputs.** `GetOutputsAsync(stackName, deploymentContent)` reads a stack's outputs from state (no refresh, no preview, no plugin); it
  returns null when the stack does not exist or records no resources (a preview alone creates an empty stack).
- **Refresh.** Every deploy and preview refreshes first; only the deploy or preview events are recorded in `DeploymentResult.Changes`, so a
  no-op re-deploy has none.
- **Work directories.** Each operation runs in `<ScratchDirectory>/just-deliver/<stack>`, created for it and removed afterwards, on success,
  failure and cancellation; everything durable is in the backend. Two operations on one stack at once would share it; the orchestrator is
  sequential. `CancellationToken` flows from every provider method to the Automation API.
- **Chaining stacks.** The orchestrator passes outputs into the next stack's config; it already sequences every node and holds the values, so
  `StackReference` (reading another stack from inside a program) is not used.

## Backend and state

Nothing is set on the machine but a signed-in identity: jd decides where it connects.

- **Data directory** `<data>`: `jd` in the local application data folder (`~/.local/share/jd` on Linux), created on first use, or
  `JD_DATA_DIR` (tests and separate runs use it); never required.
- **State:** the composition root hands the backend provider `file://<data>/state`, set in code and never in `Pulumi.yaml`, so templates stay
  backend-agnostic. The local backend still encrypts secrets: `<data>/state.key` holds a random passphrase, generated once with mode 600 and
  never replaced, passed to Pulumi as `PULUMI_CONFIG_PASSPHRASE_FILE`; an ambient `PULUMI_CONFIG_PASSPHRASE` is removed, since Pulumi would
  prefer it.
- **Records:** `<data>/records`, through `IRecordStore` ([release-record.md](release-record.md)).
- **Region:** the environment definition's `region`, copied to the descriptor; not a machine setting.
- **Identity:** the `ARM_*` service principal when all its variables are set (CI, the Azure gate), else the Azure CLI sign-in; a partial
  `ARM_*` set is an error naming the missing ones. Pulumi's Azure provider authenticates the same way.
- **Optional:** `PULUMI_HOME` (plugin cache) and `JD_SCRATCH_DIR` (work directories). The user-facing list: [usage.md](../usage.md).

Each concern has one implementation, local; an Azure-backed state or record store plugs in behind the same abstractions. Local state does not
suit ephemeral hosting (Container Apps Jobs, CI runners): a remote backend comes first.

## Scaling and performance

- Each stack operation pays a CLI process start and a provider plugin start; `azure-native` is slow to initialise. Cost is linear in stacks ×
  workloads. Reading outputs is cheap (a state read, no plugin).
- Mitigate by running independent workloads in parallel, not by merging stacks with independent lifecycles; skip stacks whose node hash is
  unchanged and keep a warm plugin cache (H47). ARM rate limits matter at high concurrency.

## Hosting the orchestrator

Today jd runs on the deployer's machine or a CI runner. Ranked options for later: a **CI/CD pipeline** (approval gates and logs for free),
then **Container Apps Jobs** once a self-serve API exists; not Azure Functions (timeouts on sequential multi-stack runs). The bottleneck of
serverless hosting is the plugin cache: re-downloading a 100+ MB provider per cold start is slow, so prefer a warm cache or plugins baked
into the image.
