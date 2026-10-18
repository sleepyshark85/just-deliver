# Release record and qualification

> Status: design note for S17 (MVP). S17a (identity), S17b (record, OQ, timings, `set.yaml`) and S17c1 (the grants preview, `ILiveRevisions`, the `Qualifier`, `jd release qualify` and its record) and S17c2 (IQ inside `jd release deploy`) are built. Addresses [E52](../open-questions.md); the audit store (F39) is out of scope.
> Builds on [release.md](release.md) (the set), [provisioning.md](provisioning.md) (the walk and release flow) and
> [ADR 0007](../decisions/0007-definition-upload-snapshot.md). Approvals (S18, F54) and promotion (S19) will use the same records.

A **release record** is the local evidence of one attempt to put a release set on one environment: what was pinned, what ran, how
long it took, who ran it, and whether the installation was **qualified**. IQ (installation) and OQ (operation) are the two
verdicts it carries. Nothing in it is a secret and nothing in it is edited after it is written.

## 1. Where and how it is stored

- **Location:** `<data>/records` in jd's data directory, through `IRecordStore` (S23, [connections.md](connections.md); before S23,
  `JD_RECORDS_DIR`, an existing directory, was required). It is
  not in the environment descriptor: the descriptor is generated from the substrate's outputs and describes the environment, not where
  the operator keeps evidence.
- **One file per attempt, never per label:** `<records>/<environment>/<label>/<UTC yyyyMMddTHHmmssfffZ>-<activity>.json`, activity
  `deploy` or `qualify` (the approvals of [approvals.md](approvals.md) are `<time>-approve.json` files of the same store, in the gated environment's directory; they are not release records). The environment name and the label are both format-restricted (they match the id and label patterns), and the store
  rejects anything with a separator or `..` anyway.
- **Write-once:** the file is created with `FileMode.CreateNew` (as `ReleaseSetFile` does), so an existing path is an error and
  nothing is ever opened for write twice. The time in the name is the **finish** time (when the file is written), so the newest file is the
  last attempt to complete. A re-run writes a **new** file; a failed run writes one too (outcome `failed`). A preview, a
  usage error or a resolution error writes none (nothing was deployed). A killed process writes none: the file is written at the end.
  If the write itself fails after a deploy, the run exits 1 and says so; the deployment stays.
- **Format:** JSON, `kind: ReleaseRecord`, contract in `schemas/release-record.schema.json` (as the set and manifest have), pinned by a test
  (a committed golden record, validated against the schema). `activity` is `deploy` or `qualify`; a deploy requires `outcome`, `deploySeconds`, `notStarted`, `iq` and each workload's `oq`, a qualify requires `iq` (`oneOf`, with
  `additionalProperties: false` kept). `iq.status` is `passed`, `failed` or, for a deploy only, `notRun` (no checks, no time: a workload failed, so IQ did not run). Deploy records written before S17c2 have no `iq` and are no longer valid; they exist only as test fixtures (the reader of S18b reads only the current shape).
- **Finding it later (S18, S19):** the records of label X on environment Y are the files in `<records>/Y/X/`. How "qualified" is derived from them and how approvals are stored and judged is in [approvals.md](approvals.md) sections 4 and 5.
- **The set itself is kept beside the records, as ADR 0007 requires:** `<records>/<environment>/<label>/set.yaml` holds the set file's exact
  bytes (the definitions as received). It is created with `CreateNew` on the label's first attempt. On every later attempt the existing file
  must hash equal to the set being deployed; otherwise the run is refused before anything is deployed (a different set under the same label).
  It is never overwritten. Records refer to it by `setSha256`. *Residual:* it is written in place, not through a temp file and rename, so a crash in the middle of the write leaves a truncated `set.yaml`, which the next attempt reports as a different set; delete it by hand.
- **Order of a recorded run (S17b):** parse and resolve everything (as today, no backend call); then obtain the deployer identity, token
  included; then check or create `set.yaml`; then the first backend call. A bad credential or a different set under the label therefore stops
  the run before anything is deployed (exit 1; a missing or invalid `JD_RECORDS_DIR` is a usage error, exit 2, checked first). `release qualify` follows the same order and also creates `set.yaml` when it is missing. A failure after `set.yaml` is created but before the walk starts (building the backend or the live-revisions reader) writes no record, as a killed process writes none; `set.yaml` stays and the next attempt reuses it.
