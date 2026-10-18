# Using the CLI

`jd` (project `src/jd.cli`) is the composition root: it parses arguments, wires the validator and the
resolver ([resolver.md](architecture/resolver.md)), prints results and sets the exit code. It holds no
resolution logic. Every input is an argument; only the deploy backend settings come from environment variables.

```
dotnet run --project src/jd.cli -- validate <workload.yaml>
dotnet run --project src/jd.cli -- preview <workload.yaml> --env <environment.yaml> --catalog <dir> [--json]
dotnet run --project src/jd.cli -- release create <manifest.yaml> --out <release-set.yaml>
dotnet run --project src/jd.cli -- release show <release-set.yaml>
dotnet run --project src/jd.cli -- release deploy <release-set.yaml> --env <environment.yaml> --catalog <dir> [--preview]
dotnet run --project src/jd.cli -- release qualify <release-set.yaml> --env <environment.yaml> --catalog <dir>
dotnet run --project src/jd.cli -- approve <label> --env <environment.yaml> --meaning <text>
dotnet run --project src/jd.cli -- promote <label> --to <environment.yaml> --catalog <dir>
dotnet run --project src/jd.cli -- deploy <workload.yaml> --env <environment.yaml> --catalog <dir> [--preview]
dotnet run --project src/jd.cli -- env up <definition.yaml> --catalog <dir> --out <descriptor.yaml> [--base <descriptor.yaml>] [--force]
dotnet run --project src/jd.cli -- --help
```

| Command | What it does |
|---|---|
| `validate` | Checks the workload against `workload.schema.json` (embedded in `jd.definitionvalidator`) and the workload rules. Prints one error per line as `file: location: message`. |
| `preview` | Validates the workload first, then loads the catalog and environment descriptor, expands requirements, applies policies and builds the graph. Prints each node in graph order with id, template, kind, phase, stack, short hash, dependencies and every config field with its value (`pending: <expression>` when it waits for a runtime or dependency output; `(from <part>.<export>)` when read from the environment descriptor, such as `(from cosmos-account.id)`) and provenance (layer and rule). `--json` prints the graph as the stable JSON the golden snapshot pins instead. |

### `jd deploy`

The unrecorded development path for one workload: it has no release label, so it leaves no release record. What is deployed and promoted is a
release set (`jd release deploy`); the evidence of a deploy comes from there ([release-record.md](architecture/release-record.md)).

