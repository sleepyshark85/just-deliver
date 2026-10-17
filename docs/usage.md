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
dotnet run --project src/jd.cli -- env up <definition.yaml> --catalog <dir> --out <descriptor.yaml> [--base <descriptor.yaml>] [--region <region>] [--force]
dotnet run --project src/jd.cli -- --help
```

| Command | What it does |
|---|---|
| `validate` | Checks the workload against `workload.schema.json` (embedded in `jd.definitionvalidator`) and the workload rules. Prints one error per line as `file: location: message`. |
| `preview` | Validates the workload first, then loads the catalog and environment descriptor, expands requirements, applies policies and builds the graph. Prints each node in graph order with id, template, kind, phase, stack, short hash, dependencies and every config field with its value (`pending: <expression>` when it waits for a runtime or dependency output; `(from env.<path>)` when read from the environment) and provenance (layer and rule). `--json` prints the graph as the stable JSON the golden snapshot pins instead. |

### `jd deploy`

The unrecorded development path for one workload: it has no release label, so it leaves no release record. What is deployed and promoted is a
release set (`jd release deploy`); the evidence of a deploy comes from there ([release-record.md](architecture/release-record.md)).

Resolves like `preview`, checks the graph against the templates in `<catalog>/templates`, then provisions the
infrastructure nodes through the Pulumi backend in graph order, then releases the runtime ([Orchestrator](architecture/provisioning.md#orchestrator)).
This **creates real Azure resources**: use a subscription with the guardrails of [tools/sandbox](../tools/sandbox/README.md)
and the Azure credentials in the environment (`source ~/.just-deliver/<subscription>.env`).

- One line per node as it completes: `<node id>: <outcome> (<changes>, <seconds>s)`, then the resources changed.
  Outcomes: `deployed`, `unchanged` (the stack already matched), `previewed`, `pending upstream` (`--preview` only: a
  value from a node that is not deployed yet is missing), `failed`. The runtime node comes last and is released in steps, one
  line each under it: `dark` (the new revision, with no traffic when a live revision exists), `grants` (the nodes that depend on
  the runtime are deployed here), `probe` (`GET https://<revision fqdn><path>` must answer the expected status, retried up to the
  mapping's timeout) and `shift` (all traffic to the new revision, also on a first deploy). A revision that fails its probe
  never gets traffic when a live one exists; fix the cause and run again. The first deploy of a workload has no live revision,
  so its revision serves before it is probed: a failed probe is reported but cannot protect traffic. A re-run with unchanged
  config reports the runtime `unchanged`, with probe and shift `skipped`.
- The first failure stops the run (exit 1) and names the node and reason; nodes already deployed stay deployed. Running
  the command again resumes: deployed nodes report `unchanged`.
- `--preview` shows what would change and creates nothing; nodes whose upstream outputs are not in state yet are `pending upstream`.
- Environment variables. Two are **required**; if either is missing `jd deploy` exits 2 and says what to set:
  - `PULUMI_BACKEND_URL`: where Pulumi keeps state, for example `file:///home/me/jd-state` or an Azure blob URL. There is no
    default, because a silent default location loses track of the stacks between runs. The sandbox credentials file does
    not set it: export it yourself, and use the same value every time.
  - `PULUMI_CONFIG_PASSPHRASE`: encrypts secrets in state. Set it explicitly; an empty value is accepted (the sandbox
    credentials file sets it empty; acceptable for local/dev only). `PULUMI_CONFIG_PASSPHRASE_FILE` instead of the
    variable also satisfies the check.
  - Optional: `PULUMI_HOME` (plugin cache), `JD_SCRATCH_DIR` (working directories; default the system temp directory).

Errors from any stage are printed to stderr with file and location; results go to stdout.

### `jd env up`