- **`jd deploy <workload>` writes no record:** it has no label; the release is the unit that is deployed and promoted. It is the unrecorded
  development path ([usage.md](../usage.md)); the evidence of S20 comes from release sets.

## 2. Contents and where each value comes from

| Field | Source today |
|---|---|
| `activity`, `startedAt`, `finishedAt` (UTC, to the second) | composition root, an injected `TimeProvider` |
| `label`, set `createdAt`, `setSha256` (SHA-256 of `set.yaml`'s bytes) | `ReleaseSet`; the hash and the bytes are new: `ReleaseSetFile.LoadAsync` exposes the bytes it verified, so the hash and the copy come from one read |
| per workload: `name`, `team`, `definitionSha256`, `dependsOn`, `image` (digest-pinned), deploy order | `ReleaseWorkload`, `ReleaseSet.Order` |
| `environment`, `type` (`tier` in records written before S21a, still accepted), `region`, `catalogVersion` | `EnvironmentDescriptor`; `ResolvedGraph.CatalogVersion` (a version label, not a content hash) |
| graph: nodes as `{id, template, stack, phase, hash}` and `graphSha256` of `GraphJson.Serialize` | `ResolvedGraph` |
| per workload and node: outcome, change **counts**, message, elapsed; runtime steps with outcome, detail, elapsed, attempts | `WorkloadRun`, `NodeReport`, `StepReport` |
| `outcome`: `succeeded` or `failed`, with the failing workload and node, and the workloads not started | the runs and `ReleaseSet.Order` |
| `identity` | the identity port (section 3) |
| `oq` per workload, `iq` | sections 4 to 6 |

- **As built:** the record has `workloads` in deploy order, each with its pinned fields, `graph` (`sha256` and the nodes as `{id, template, stack,
  phase, hash}`), `run` (absent when the workload was not started: `elapsedSeconds` and its nodes with outcome, change counts, message, elapsed
  and, for the runtime node, the steps) and `oq`; at the top `outcome`, `failure` (`{workload, node}`, only when failed), `notStarted`, `identity`,
  `environment`, `release` (`label`, `createdAt`, `setSha256`), `deploySeconds` and `iq` (section 6). `failure.node` is the last node of the failed workload's
  report: the runtime node when one of its steps failed (its message reads `<step>: <reason>`).
- **The graph is stored as hashes, not config.** The node hashes and `graphSha256` let anyone check that a graph resolved from the
  set, the catalog and the environment matches the one that was deployed (hashes are deterministic, S07); this also keeps config out of the record. The records carry the definitions' SHA-256
  and the set file's; the definition text itself is the verified `set.yaml` beside them (ADR 0007: the platform keeps what it receives), so
  each record stays small and the content is stored once per label.
- **Secrets cannot reach the record, by construction, not by filtering.** The writer is an allowlist of the fields above. It never reads
  `NodeReport.Outputs` (already non-secret only, but unneeded) or `NodeReport.Changes` (per-property old and new values, which
  may carry secret values). Remaining free text is `StepReport.Detail` and `NodeReport.Message`: revision names, `HTTP 503`, the transport
  error, or provider and exception text. Pulumi masks the secrets it knows in its diagnostics and we add no scrubbing, so this text is the
  residual risk; a test pins that a secret output, a secret config value and a changed-property value never appear.
- **Timings:** the node's `Elapsed` exists; the **step** elapsed and the probe **attempts** do not, so `StepReport` gains `Elapsed` and
  `Attempts` (a stopwatch per step in `ReleaseFlow`, the attempt counter it already has) and `WorkloadRun` gains `Elapsed`. A workload's
  nodes cannot be summed: the grants run inside the runtime's Grants step, so the runtime node's elapsed already contains them. The deploy
  walk's total (success criterion 6) is recorded as its own field, `deploySeconds` (a stopwatch around the walk), and the IQ walk as
  `iq.elapsedSeconds`, so the deploy time excludes IQ; `startedAt` and `finishedAt` bracket the whole activity.

## 3. Machine identity

The principal the run authenticated with, `{tenantId, clientId, objectId}`, from a port `IDeployerIdentity` in `jd.core` (`identity/`, beside the
`IBackEndProvider` port). Its adapter (new project `jd.azure`, which references only `jd.core` and Azure SDKs; `HttpRevisionProbe` in the CLI
is the precedent for adapters at the root, but an Azure SDK reference belongs behind a boundary, enforced by `tools/verify.sh`) builds a `ClientSecretCredential` from `ARM_TENANT_ID`, `ARM_CLIENT_ID` and
`ARM_CLIENT_SECRET`, the variables Pulumi uses, gets an ARM token and reads `tid`, `appid` and `oid` from it. A credential Entra refuses
(`AuthenticationFailedException`) is turned into an `InvalidOperationException` that names the three variables and carries neither the SDK's
text nor any value; the CLI prints it and stops (exit 1) before the backend is called. Reading the claims without validating the
signature is sound: the token came straight from Entra over TLS in this call, and the claims are what Entra says the credential is, not
what the environment says. The CLI refuses to deploy if the variables are missing (Pulumi would otherwise fall back to an ambient `az` login that
the record could not name), and never prints the secret (`ArmCredentials` is a class, so no generated `ToString` can). The ARM token scope is a named constant in
the adapter (standards section 1 exception for Azure protocol constants). Public cloud only, as the rest of the MVP (E53).

It cannot be spoofed by workload data because no definition, catalog, descriptor, release-set or stack-output value flows into it: the
record builder takes the port's result as an argument, and the port reads only the credential. A person who controls the process
environment controls the identity; that is the trust boundary of a local tool (no signing in the MVP).

## 4. OQ

From the runtime node's `Steps`, no new probing. Per workload: `oq.status` is `passed` (Probe `Done`), `failed` (Probe `Failed`; the
reason and attempts are in the detail), `unchanged` (Probe `Skipped`: the new revision is already the live one, which only a passed
probe and shift could have made it; for S19 it counts toward "qualified" only when an earlier record under the same `<env>/<label>/` has OQ
`passed` for that workload, [approvals.md](approvals.md) section 5) or `notRun` (the dark or grants step failed, or the workload was not started). The contract applied
(`path`, `expectedStatus`) comes from the graph node's `probe`, which is catalog data. Plus the probe's detail, attempts and elapsed.

