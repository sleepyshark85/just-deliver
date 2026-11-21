# Using jd

`jd` validates and previews workloads, brings up environments, and creates, deploys, qualifies, approves and promotes release sets on Azure.
It is a thin CLI over use cases ([application.md](architecture/application.md)): it parses arguments, reads the files they name, prints results
and sets the exit code. Hands-on scenarios: [walkthrough.md](walkthrough.md).

```
jd validate <workload.yaml>
jd preview <workload.yaml> --env <environment.yaml> --catalog <dir> [--json]
jd deploy <workload.yaml> --env <environment.yaml> --catalog <dir> [--preview]
jd env up <definition.yaml> --catalog <dir> --out <descriptor.yaml> [--base <descriptor.yaml>] [--force]
jd release create <manifest.yaml> --out <release-set.yaml>
jd release show <release-set.yaml>
jd release deploy <release-set.yaml> --env <environment.yaml> --catalog <dir> [--preview]
jd release qualify <release-set.yaml> --env <environment.yaml> --catalog <dir>
jd approve <label> --env <environment.yaml> --meaning <text>
jd promote <label> --to <environment.yaml> --catalog <dir>
jd --help
```

From source, `jd` is `dotnet run --project src/jd.cli --`. `--env` and `--to` take an environment **descriptor** (the file `jd env up` writes).

| Command | What it does |
|---|---|
| `validate` | Checks a workload definition against its schema and rules. |
| `preview` | Resolves a workload on an environment over a catalog and prints the graph. Creates nothing; needs no sign-in. |
| `deploy` | Deploys one workload: the unrecorded development path (no label, no record). **Creates Azure resources.** |
| `env up` | Provisions an environment's substrate from its definition and writes its descriptor from the outputs. **Creates Azure resources.** |
| `release create` | Writes an immutable release set from a manifest: every definition and image pinned, the deploy order worked out. |
| `release show` | Verifies a release set and prints it. |
| `release deploy` | Deploys every workload of a set in deploy order, runs IQ, writes a release record. **Creates Azure resources.** |
| `release qualify` | Re-checks a deployed set (IQ) without deploying; writes a record. |
| `approve` | Records that the signed-in principal approves a label's kept set for a gated environment. |
| `promote` | Deploys a label's kept set to a gated environment once it is qualified and approved. **Creates Azure resources.** |

Errors are printed to stderr, one per line as `file: location: message`; refusals as `jd: <message>`. Results go to stdout.

## Workloads

**`validate <workload>`** checks the definition against `workload.schema.json` (embedded in `jd.resolver`) and the workload rules. Prints
`<file>: valid.`

**`preview <workload> --env --catalog [--json]`** validates the workload, loads the catalog and the descriptor, checks the descriptor's type
against the catalog, then resolves. Prints `<workload> in <environment> (catalog <version>): <n> nodes`, then per node in graph order: its id;
`template:`, `kind:`, `phase:`; `stack:` and a short `hash:`; `probe: GET <path> expects <status>` for the runtime; `depends on:`; and every
config field as `<path> = <value>   [<provenance>]`. A value is `pending: <expression>` when it waits for a runtime or dependency output, and
ends `(from <part>.<export>)` when read from the descriptor. `--json` prints the graph as the stable JSON the golden snapshot pins.

**`deploy <workload> --env --catalog [--preview]`** resolves like `preview`, checks the graph against `<catalog>/templates`, then provisions
the infrastructure nodes in graph order and releases the runtime ([provisioning.md](architecture/provisioning.md)).

- One line per node: `<node id>: <outcome> (<changes>, <seconds>s)`, then the resources changed. Outcomes: `deployed`, `unchanged`,
  `previewed`, `pending upstream` (`--preview`: an upstream node is not deployed yet), `failed`. The runtime comes last with one line per
  release step: `dark`, `grants`, `probe`, `shift`; a re-run with unchanged config reports `probe` and `shift` `skipped`.