Provisions an environment's **substrate** from an environment definition and writes the environment descriptor
([resolver.md](architecture/resolver.md#environment-descriptor)) from the real outputs, so no Azure id is typed by hand.
It uses the same resolver and orchestrator as `jd deploy`, and the same backend variables
(`PULUMI_BACKEND_URL`, `PULUMI_CONFIG_PASSPHRASE`) and Azure credentials. **It creates real Azure resources**: free tier only,
see [standards §6](engineering/standards.md).

```
jd env up samples/environments/shared.yaml --catalog catalog --out shared.env.yaml
jd env up samples/environments/dev.yaml    --catalog catalog --base shared.env.yaml --out dev.env.yaml
```

- Two layers, because Azure allows one free-tier Cosmos account per subscription: `shared` (once per subscription: resource
  group, capped Log Analytics workspace, the Cosmos account) and one definition per environment on top (`dev`, later `stage`:
  resource group, Container Apps environment bound to the shared workspace, a 400 RU/s database in the shared account).
  Everything about them (SKUs, caps, throughput, names, tags) is catalog data: `catalog/mappings/shared-substrate`,
  `catalog/mappings/env-substrate`, `catalog/naming.yaml`.
- `--base` is the descriptor of the layer below. It is the `env` context while resolving (mappings read
  `${env.cosmos.accountName}` and so on), and the written descriptor is its values plus the definition's. A key the base
  already has is an error; `grantable` paths are added to the base's.
- `--region` defaults to `JD_REGION`; one of them is required. The descriptor takes its name and tier from the definition.
- `--out` must not exist unless `--force` is given; this is checked before anything is created.
- Before anything is created the definition is resolved and the descriptor is composed with its values still pending: an
  unknown requirement or export, a collision with the base, or a descriptor rule broken (reserved key, `grantable` path
  that is not a value) is reported and nothing is deployed. After the deploy the descriptor is composed from the outputs,
  validated by the descriptor loader and only then written. Secret or null outputs are never written; a value that needs
  one is an error.
- Running a command again reports every node `unchanged` and rewrites the same descriptor (with `--force`).
- If a deploy fails nothing is written; running again resumes (deployed nodes are `unchanged`).

An environment definition (`kind: EnvironmentDefinition`, [schema](../schemas/environment-definition.schema.json)):

```yaml
kind: EnvironmentDefinition
name: dev                       # the environment's name
tier: team                      # team | protected
requires:                       # substrate, matched by catalog mappings exactly like a workload's requirements
  - type: env-substrate
    id: substrate
values:                         # the descriptor layout; each leaf is an expression over the requirement's exports
  resourceGroup: ${resource.substrate.resourceGroupName}
  cosmos: { databaseName: ${resource.substrate.databaseName} }
grantable: [cosmos.accountId]   # may name base values
approval:                       # optional for team, required for protected; copied as it is into the descriptor
  qualifiedOn: dev              # the environment whose records must show the set qualified
  required: 1                   # distinct approvers needed (1 to the number listed)
  approvers: ["00000000-0000-0000-0000-000000000000"]   # Entra object ids (lower case) that may approve
  separateFromDeployer: true    # an approver may not be a principal that deployed the label on qualifiedOn; required, no default
```

`samples/environments/stage.yaml` is `dev`'s layout plus such a block, tier `protected`: bring it up like `dev`
(`jd env up samples/environments/stage.yaml --catalog catalog --base shared.env.yaml --out stage.env.yaml`). It gets its own resource group,
Container Apps environment and 400 RU/s database (`db-stage`) on the shared Cosmos account and workspace, so `dev` and `stage` use 800 of the free
1,000 RU/s; a third environment would not fit. As shipped the approver is the all-zero id, which matches no principal: replace it with the object ids of
the service principals that may approve before anything can be promoted.

#### Gated environments

An environment whose **descriptor carries an `approval` block** is gated, whatever its tier (the tool tests for the block, never for a tier name). The
schemas only require the block for tier `protected`, so one cannot be written without it. A `protected` environment without an `approval` block fails the schema check (`NotAnyOf … PropertyRequired: #/approval`). `jd deploy`, `jd release deploy` and `jd release qualify`
on a gated descriptor exit 1 with "environment '<name>' is gated by an approval policy: use `jd promote`" before the principal, `JD_RECORDS_DIR` or the
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

| `release deploy <set> --env <environment> --catalog <dir> [--preview]` | Loads the set (re-verifying it like `show`), resolves **every** workload on the environment and checks every graph against the templates, and only then deploys each workload (infrastructure, then the runtime release) in the set's deploy order. Same resolver, template check and orchestrator as `jd deploy`, and the same backend variables (`PULUMI_BACKEND_URL`, `PULUMI_CONFIG_PASSPHRASE`), plus `JD_RECORDS_DIR` and the `ARM_*` principal (below) unless `--preview`. **Creates real Azure resources.** |
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
  `<JD_RECORDS_DIR>/<environment>/<label>/<UTC yyyyMMddTHHmmssfffZ>-deploy.json`; the line `record: <path>` names it. It holds the outcome
  (and the failing workload and node, and the workloads not started), who deployed, what was pinned (hashes, not config), what ran with change
  **counts**, timings, and the operational qualification (OQ) of each workload read from its probe: `passed`, `failed`, `unchanged` (the revision
  was already live) or `notRun`. Contract: `schemas/release-record.schema.json`; design: [release-record.md](architecture/release-record.md).
  A deploy that succeeded then runs **IQ** (the same checks as `jd release qualify`, below) and the record carries it as `iq`: `passed`, `failed`, or `notRun`
  when a workload failed (a failed deploy runs no IQ). `deploySeconds` is the deploy walk alone; the IQ time is `iq.elapsedSeconds`, so the whole run (roughly
  one more pass over every node for IQ) takes longer than `deploySeconds`. **If IQ fails, the command exits 1** and prints each failure as
  `jd: IQ failed: <workload>: <reason>`; the record reads `outcome: succeeded` with `iq.status: failed` and the deployment is left as it is (no rollback). It needs
  the `ARM_*` principal to read the live revisions.
  - `JD_RECORDS_DIR` is **required**: an existing directory (exit 2 and what to set otherwise, before anything is created). Keep it
    somewhere durable; it is the evidence, and nothing in it is a secret.
  - **Who deployed** is the service principal in `ARM_TENANT_ID`, `ARM_CLIENT_ID` and `ARM_CLIENT_SECRET` (the variables Pulumi uses too),
    as Entra reports it for an access token obtained in this run. Missing variables or a refused credential stop the run (exit 1) before
    anything is deployed; the secret is never printed.
  - `<records>/<environment>/<label>/set.yaml` is the set file's exact bytes, kept on the label's first attempt. A later attempt under the same
    label with a different set is refused (exit 1) before anything is deployed: use a new label. It is never overwritten.
  - If the record itself cannot be written, the run exits 1 and says the release was deployed; the deployment stays.
  - Also exit 1, before anything is deployed and with nothing created: a credential Entra refuses or that is malformed (for example `ARM_TENANT_ID`
    neither a directory id nor a domain name), and a label the record store rejects (it must not contain a path separator or `..`, since it names a directory).
    A missing or invalid `JD_RECORDS_DIR` is exit 2.

### `jd release qualify`

Checks, never changes: it makes no deploy call. It runs every check and lists every failure; none stops the others.

- **No drift:** every node of every workload (the grants included) is previewed against what is deployed; a node that would change, cannot be previewed
  (waits on something not deployed) or fails is a failed check. The substrate (`jd env up`) is not part of a release and is not checked.
- **The running image:** the runtime's revisions are read live from Azure (not from state or the record); every image of every revision with traffic must equal
  the workload's pinned image, and at least one revision must hold traffic. A failure reads
  `pinned <image>, live <image> on revision <name>`. If Azure cannot answer (the app is gone, access refused, no network) that is a failed check with the reason.
- Starts like a recorded `release deploy` (needs `JD_RECORDS_DIR`, the `ARM_*` principal and the backend variables; creates `set.yaml` when the label has none,
  refuses a different set under the label), then writes `...-qualify.json` and prints `record: <path>`. The record holds the pinned release and graphs, who ran
  it and the `iq` verdict (`passed` or `failed`, its time and each check); it has no run, no OQ and no deploy time.
- Exit 0 when IQ passes; exit 1 when it fails (each failure is a `jd: IQ failed: <workload>: <reason>` line) or when the record cannot be written.
  The deployment is left as it is.

### `jd approve`

`jd approve <label> --env <stage.env.yaml> --meaning <text>` records that the principal of the `ARM_*` credential approves the set that is kept for `<label>`
(`<JD_RECORDS_DIR>/<qualifiedOn>/<label>/set.yaml`) for a gated environment. `--env` is the target's descriptor, which carries the `approval` policy
(`qualifiedOn`, `required`, `approvers`, `separateFromDeployer`). The approver is a **service principal**, so `approvers` lists principals, and
separation from the deployer means something only when each approver and each deployer has its own. Needs `JD_RECORDS_DIR` and the `ARM_*` variables
(no catalog, no backend). `--meaning` is required: 1 to 500 characters, no control characters; it is recorded and printed, never interpreted.

Exit 1 with a message naming the cause, and nothing written, when: the descriptor has no `approval` block; the label is unknown on `qualifiedOn`
(or its `set.yaml` or directory cannot be read, which is said as such); the set is not qualified on `qualifiedOn` (every reason is listed); the principal
is not in `approvers`; `separateFromDeployer` is on and the principal deployed the label on `qualifiedOn`; or the principal already approved this set for this
environment. A bad credential stops before any record is read (exit 1). Exit 2: a missing or empty `--meaning`, a label that is not a release label (or is a path), an unreadable `--env`,
a missing `JD_RECORDS_DIR`.

It writes `<JD_RECORDS_DIR>/<target>/<label>/<UTC time>-approve.json` (`kind: ApprovalRecord`, [schema](../schemas/approval-record.schema.json); created once,
never edited) and prints `approved <label> for <env> (<n> of <required>)` and `record: <path>`. The record names the target, the label, the SHA-256 of the
kept `set.yaml` (the approval binds the set, not a record), `qualifiedOn`, the principal as the credential reports it and the meaning. A repeat is refused as a
convenience, not a lock; `jd promote` counts distinct principals. Approvals are not revoked: a withdrawn release gets a new label.

### `jd promote`

`jd promote <label> --to <stage.env.yaml> --catalog <dir>` deploys a set to a gated environment. It takes **no set file**: the set is the bytes kept on the
policy's `qualifiedOn` environment, `<JD_RECORDS_DIR>/<qualifiedOn>/<label>/set.yaml`, which become the target's own `set.yaml` unchanged, so both
environments run the same digests and the same set hash. `--to` is the target's descriptor (it carries the `approval` policy). Needs `JD_RECORDS_DIR`,
the `ARM_*` principal and the backend variables, as `release deploy` does.

The set is promoted only when it is **qualified** on `qualifiedOn` (the newest deploy succeeded and the newest IQ passed, judged from the records now) and
**approved** by enough distinct principals (`jd approve`, counted by the environment's policy). Whoever runs `promote` is not checked against `approvers`.
Otherwise exit 1 lists every reason (and every approval file that was ignored and why) and nothing is written, no backend is called. Exit 1 also when the descriptor has no
`approval` block. Exit 2: a missing or bad argument, a label that is not a release label, an unreadable `--to` or `--catalog`, a missing `JD_RECORDS_DIR` or
backend variable.

When it may go ahead it prints `promoting <label> from <source> (set sha256 <h>)`, then runs the walk of `jd release deploy` on the target with its output, exit
codes, deploy record (with IQ) and `set.yaml` check. The record's `release.setSha256` is `<h>`. `jd release deploy`, `jd deploy` and `jd release qualify`
stay refused on the gated environment.

## Walkthrough

From a clean subscription to a release promoted to `stage`, then teardown. Commands only; each is described above. `jd` stands for
`dotnet run --project src/jd.cli --`. Free tier only: the subscription must have no free-tier Cosmos account yet (Azure allows one).
To test every check by hand (bad release, drift, refused approvals, tampering), follow [walkthrough.md](walkthrough.md) instead.

**1. Prerequisites.** The .NET SDK, the `pulumi` CLI and the `az` CLI. A deploying service principal and, because `stage` keeps approval separate
from deploying (`separateFromDeployer: true`), a second one that approves. Set (names only; never commit the values):

| Variable | For |
|---|---|
| `ARM_CLIENT_ID`, `ARM_CLIENT_SECRET`, `ARM_TENANT_ID`, `ARM_SUBSCRIPTION_ID` | the principal that runs the command (Pulumi, the records' identity) |
| `JD_REGION` | the region of everything (or `--region` on `env up`) |
| `PULUMI_BACKEND_URL`, `PULUMI_CONFIG_PASSPHRASE` (or `PULUMI_CONFIG_PASSPHRASE_FILE`) | the Pulumi state; the same values on every run |
| `JD_RECORDS_DIR` | an existing directory for the release records (the evidence) |

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

**5. Approve and promote to `stage`.** `jd approve` as the approver (its `ARM_*` values in the environment), then `jd promote` as the deployer (whoever runs it is not checked against `approvers`):

```
jd approve 1.0.0 --env stage.env.yaml --meaning "Reviewed the dev evidence for 1.0.0."
jd promote 1.0.0 --to stage.env.yaml --catalog catalog
```

**6. Read the records.** Every attempt is one JSON file, never edited (`schemas/release-record.schema.json`, `schemas/approval-record.schema.json`):

```
ls "$JD_RECORDS_DIR"/dev/1.0.0     # set.yaml, …-deploy.json, …-qualify.json
ls "$JD_RECORDS_DIR"/stage/1.0.0   # set.yaml (dev's bytes), …-approve.json, …-deploy.json
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

Then remove the Pulumi state at `PULUMI_BACKEND_URL` (its stacks now describe deleted resources) and the descriptors. Keep `JD_RECORDS_DIR` if the
evidence is wanted; nothing in it is a secret.