## 5. IQ: no drift

No new provider method. `PreviewAsync` already runs a refresh (persisted) and then a preview, so "a refresh shows no drift" is the existing
preview walk (`Orchestrator.DeployReleaseAsync(preview: true)`) over **every node of every workload in the set** with **no change operation**
in any summary (`DeploymentResult.ChangedOperations`) and no `PendingUpstream` or `Failed` node. The substrate stacks (`jd env up`) are
excluded: they are not part of the release; qualifying them is later work.

*Verified offline* (Pulumi 3.256, file backend, `random` provider, a state edited to look drifted): with no drift, refresh then preview gives
all `same`; a changed resource input in state gives `replace` after a persisted refresh and **again on every later preview**, and a resource
removed from state gives `create`. So a persisted refresh does not absorb drift: the diff against the program stays visible.
*Not verifiable offline:* that azure-native's refresh of a Container App, its role assignments and the Cosmos container reflects an
out-of-band change into the inputs (it should; S11 shows no perpetual diffs on a clean re-run).

**The gap, closed in S17c1:** the preview walk did not preview the after-runtime nodes (the grants), because it never runs the Grants step. They
matter most for identity, so in preview `ReleaseFlow` now calls `deployDependants` with the runtime outputs read from state (the walk already
reads them for upstream nodes, and a never-deployed runtime yields `PendingUpstream`). A grant that fails in preview fails the runtime node's report (`grants: ...`).

## 6. IQ: running digests

Observed from Azure, never from state or the record. A new port `ILiveRevisions` in `jd.core` (like `IDeployerIdentity`, so `jd.azure` needs
nothing else of ours): given the runtime's resource id, return the revisions that hold traffic, each with its name, weight and container
images. The `Qualifier` in `jd.orchestrator` consumes it. Adapter in `jd.azure` over
`Azure.ResourceManager.AppContainers` (new package, 1.6.0, the newest stable; the reason is this port; `ContainerAppRevisionData.TrafficWeight` and
`Template.Containers[].Image` exist in 1.6.0, checked in its XML docs; it shares the credential of section 3). The check: **every image of every
revision with weight > 0 equals the workload's pinned image, ordinal string equality**, and at least one revision holds traffic.

- **The live revision, not the latest.** `ContainerApp.template` and `latestRevisionName` describe the newest revision, which after a
  failed dark deploy is the bad one while the old one serves. Traffic weight on the revisions is what serves, and `trafficWeight` is
  read from Azure, not from the stack's `traffic` echo (which only says what we last asked for).