- The first failure stops the run: `jd: stopped at <node id>: <reason>`, exit 1. Deployed nodes stay; running again resumes.
- `--preview` shows what would change and creates nothing.
- Refused on a gated environment (below).

**Gated environments.** A descriptor with an `approval` block is gated ([approvals.md](architecture/approvals.md) §2). `deploy`, `release deploy`
and `release qualify` on it exit 1 with ``jd: <command>: environment '<name>' is gated by an approval policy: use `jd promote`.`` before the
sign-in, the records or the backend is touched. `--preview` and `env up` are not refused.

## Environments

**`env up <definition> --catalog --out [--base] [--force]`** deploys what the definition's `offers` reach in its type's layer and writes the
descriptor ([schema](../schemas/environment-definition.schema.json); layering: [provisioning.md](architecture/provisioning.md)).

- `--base` is the descriptor of the layer below (`shared` under `dev` and `stage`); the written descriptor is the base's values plus this run's.
- `--out` must not exist unless `--force` is given, checked before anything else (exit 2).
- Before anything is created: the definition's type and platform, and the base's, must be declared in `catalog.yaml`, the base must be of the
  definition's platform, and a type with `approval: true` needs an `approval` block; the definition must resolve and the descriptor compose with
  its values pending. Any error: exit 1, nothing deployed.
- A descriptor written by a jd older than its `platform` field is refused wherever it is read; re-running `jd env up` with its definition
  rewrites it.
- Prints the node lines of `deploy`, then `<out>: environment <name> (<region>, <type>) written.` A failed deploy writes nothing; running again resumes.

```
jd env up samples/environments/shared.yaml --catalog catalog --out shared.env.yaml
jd env up samples/environments/dev.yaml    --catalog catalog --base shared.env.yaml --out dev.env.yaml
```

## Releases

A release is one label over a pinned set of workloads ([release.md](architecture/release.md)); its evidence is the release record
([release-record.md](architecture/release-record.md)).