Resolves like `preview`, checks the graph against the templates in `<catalog>/templates`, then provisions the
infrastructure nodes through the Pulumi backend in graph order, then releases the runtime ([Orchestrator](architecture/provisioning.md#orchestrator)).
This **creates real Azure resources**: use a subscription with the guardrails of [tools/sandbox](../tools/sandbox/README.md)
signed in with `az login` (or a service principal's `ARM_*`, see [Data and identity](#data-and-identity)).

- One line per node as it completes: `<node id>: <outcome> (<changes>, <seconds>s)`, then the resources changed.
  Outcomes: `deployed`, `unchanged` (the stack already matched), `previewed`, `pending upstream` (`--preview` only: a
  value from a node that is not deployed yet is missing), `failed`. The runtime node comes last and is released in steps, one
  line each under it: `dark` (the new revision, with no traffic when a live revision exists), `grants` (the nodes that depend on
  the runtime are deployed here), `probe` (`GET https://<revision fqdn><path>` must answer the expected status, retried up to the
  runtime's timeout) and `shift` (all traffic to the new revision, also on a first deploy). A revision that fails its probe
  never gets traffic when a live one exists; fix the cause and run again. The first deploy of a workload has no live revision,
  so its revision serves before it is probed: a failed probe is reported but cannot protect traffic. A re-run with unchanged
  config reports the runtime `unchanged`, with probe and shift `skipped`.
- The first failure stops the run (exit 1) and names the node and reason; nodes already deployed stay deployed. Running
  the command again resumes: deployed nodes report `unchanged`.
- `--preview` shows what would change and creates nothing; nodes whose upstream outputs are not in state yet are `pending upstream`.
- Nothing needs to be set: the Pulumi state is in jd's data directory ([Data and identity](#data-and-identity)).

Errors from any stage are printed to stderr with file and location; results go to stdout.

#### Monitoring

Every workload requires the `monitoring` component (Application Insights reporting to the environment's workspace) and maps its
connection string to its own variable; the `enforce-monitoring` policy requires the component, never the variable, and adds nothing:

```yaml
container:
  variables:
    APPLICATIONINSIGHTS_CONNECTION_STRING: ${resource.monitoring.connectionString}
requires:
  - type: monitoring
```

A workload without it fails to resolve with `requires: policy 'enforce-monitoring' (policies/enforce-monitoring.yaml) requires a
'monitoring' requirement (…)`. Without the variable mapping the component is deployed but the app gets no connection string.

**Upgrading a workload deployed before S21c**, when App Insights came from the policy: the next deploy creates `monitoring/appinsights` and
`monitoring/access` and rolls a new revision (probe, then shift). jd deletes nothing, so the old App Insights and its role assignment stay in
the stacks `<workload>.<env>._workload.appinsights` and `<workload>.<env>._workload.appinsights-access`, with the telemetry history. They
cost nothing and go with the environment's resource group at teardown. To remove them earlier, with `PULUMI_BACKEND_URL=file://<data>/state`,
`PULUMI_CONFIG_PASSPHRASE_FILE=<data>/state.key` and `PULUMI_CONFIG_PASSPHRASE` unset (Pulumi prefers it to the file), from the template's directory (`catalog/templates/azure/role-assignment`, then `catalog/templates/azure/application-insights`):
`pulumi destroy --stack <stack> --yes`, then `pulumi stack rm <stack> --yes`.

### `jd env up`

Provisions an environment's **substrate** from an environment definition and writes the environment descriptor
([resolver.md](architecture/resolver.md#environment-descriptor)) from the real outputs, so no Azure id is typed by hand.
It uses the same resolver and orchestrator as `jd deploy`, the same state in jd's data directory and the same sign-in. **It creates real Azure resources**: free tier only,
see [standards §6](engineering/standards.md).

```
jd env up samples/environments/shared.yaml --catalog catalog --out shared.env.yaml
jd env up samples/environments/dev.yaml    --catalog catalog --base shared.env.yaml --out dev.env.yaml
```

- Two layers, because Azure allows one free-tier Cosmos account per subscription: `shared` (once per subscription: resource
  group, capped Log Analytics workspace, the Cosmos account) and one definition per environment on top (`dev`, later `stage`:
  resource group, Container Apps environment whose diagnostic setting sends its logs to the shared workspace, a 400 RU/s database in the shared account).
  What a layer deploys follows from its `offers`: the shared resources (`catalog/shared/`) and components' `environment` parts the
  offered components and runtime reach through `uses`. Everything about them (SKUs, caps, throughput, names, tags) is catalog data:
  `catalog/shared/*.yaml`, `catalog/components/database.yaml`, `catalog/naming.yaml`.
- `--base` is the descriptor of the layer below. Its values are keyed by part (`cosmos-account.name` and so on); a part of the run reads
  another as `${cosmos-account.name}`, in place when it is deployed in the same run, else from the base. The written descriptor is the
  base's values plus each deployed part's exports. A shared resource the offers reach must be in the base (an environment never deploys
  one), and a part the base already has is an error.
- The definition's `region` (required) is the descriptor's region, the same on every machine; the descriptor takes its name and type from
  the definition too.
- `--out` must not exist unless `--force` is given; this is checked before anything is created.
- Before anything is created the definition's type (and the base's) must be one `catalog.yaml` declares under `environmentTypes`, and a
  definition of a type with `approval: true` must carry an `approval` block; otherwise the error names the type (and the declared ones) and
  nothing is deployed. `jd preview`, `deploy`, `release deploy`, `release qualify` and `promote` check a descriptor the same way.
- Then the definition is resolved and the descriptor is composed with its values still pending: an
  unknown offer, two runtimes, a component without an implementation for the type, a collision with the base, or a descriptor rule broken (reserved key, a dotted key) is reported and nothing is deployed. After the deploy the descriptor is composed from the outputs,
  validated by the descriptor loader and only then written. Secret or null outputs are never written; a value that needs
  one is an error.
- Running a command again reports every node `unchanged` and rewrites the same descriptor (with `--force`).
- A descriptor written before S21f is refused (before S21e its values were keyed by layout, not by part; S21e ones carry `grantable`): run the
  `env up`s again with `--force`; every node is `unchanged` and the descriptors are rewritten in the new layout.
- If a deploy fails nothing is written; running again resumes (deployed nodes are `unchanged`).

An environment definition (`kind: EnvironmentDefinition`, [schema](../schemas/environment-definition.schema.json)):

```yaml
kind: EnvironmentDefinition
name: dev                       # the environment's name
region: southeastasia          # where it is deployed (required): the descriptor's region, the same on every machine
type: dev                       # an environment type catalog.yaml declares (shared, dev, stage, prod in the seed)
offers: [container-app, database, monitoring]   # catalog components and at most one runtime; the parts follow from them
approval:                       # optional, required for a type with approval: true (stage, prod); copied as it is into the descriptor
  qualifiedOn: dev              # the environment whose records must show the set qualified
  required: 1                   # distinct approvers needed (1 to the number listed)
  approvers: ["00000000-0000-0000-0000-000000000000"]   # Entra object ids (lower case) that may approve
  separateFromDeployer: true    # an approver may not be a principal that deployed the label on qualifiedOn; required, no default
```

`samples/environments/stage.yaml` is `dev`'s layout plus such a block, type `stage`: bring it up like `dev`
(`jd env up samples/environments/stage.yaml --catalog catalog --base shared.env.yaml --out stage.env.yaml`). It gets its own resource group,
Container Apps environment and 400 RU/s database (`db-stage`) on the shared Cosmos account and workspace, so `dev` and `stage` use 800 of the free
1,000 RU/s; a third environment would not fit. As shipped the approver is the all-zero id, which matches no principal: replace it with the object ids of
the service principals that may approve before anything can be promoted.

#### Gated environments

An environment whose **descriptor carries an `approval` block** is gated, whatever its type (the tool tests for the block, never for a type name). The
catalog requires the block for a type declared with `approval: true` (`stage`, `prod`), so one cannot be written without it: such a definition or
descriptor without the block fails with "environment type 'stage' requires an 'approval' block". `jd deploy`, `jd release deploy` and `jd release qualify`
on a gated descriptor exit 1 with "environment '<name>' is gated by an approval policy: use `jd promote`" before the principal, the records or the
backend is touched: no call, no record, no `set.yaml`. `--preview` is not refused (it creates nothing), and neither is `jd env up` (the substrate is not a
release). `jd approve` records an approval for a gated environment (below); `jd promote`, the only way a set reaches one, is below ([approvals.md](architecture/approvals.md)).

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | Validation or resolution errors (workload, catalog, environment or graph), a failed deployment, a deploy to a gated environment, a credential Entra refuses or that is malformed (for example `ARM_TENANT_ID` neither a directory id nor a domain name), or a release label the record store rejects (a path separator or `..`) |
| 2 | Usage error: unknown command or option, missing argument, unreadable file or directory |

## Releases

A release is a pinned **set** of workloads under one label, deployed in dependency order
([release.md](architecture/release.md), C53). You author a manifest; the platform generates the set.

```yaml
# samples/releases/sample.manifest.yaml
kind: ReleaseManifest
label: 1.0.0
workloads:
  - definition: ../workloads/sample-app.yaml      # relative to the manifest
    dependsOn: [just-deliver-sample-worker]       # workload names; ordering only
  - definition: ../workloads/sample-worker.yaml
```

| Command | What it does |
|---|---|
| `release create <manifest> --out <set>` | Validates every listed workload definition, requires each image to be pinned by digest (`@sha256:` and 64 hex digits; a tag is rejected), checks names and dependencies, works out the deploy order and writes the release set. Refuses to overwrite an existing file: sets are immutable, so a changed release gets a new file. Prints every problem as `file: location: message`. |
| `release show <set>` | Re-verifies each embedded definition against its recorded SHA-256 and re-applies the create rules, then prints (derived from the definitions) the label, the deploy order and, per workload, team, definition SHA-256, image and dependencies. A modified set, or one that breaks a rule, is an error (exit 1). |

| `release deploy <set> --env <environment> --catalog <dir> [--preview]` | Loads the set (re-verifying it like `show`), resolves **every** workload on the environment and checks every graph against the templates, and only then deploys each workload (infrastructure, then the runtime release) in the set's deploy order. Same resolver, template check and orchestrator as `jd deploy`, and the same state and sign-in ([Data and identity](#data-and-identity)), plus the records (below) unless `--preview`. **Creates real Azure resources.** |
| `release qualify <set> --env <environment> --catalog <dir>` | Re-checks a deployed set without deploying anything (installation qualification, IQ): resolves like `release deploy`, then checks that no node of any workload has drifted and that every revision serving traffic runs the pinned image. Same variables as a recorded `release deploy`. Writes a record (below). |

Exit codes are the same as above (a manifest, set or `--out` path problem is 2; invalid content is 1).

### `jd release deploy`

- Any error before the first node stops the run and nothing is created (exit 1). A workload that does not resolve is
  reported with the set and the workload (`<set.yaml>#<workload>: location: message`), every workload's errors in one pass;
  a graph the templates do not fit names the node (`<workload>/<env>/<scope>/<name>`) or the template file.
- Output per workload: a `workload <name>` line, then the `jd deploy` node lines (`deployed`, `unchanged`; the runtime last, with its
  release steps). The last line summarises:
  `release <label> on <environment>: <n> deployed, <n> unchanged, <n> failed, <n> not started (<names>).`
  A workload counts as deployed when any of its nodes changed, otherwise unchanged.
- The first node that fails stops the whole run (exit 1): the failure names the workload and node, workloads after it are
  not started (listed in the summary), and what was already deployed stays. Running the command again resumes: deployed
  nodes report `unchanged`. Rolling earlier workloads back is not done (open question E31).
- `--preview` walks the same order and creates nothing; the summary says `previewed`.
- **The release record** (not with `--preview`): one JSON file per attempt, written at the end of the run (also when it failed), never edited:
  `<data>/records/<environment>/<label>/<UTC yyyyMMddTHHmmssfffZ>-deploy.json`; the line `record: <path>` names it. It holds the outcome
  (and the failing workload and node, and the workloads not started), who deployed, what was pinned (hashes, not config), what ran with change
  **counts**, timings, and the operational qualification (OQ) of each workload read from its probe: `passed`, `failed`, `unchanged` (the revision
  was already live) or `notRun`. Contract: `schemas/release-record.schema.json`; design: [release-record.md](architecture/release-record.md).
  A deploy that succeeded then runs **IQ** (the same checks as `jd release qualify`, below) and the record carries it as `iq`: `passed`, `failed`, or `notRun`
  when a workload failed (a failed deploy runs no IQ). `deploySeconds` is the deploy walk alone; the IQ time is `iq.elapsedSeconds`, so the whole run (roughly
  one more pass over every node for IQ) takes longer than `deploySeconds`. **If IQ fails, the command exits 1** and prints each failure as
  `jd: IQ failed: <workload>: <reason>`; the record reads `outcome: succeeded` with `iq.status: failed` and the deployment is left as it is (no rollback). It reads
  the live revisions as the signed-in principal.
  - The records are in jd's data directory (`<data>/records`, [Data and identity](#data-and-identity)); keep it somewhere durable: it is the
    evidence, and nothing in it is a secret.
  - **Who deployed** is the signed-in principal ([Data and identity](#data-and-identity)) as Entra reports it for an access token obtained in
    this run. A partial `ARM_*` set or a refused credential stops the run (exit 1) before anything is deployed; the secret is never printed.
  - `<records>/<environment>/<label>/set.yaml` is the set file's exact bytes, kept on the label's first attempt. A later attempt under the same
    label with a different set is refused (exit 1) before anything is deployed: use a new label. It is never overwritten.
  - If the record itself cannot be written, the run exits 1 and says the release was deployed; the deployment stays.
  - Also exit 1, before anything is deployed and with nothing created: a credential Entra refuses or that is malformed (for example `ARM_TENANT_ID`
    neither a directory id nor a domain name), and a label the record store rejects (it must not contain a path separator or `..`, since it names a directory).

### `jd release qualify`

Checks, never changes: it makes no deploy call. It runs every check and lists every failure; none stops the others.

- **No drift:** every node of every workload (the grants included) is previewed against what is deployed; a node that would change, cannot be previewed
  (waits on something not deployed) or fails is a failed check. The substrate (`jd env up`) is not part of a release and is not checked.
- **The running image:** the runtime's revisions are read live from Azure (not from state or the record); every image of every revision with traffic must equal
  the workload's pinned image, and at least one revision must hold traffic. A failure reads
  `pinned <image>, live <image> on revision <name>`. If Azure cannot answer (the app is gone, access refused, no network) that is a failed check with the reason.
- Starts like a recorded `release deploy` (the records, the signed-in principal and the state; creates `set.yaml` when the label has none,
  refuses a different set under the label), then writes `...-qualify.json` and prints `record: <path>`. The record holds the pinned release and graphs, who ran
  it and the `iq` verdict (`passed` or `failed`, its time and each check); it has no run, no OQ and no deploy time.
- Exit 0 when IQ passes; exit 1 when it fails (each failure is a `jd: IQ failed: <workload>: <reason>` line) or when the record cannot be written.
  The deployment is left as it is.

### `jd approve`

`jd approve <label> --env <stage.env.yaml> --meaning <text>` records that the signed-in principal approves the set that is kept for `<label>`
(`<data>/records/<qualifiedOn>/<label>/set.yaml`) for a gated environment. `--env` is the target's descriptor, which carries the `approval` policy
(`qualifiedOn`, `required`, `approvers`, `separateFromDeployer`). The approver is a **service principal**, so `approvers` lists principals, and
separation from the deployer means something only when each approver and each deployer has its own. Needs the records and the sign-in (no catalog, no
backend). `--meaning` is required: 1 to 500 characters, no control characters; it is recorded and printed, never interpreted.

Exit 1 with a message naming the cause, and nothing written, when: the descriptor has no `approval` block; the label is unknown on `qualifiedOn`
(or its `set.yaml` or directory cannot be read, which is said as such); the set is not qualified on `qualifiedOn` (every reason is listed); the principal
is not in `approvers`; `separateFromDeployer` is on and the principal deployed the label on `qualifiedOn`; or the principal already approved this set for this
environment. A bad credential stops before any record is read (exit 1). Exit 2: a missing or empty `--meaning`, a label that is not a release label (or is a path), an unreadable `--env`.

It writes `<data>/records/<target>/<label>/<UTC time>-approve.json` (`kind: ApprovalRecord`, [schema](../schemas/approval-record.schema.json); created once,
never edited) and prints `approved <label> for <env> (<n> of <required>)` and `record: <path>`. The record names the target, the label, the SHA-256 of the
kept `set.yaml` (the approval binds the set, not a record), `qualifiedOn`, the principal as the credential reports it and the meaning. A repeat is refused as a
convenience, not a lock; `jd promote` counts distinct principals. Approvals are not revoked: a withdrawn release gets a new label.

### `jd promote`

`jd promote <label> --to <stage.env.yaml> --catalog <dir>` deploys a set to a gated environment. It takes **no set file**: the set is the bytes kept on the
policy's `qualifiedOn` environment, `<data>/records/<qualifiedOn>/<label>/set.yaml`, which become the target's own `set.yaml` unchanged, so both
environments run the same digests and the same set hash. `--to` is the target's descriptor (it carries the `approval` policy). Needs the records, the
signed-in principal and the state, as `release deploy` does.

The set is promoted only when it is **qualified** on `qualifiedOn` (the newest deploy succeeded and the newest IQ passed, judged from the records now) and
**approved** by enough distinct principals (`jd approve`, counted by the environment's policy). Whoever runs `promote` is not checked against `approvers`.
Otherwise exit 1 lists every reason (and every approval file that was ignored and why) and nothing is written, no backend is called. Exit 1 also when the descriptor has no
`approval` block. Exit 2: a missing or bad argument, a label that is not a release label, an unreadable `--to` or `--catalog`.

When it may go ahead it prints `promoting <label> from <source> (set sha256 <h>)`, then runs the walk of `jd release deploy` on the target with its output, exit
codes, deploy record (with IQ) and `set.yaml` check. The record's `release.setSha256` is `<h>`. `jd release deploy`, `jd deploy` and `jd release qualify`
stay refused on the gated environment.

## Data and identity

Nothing needs to be set on the machine ([connections.md](architecture/connections.md)).

- **jd's data directory** `<data>` is `jd` in the local application data folder (`~/.local/share/jd` on Linux), created on first use;
  `JD_DATA_DIR` moves it (to keep runs apart). It holds:
  - `state/`: the Pulumi state (a `file://` backend);
  - `state.key`: the passphrase that encrypts the state's secrets, random, generated on first use (mode 600) and kept; without it the state's
    secrets cannot be read, so keep it with `state/`;
  - `records/`: the release and approval records, `<environment>/<label>/…` (the evidence).
- **Region:** each environment definition's `region` (required).
- **Identity:** the Azure CLI sign-in (`az login`, and `az account set` for the subscription), for jd and for Pulumi. A service principal
  (CI, the Azure tests) sets all three of `ARM_TENANT_ID`, `ARM_CLIENT_ID` and `ARM_CLIENT_SECRET` (and `ARM_SUBSCRIPTION_ID` for Pulumi) instead;
  a partial set is refused (exit 1) and names the missing ones.
- Optional: `PULUMI_HOME` (plugin cache), `JD_SCRATCH_DIR` (working directories; default the system temp directory).

**Upgrading from before S23**, when the state and records were where `PULUMI_BACKEND_URL` and `JD_RECORDS_DIR` pointed and the region came from
`JD_REGION` or `--region`: jd reads none of them any more, so the old state and records are not found. Add `region:` to your definitions. Then either
start clean (tear down, then run from an empty data directory), or move them by hand: the old state folder to `<data>/state`, the old records
folder to `<data>/records`, and the old passphrase into `<data>/state.key` (no trailing newline) before jd generates one.

## Walkthrough

From a clean subscription to a release promoted to `stage`, then teardown. Commands only; each is described above. `jd` stands for
`dotnet run --project src/jd.cli --`. Free tier only: the subscription must have no free-tier Cosmos account yet (Azure allows one).
To test every check by hand (bad release, drift, refused approvals, tampering), follow [walkthrough.md](walkthrough.md) instead.

**1. Prerequisites.** The .NET SDK, the `pulumi` CLI and the `az` CLI. A deploying principal and, because `stage` keeps approval separate
from deploying (`separateFromDeployer: true`), a second one that approves. Nothing is set ([Data and identity](#data-and-identity)): sign in as
the deployer with `az login` and `az account set -s <subscription id>`, and set each definition's `region` (`southeastasia` in the samples) to the
region the subscription allows.

Before `stage` is brought up, put the approver's object id (`az ad sp show --id <approver client id> --query id -o tsv`) in `approvers` of
`samples/environments/stage.yaml`.

**2. Environments** (as the deployer):

```
jd env up samples/environments/shared.yaml --catalog catalog --out shared.env.yaml
jd env up samples/environments/dev.yaml    --catalog catalog --base shared.env.yaml --out dev.env.yaml
jd env up samples/environments/stage.yaml  --catalog catalog --base shared.env.yaml --out stage.env.yaml
```

**3. One workload on `dev`** (the unrecorded path):

```
jd preview samples/workloads/sample-app.yaml --env dev.env.yaml --catalog catalog
jd deploy  samples/workloads/sample-app.yaml --env dev.env.yaml --catalog catalog
```

**4. A two-workload release on `dev`** (`sample-app` depends on `sample-worker`):

```
jd release create  samples/releases/sample.manifest.yaml --out release.yaml
jd release show    release.yaml
jd release deploy  release.yaml --env dev.env.yaml --catalog catalog
jd release qualify release.yaml --env dev.env.yaml --catalog catalog
```

**5. Approve and promote to `stage`.** `jd approve` signed in as the approver (`az login` as the second principal), then `jd promote` signed in as the deployer again (whoever runs it is not checked against `approvers`):

```
jd approve 1.0.0 --env stage.env.yaml --meaning "Reviewed the dev evidence for 1.0.0."     # az login as the approver first
jd promote 1.0.0 --to stage.env.yaml --catalog catalog
```

**6. Read the records.** Every attempt is one JSON file, never edited (`schemas/release-record.schema.json`, `schemas/approval-record.schema.json`):

```
ls ~/.local/share/jd/records/dev/1.0.0     # set.yaml, …-deploy.json, …-qualify.json
ls ~/.local/share/jd/records/stage/1.0.0   # set.yaml (dev's bytes), …-approve.json, …-deploy.json
```

`outcome`, `oq` per workload, `iq.status`, `deploySeconds` and `identity` are the fields to read first ([release-record.md](architecture/release-record.md)).

### Teardown

`jd` has no destroy command. Delete the resource groups with `az`, in the order the Azure test uses: the environment groups first (their
Container Apps environments log to the shared workspace), then the shared group. The names are in the descriptors: `resourceGroup` in
`dev.env.yaml` and `stage.env.yaml`, `shared.resourceGroup` in `shared.env.yaml`.

```
az group delete --name <dev resourceGroup>   --yes --no-wait
az group delete --name <stage resourceGroup> --yes --no-wait
az group wait   --name <dev resourceGroup>   --deleted
az group wait   --name <stage resourceGroup> --deleted
az group delete --name <shared resourceGroup> --yes
az group list --query "[?tags.project=='just-deliver-mvp'].name" -o tsv    # prints nothing when all are gone
```

Then remove the Pulumi state, `<data>/state` and `<data>/state.key` (its stacks now describe deleted resources), and the descriptors. Keep
`<data>/records` if the evidence is wanted; nothing in it is a secret.