- **The resource id is data:** the mapping's `release:` block gains `resourceIdOutput: containerAppId` (the ninth name; parser, schema,
  `RuntimeRelease`, `GraphJson`, goldens). The id is read with `GetOutputsAsync`; it only identifies the app, the observation is live.
- **Why a typed port and not data:** the generic alternative (mapping carries an ARM path, api-version and JSONPath expressions; the adapter is a
  raw ARM GET) needs a hand-built ARM client, an endpoint to configure and a small query language in the catalog, for a runtime that
  has one implementation (ADR 0013). The adapter holds the runtime port's Container Apps knowledge (revisions, weights), like the probe's
  transport; no workload, name or image is in it. A second runtime replaces the adapter and the one mapping name.
- **As built (S17c1):** `ILiveRevisions` is `GetAsync(resourceId)` returning `LiveRevision(name, trafficWeight, images)` and throws `InvalidOperationException` with a
  message for a person (HTTP status and Azure error code only, never the reply) when Azure cannot answer; `ArmLiveRevisions` is the adapter, over the same `ARM_*`
  credential as the identity. The `Qualifier` (in `jd.orchestrator`) previews each workload on its own, so a failure in one does not hide another, turns every
  graph node into a `drift` check and the runtime's revisions into `image` checks, and makes an exception of the reader or of the state read a failed check. A node the
  walk never reached (it stops at a failed node, or at a runtime that is pending) is a failed check, "not checked", never a silent pass. The pinned image is the graph's
  `WorkloadImage`, which for a release is the digest-pinned `container.image` of the set's definition.
- **The record's `iq`:** `{status, elapsedSeconds, checks[]}`, each check `{workload, kind: drift|image, subject, status, detail}`; `detail` is the failure text and empty
  when the check passed. `subject` is the node (drift), the revision (image) or the workload when there is no revision to name.
- **Limit, stated:** Azure reports the image reference configured on the revision, which is the digest we pinned, not a content digest read
  from a running replica. *Not verifiable offline:* that Azure echoes a `repo@sha256:…` reference unchanged (expected; the Azure test shows it).

## 7. When IQ runs, how it fails, the order

- **`jd release deploy`** runs IQ after all workloads deployed successfully, then writes the one record with the IQ result. If a workload
  failed, IQ is `notRun` and the outcome `failed`.
- **`jd release qualify <set> --env --catalog`** (new) re-checks without deploying: it resolves like deploy, runs the same IQ and writes a `qualify`
  record (no steps, no OQ). It never calls `DeployAsync`.
- **A deploy must not hide drift:** a deploy reconciles by design (refresh, then up), so drift present before it is corrected by it and a
  post-deploy IQ cannot see it. That is recorded, not silent: the node reports in the record show `deployed` with the change counts instead of
  `unchanged`. The independent drift detector is `qualify`, which cannot fix what it finds. The deploy does not run IQ first; a "refuse to
  deploy over drift" gate is a policy, not MVP.
- **Outcome semantics:** IQ failure makes `deploy` and `qualify` exit **1**, lists every failed check (checks are all run, none short-circuits), and
  leaves the deployment as it is (no rollback; E31). The record says `outcome: succeeded`, `iq.status: failed`: *unqualified*, which S19 derives as in
  [approvals.md](approvals.md) section 5. A later `qualify` or deploy that passes writes a new record; nothing is edited.