**`release create <manifest> --out <set>`** applies the rules of [release.md](architecture/release.md#rules) and prints
`<set>: release <label> with <n> workload(s), deploy order <names>.` An existing `--out` is refused (exit 2): sets are immutable.

**`release show <set>`** re-verifies the set and prints `release <label> (created <time>)`, the deploy order, and per workload its team,
`sha256`, image and dependencies. A modified set, or one that breaks a rule, is an error (exit 1).

**`release deploy <set> --env --catalog [--preview]`** verifies the set, resolves **every** workload and checks every graph against the templates
before the first node; then deploys each workload in deploy order.

- Output per workload: `workload <name>`, then the `deploy` node lines. Then
  `release <label> on <env>: <n> deployed, <n> unchanged, <n> failed, <n> not started.`, with ` (<names>)` after "not started" only when some were not started; a workload counts as deployed when any
  node changed. `--preview` creates nothing and records nothing; its summary says `<n> previewed`.
- The first failed node stops the run: `jd: stopped at <workload>, node <node id>: <reason>`, exit 1; later workloads are not started.
- A recorded run names the principal, then keeps the set as `set.yaml` beside the records; a different set under the label is refused (exit 1)
  before anything is deployed: use a new label.
- After a successful walk IQ runs: `release <label> on <env>: IQ passed|failed, <n> checks, <m> failed.`, each failure on stderr as
  `jd: IQ failed: <workload>: <reason>`. A failed IQ exits 1 and leaves the deployment as it is.
- Last line: `record: <path>`. A record that cannot be written: `jd: the release was deployed, but its record could not be written: …`, exit 1.
- Refused on a gated environment.

**`release qualify <set> --env --catalog`** starts like a recorded `release deploy`, runs IQ and nothing else (no deploy call), then prints the
IQ line, the failures and `record: <path>`. Exit 0 when IQ passes, else 1. Refused on a gated environment.

## Approvals and promotion

`--env` / `--to` is the gated environment's descriptor; its `approval` block is the policy. The rules are in [approvals.md](architecture/approvals.md).

**`approve <label> --env --meaning <text>`** records an approval of the set kept for `<label>` on the policy's `qualifiedOn` environment.
`--meaning` (1 to 500 characters, no control characters) is recorded and printed, never interpreted. Prints `approved <label> for <env> (<n> of
<required>)` and `record: <path>`. An approval file that cannot count is reported as `jd: approve: ignored <file>: <why>`.

**`promote <label> --to --catalog`** takes no set file: it deploys the set kept on `qualifiedOn`. When the set is qualified and approved it prints
`promoting <label> from <source> (set sha256 <h>)`, then runs the `release deploy` walk on the target, with its output, IQ and record.
Whoever runs it is not checked against `approvers`.

| Refusal (exit 1, nothing written) | `approve` | `promote` |
|---|---|---|
| The descriptor has no `approval` block | `…has no approval policy, so there is nothing to approve.` | `…nothing to promote through.` |
| The credential names no principal | `jd: <reason>` | `jd: <reason>` |
| No `set.yaml` for the label on `qualifiedOn`, or it cannot be read | `jd: approve: cannot approve <label>: <reasons>` | a reason below |
| Not qualified on `qualifiedOn` | `jd: approve: release <label> is not qualified on <env>:` and each reason | `jd: promote: release <label> cannot be promoted to '<env>':` and each reason |
| The principal is not in `approvers` | `…is not listed in the approvers of '<env>'.` | (not checked) |
| `separateFromDeployer` and the principal deployed the label | `…requires approvers separate from the deployer.` | the approval does not count |
| The principal already approved this set | `…already approved <label> for '<env>'.` | counted once |
| Fewer than `required` approvals count | (not checked) | `  <n> of <required> required approvals for '<env>' count.` |
| The approval file cannot be created | `jd: approve: the approval could not be written: <reason> If the file already exists, run the command again.` | (not applicable) |
| The kept set changed after it was checked | (not applicable) | `jd: promote: the kept set '<set>' changed since it was checked (expected sha256 <h>).` |

Usage errors (exit 2): a missing or invalid `--meaning`; a label that is not a release label (like 1.0.0 or 1.0.0-rc.1), or one that cannot name a records directory (empty, or containing `..`, `/` or `\`).

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | Validation or resolution errors (workload, manifest, set, catalog, descriptor, graph); a refusal (gated, not gated, a credential that names no principal, a different set under the label, not qualified, not approved); a failed deployment or IQ; a record that cannot be written. |
| 2 | Usage error: unknown command or option, a missing argument or value, a file or directory not found, an existing `--out`, an invalid label or meaning for `approve`/`promote`, a file or the data directory that cannot be read or written. |

## Data and identity

Nothing needs to be set on the machine ([provisioning.md](architecture/provisioning.md) on the backend and state).

- **Data directory** `<data>`: `jd` in the local application data folder (`~/.local/share/jd` on Linux), or `JD_DATA_DIR`. Created on first use:
  `state/` (the Pulumi state), `state.key` (the random passphrase that encrypts the state's secrets; keep it with `state/`), and `records/`
  (`<environment>/<label>/`: `set.yaml`, deploy, qualify and approve records; the evidence, nothing secret).
- **Region:** each environment definition's `region`.
- **Identity:** the Azure CLI sign-in (`az login`, `az account set`), for jd and Pulumi; or a service principal when all of `ARM_TENANT_ID`,
  `ARM_CLIENT_ID` and `ARM_CLIENT_SECRET` are set (plus `ARM_SUBSCRIPTION_ID` for Pulumi). A partial set is refused (exit 1) and names the
  missing ones; the secret is never printed. Records name this principal as the deployer or approver.
- **Optional:** `PULUMI_HOME` (plugin cache), `JD_SCRATCH_DIR` (working directories; default the system temp directory).
- Free tier only: use a subscription with the guardrails of [tools/sandbox](../tools/sandbox/README.md).
