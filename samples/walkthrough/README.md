# Walkthrough kit

The files [docs/walkthrough.md](../../docs/walkthrough.md) uses, so nothing is typed by hand. Every workload uses the published sample-app image digest;
a "new version" is an `APP_VERSION` change (a new revision of the same image). `CliTests.The_walkthrough_kit_passes_the_offline_checks_and_its_policy_shows_in_the_preview`
previews every workload and every release here offline.

| File | What it is | Scenarios |
|---|---|---|
| `env.example.sh` | Every variable the tool and the walkthrough need, placeholders only, and a `jd` shell function (jd built once into `$JD_WALK/jd`) | setup |
| `workloads/app.yaml` | The sample app, `APP_VERSION: "1"` | 2, 3, 5, 10 |
| `workloads/app.bad.yaml` | The app with `COSMOS_CONTAINER` naming a container that does not exist: `/health` answers 503 | 4 |
| `workloads/worker.v1.yaml`, `worker.v2.yaml`, `worker.v3.yaml` | The worker, `APP_VERSION` `"1"`, `"2"` and `"3"` | 2, 3, 6 (v3) |
| `releases/v1.manifest.yaml` | Label `1.1.0`: app + worker v1; the worker depends on the app | 2, 7, 8, 9 |
| `releases/v2.manifest.yaml` | Label `1.2.0`: the same app + worker v2 | 3, 4 (revert), 6, 10 |
| `releases/v3.manifest.yaml` | Label `1.4.0`: the same app + worker v3, the recovery from the drift | 6 |
| `releases/bad.manifest.yaml` | Label `1.3.0-bad`: the bad app + worker v2 | 4 |
| `policies/limit-revisions.yaml` | A policy to copy into a **copy** of the catalog (never into `catalog/`) | 5 |
| `stage-policy.sh` | Writes a copy of the stage descriptor with another approval block (required, separation, approvers) | 7, 8 |
| `teardown.sh` | Deletes the environment groups in parallel, then the shared group, then lists what is left | 11 |

Scenario 1 uses `samples/workloads/sample-app.yaml` itself; the environments are `samples/environments/*.yaml`.