- **Cost:** IQ re-previews every node (a refresh each), so a deploy takes roughly one more walk. Accepted for the MVP; measure in S17c. Its time is recorded apart (`iq.elapsedSeconds`), not in the deploy time.
- **The Azure test mismatch** (last step of `EnvUpAzureTests`, after all other assertions; same environment, no new account): on the
  worker's app, `az containerapp update --image <another publicly pullable image> --revision-suffix oob1`, then `az containerapp ingress traffic set
  --revision-weight <app>--oob1=100`. `jd release qualify` then exits 1 and reports both the drift on the runtime node and the image check
  (`pinned …@sha256:…, live <other> on revision <app>--oob1`); the `qualify` record has `iq.status: failed`. The Azure test must assert the image-check line itself, not only the drift line. The offline tests prove the image
  check alone (clean preview, different image). *Unverified:* that the revision can be created and given traffic if the other image does not serve
  on the app's port (`ingress update --target-port` fixes it); and what weight the new revision gets by default (hence the explicit `traffic set`).

## 8. Split into slices (estimates are production code lines, not tests or docs)

| Slice | Contents | Est. |
|---|---|---|
| **S17a identity** | `IDeployerIdentity` (in `jd.core`), `jd.azure` project (credential from `ARM_*`, token claims), standards sections 1 and 3 amended, boundary check, composition-root seam | ~65 |
| **S17b record + OQ + timings** | `JD_RECORDS_DIR`, `ReleaseRecordFile` (write-once store and `set.yaml` check, in `jd.resolver` beside `ReleaseSetFile`), `ReleaseRecordBuilder` (in `jd.orchestrator`, builds the JSON from the runs), schema, `StepReport`/`WorkloadRun` timings and attempts, set bytes and hash, `release deploy` wiring | ~220 |
| **S17c1 IQ + qualify** | grants preview, `resourceIdOutput`, `ILiveRevisions` (in `jd.core`) and adapter, `Qualifier`, `release qualify` and its record, the `jd.azure` allowlist check | ~200 |
| **S17c2 IQ in deploy** (built) | IQ inside `jd release deploy` after all workloads deployed (`notRun` when one failed), deploy time excluding IQ, exit 1 with `succeeded` + `failed` | ~65 |

The total (~550) is one slice's worth of work three times over (S17c, estimated ~265, was split in two); each part stands alone and keeps the gate green.

**Required boundary tests**

- *S17a:* claims decoded from a hand-built token (`tid`, `appid`, `oid`); a missing claim or a malformed token fails clearly; missing `ARM_*`
  names the variables and never echoes the secret. Azure (no resources): identity `clientId`/`tenantId` equal `ARM_CLIENT_ID`/`ARM_TENANT_ID`.
- *S17b:* golden record validated against the schema; the file is never opened for write twice (existing path fails, a re-run adds a file and leaves
  the old bytes unchanged); label or environment with `..` or a separator rejected; no secret (output, config value, changed property) in the text;
  failed run writes outcome `failed`, failing node and not-started workloads; preview, usage and resolution errors write nothing; missing
  `JD_RECORDS_DIR` stops before the backend is called; an identity failure (bad credential) stops before the backend is called and creates no
  `set.yaml`; `release qualify` creates a missing `set.yaml` and checks an existing one; deploy time excludes the IQ walk; OQ `passed`/`failed`/`unchanged`/`notRun`; identity equals the port's value, whatever the
  definition says; `setSha256` equals the SHA-256 of `set.yaml`, which holds the set's exact bytes; an identical re-run reuses `set.yaml`; different
  bytes under the same label are refused before the backend is called; `set.yaml` is never overwritten; step elapsed within the node's. Azure: in `EnvUpAzureTests`, the good, bad and revert
  deploys each leave a record (outcomes `succeeded`, `failed`, `succeeded`; the bad one has OQ `failed`) and identity equals `ARM_CLIENT_ID`.
- *S17c1:* drift (fake preview reports an update, or a create for a removed node) fails IQ; a drifted grant node fails it; `PendingUpstream` fails
  it; digest mismatch with a clean preview fails it; the latest revision wrong but the live one right passes, the reverse fails; no revision with
  traffic fails; a reader error (app gone) is a failed check, not a crash; `qualify` makes zero `DeployAsync` calls; the failure
  text lists all failed checks. Azure: the mismatch of section 7. *S17c2:* in a deploy every `DeployAsync` precedes every IQ preview, and a failed deploy gives
  `notRun`; IQ failure exits 1 and the record reads `succeeded` + `failed`.

## Decisions (lead, pending the user's review)

1. `jd deploy <workload>` writes no record; a release is the unit. It is the unrecorded development path. Success criterion 7 is met by release sets.
2. `JD_RECORDS_DIR` is required for `release deploy` (not `--preview`) and `release qualify`.
3. A new adapter project `jd.azure`; the package `Azure.ResourceManager.AppContainers` arrives in S17c1 (S17a needs only Azure.Identity, already pinned);
   standards section 3 is amended in S17a.
4. No deviation from ADR 0007: the record directory keeps the set file's bytes (`set.yaml`, section 1); records reference it by `setSha256`.
5. IQ failure exits 1 and leaves the deployment live (no rollback; E31, E32).
