# Using the CLI

`jd` (project `src/jd.cli`) is the composition root: it parses arguments, wires the validator and the
resolver ([resolver.md](architecture/resolver.md)), prints results and sets the exit code. It holds no
resolution logic. Every input is an argument; only the deploy backend settings come from environment variables.

```
dotnet run --project src/jd.cli -- validate <workload.yaml>
dotnet run --project src/jd.cli -- preview <workload.yaml> --env <environment.yaml> --catalog <dir> [--json]
dotnet run --project src/jd.cli -- release create <manifest.yaml> --out <release-set.yaml>
dotnet run --project src/jd.cli -- release show <release-set.yaml>
dotnet run --project src/jd.cli -- deploy <workload.yaml> --env <environment.yaml> --catalog <dir> [--preview]
dotnet run --project src/jd.cli -- env up <definition.yaml> --catalog <dir> --out <descriptor.yaml> [--base <descriptor.yaml>] [--region <region>] [--force]
dotnet run --project src/jd.cli -- --help
```

| Command | What it does |
|---|---|
| `validate` | Checks the workload against `workload.schema.json` (embedded in `jd.definitionvalidator`) and the workload rules. Prints one error per line as `file: location: message`. |
| `preview` | Validates the workload first, then loads the catalog and environment descriptor, expands requirements, applies policies and builds the graph. Prints each node in graph order with id, template, kind, phase, stack, short hash, dependencies and every config field with its value (`pending: <expression>` when it waits for a runtime or dependency output; `(from env.<path>)` when read from the environment) and provenance (layer and rule). `--json` prints the graph as the stable JSON the golden snapshot pins instead. |

### `jd deploy`

Resolves like `preview`, checks the graph against the templates in `<catalog>/templates`, then provisions the
infrastructure nodes through the Pulumi backend in graph order ([Orchestrator](architecture/provisioning.md#orchestrator)).
This **creates real Azure resources**: use a subscription with the guardrails of [tools/sandbox](../tools/sandbox/README.md)
and the Azure credentials in the environment (`source ~/.just-deliver/<subscription>.env`).

- One line per node as it completes: `<node id>: <outcome> (<changes>, <seconds>s)`, then the resources changed.
  Outcomes: `deployed`, `unchanged` (the stack already matched), `previewed`, `pending upstream` (`--preview` only: a
  value from a node that is not deployed yet is missing), `waiting for runtime` (the node depends on the runtime, which
  is not deployed by this command), `failed`.
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
```

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | Validation or resolution errors (workload, catalog, environment or graph), or a failed deployment |
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

Exit codes are the same as above (a manifest, set or `--out` path problem is 2; invalid content is 1).
Resolving each workload and deploying a set is not part of these commands yet.
