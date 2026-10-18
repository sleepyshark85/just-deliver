# What jd connects to: nothing set on the machine

Status: **user direction (2026-10-18)**: "nothing need to be set in any machine running jd. Deployments should be stateless, jd should
decide what to connect", "everything should be tested in my local machine now, so state storage (and any other external concerns of jd)
should be implemented in an abstract manner and defaulted to local", "it should be part of MVP". Slice **S23** (MVP), **S23 built**. The lead's
design below; the user reviews it.

## Problem

Before `jd` runs, the machine must export what `jd` needs to connect: `PULUMI_BACKEND_URL` (where Pulumi state lives, a local folder in the
walkthrough), `PULUMI_CONFIG_PASSPHRASE` (or `_FILE`), `JD_RECORDS_DIR` (release and approval records), `JD_REGION`, and `ARM_*` (the
service principal). A run is only reproducible on a machine set up by hand, and state is wherever those variables point.

## Design

Each external concern sits behind one abstraction with one implementation now, **local**, which is the default. `jd` decides the
location; nothing is required from the machine except a signed-in identity.

| Concern | Abstraction | Local default | Today |
|---|---|---|---|
| Deployment state | a state store the backend provider is given (backend URL plus secrets settings) | `file://<data>/state` | `PULUMI_BACKEND_URL`, required |
| State encryption | the same state store | a random passphrase generated once into `<data>/state.key` (mode 600), passed to Pulumi as a passphrase file | `PULUMI_CONFIG_PASSPHRASE`, required |
| Release and approval records | `IRecordStore` (`jd.core`): write a record, list an environment's records; the record files, qualification reader and approvals go through it | `<data>/records` (today's layout) | `JD_RECORDS_DIR`, an existing directory, required |
| Region | none: it is environment data | the environment definition's `region` (required), copied to the descriptor | `--region` or `JD_REGION` |
| Identity | `IDeployerIdentity`, `ILiveRevisions` (exist) | the Azure CLI sign-in (`az login`): `AzureCliCredential` for jd, Pulumi's CLI authentication for the provider | `ARM_*`, required |

- **`<data>`** is jd's data directory: `Environment.SpecialFolder.LocalApplicationData/jd` (`~/.local/share/jd` on Linux), created on first
  use. One optional override, `JD_DATA_DIR`, for tests and for keeping runs apart (the Azure tests and the upgrade gate use it); never
  required.
- **Identity:** when the `ARM_*` variables are all set, they are used as today (CI and the lead's Azure gate sign in as a service principal,
  which Pulumi's CLI authentication does not cover). Otherwise the Azure CLI sign-in. A partial `ARM_*` set is an error naming the missing
  ones, as today. A second principal for approvals is `az login` as that principal (or its `ARM_*`).
- **Region in the definition** makes the environment's inputs explicit and the same on every machine (principles 6, 13). The sample
  definitions carry a region; the walkthrough says to set it to the sandbox's allowed region. `--region` and `JD_REGION` go.
- **No `state:` or `records:` field yet:** with one implementation a field would be a choice of one (simplification). The Azure-backed
  stores (a blob container owned by the shared layer, reached through the descriptor) add the field when they arrive; the abstractions are
  where they plug in.
- **Kept:** `PULUMI_HOME` and `JD_SCRATCH_DIR` stay optional overrides (caches and temporary files). `PULUMI_BACKEND_URL`,
  `PULUMI_CONFIG_PASSPHRASE[_FILE]` and `JD_RECORDS_DIR` are no longer read.

## Migration

State and records written under the old variables are not found at the new location: start the walkthrough from an empty `$JD_WALK`
after tearing down (or move the old state folder to `<data>/state` and set the generated key to the old passphrase by hand: documented
in `usage.md`, not automated). `samples/walkthrough/env.example.sh` shrinks to `JD_WALK`, `AZURE_CONFIG_DIR` and the build line; the
walkthrough setup becomes `az login`.

## Proof

Offline: each default and the `JD_DATA_DIR` override tested; no `jd` command reads the removed variables (a test runs the CLI with an
environment holding only the identity); records written and read through `IRecordStore`. Azure gate (the direct-Azure code changes):
`verify.sh --azure` with `JD_DATA_DIR` per run; the deployed-config snapshot unchanged except where the region now comes from (same
value).
