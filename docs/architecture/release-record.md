# Release record and qualification

A **release record** is the local evidence of one attempt to put a release set ([release.md](release.md)) on one environment: what was
pinned, what ran, how long it took, who ran it, and the two verdicts, OQ (operation) and IQ (installation). Nothing in it is a secret and
nothing in it is edited after it is written. Addresses [E52](../open-questions.md); the audit store (F39) is out of scope. Commands and
their output: [usage.md](../usage.md#releases). How records decide "qualified" and "approved": [approvals.md](approvals.md) §5.

## 1. Where and how it is stored

- **Store:** the port `IRecordStore` (`jd.core`); its adapter `LocalRecordStore` writes under `<data>/records` in jd's data directory
  ([usage.md](../usage.md#data-and-identity)). Not in the descriptor: the descriptor describes the environment, not where evidence is kept.
- **One file per attempt:** `<records>/<environment>/<label>/<UTC yyyyMMddTHHmmssfffZ>-<activity>.json`, activity `deploy` or `qualify`
  (approvals are `-approve.json` files of the same store, [approvals.md](approvals.md) §4). The store refuses a segment that is empty or holds
  a separator or `..`.
- **Write-once:** every file is created with `FileMode.CreateNew`, so an existing path is an error and nothing is opened for write twice. The
  time in the name is the finish time, so the newest file is the last attempt to complete. A failed run writes one too; a preview, a usage
  error, a resolution error or a killed process writes none.
- **Format:** JSON, `kind: ReleaseRecord`, contract [`release-record.schema.json`](../../schemas/release-record.schema.json), pinned by a golden
  record validated against it. A deploy record requires `outcome`, `deploySeconds`, `notStarted`, `iq` and each workload's `oq`; a qualify record
  requires `iq`.
- **The set is kept beside the records** (ADR 0007): `<records>/<environment>/<label>/set.yaml` holds the set file's exact bytes, created on the
  label's first recorded attempt and never overwritten. Every later attempt must bring a set that hashes equal, or it is refused before anything
  is deployed. Records refer to it by `setSha256`. *Residual:* it is written in place, so a crash mid-write leaves a truncated `set.yaml` that
  the next attempt reports as a different set; delete it by hand.
- **Order of a recorded run** (`release deploy`, `release qualify`, `promote`): load and resolve everything, check the templates; name the
  principal; keep or compare `set.yaml`; build the backend, orchestrator and qualifier together; then the first backend call. A bad
  credential or a different set therefore stops the run before anything is deployed.
- **`jd deploy <workload>` writes no record:** it has no label; the release is the unit that is deployed and promoted.

## 2. Contents

The writer is an allowlist; the schema is the field reference. A record holds:

- the activity, `startedAt` and `finishedAt` (UTC, to the second, from the injected `TimeProvider`);
- the release: label, the set's `createdAt` and `setSha256`;
- the environment: name, type, region, and the catalog version;
- per workload, in deploy order: name, team, definition SHA-256, `dependsOn`, the digest-pinned image; its `graph`: the `sha256` of the graph's stable JSON and
  the nodes as `{id, template, stack, phase, hash}`; its run (absent when not started): elapsed, and per node outcome, change **counts**, message,
  elapsed and, for the runtime, the release steps with outcome, detail, elapsed and attempts; its `oq` (§4);
- the outcome `succeeded` or `failed`, the failing workload and node, the workloads not started;
- `identity` (§3), `deploySeconds` (the deploy walk alone) and `iq` (§5, §6) with its own `elapsedSeconds`.

- **Graphs as hashes, not config.** Node hashes are deterministic, so anyone can re-resolve the set on the environment and compare, and no config
  reaches the record. The definition text is stored once, in `set.yaml`.
- **Secrets cannot reach the record, by construction.** The writer never reads a node's outputs or per-property changes (which may carry secret
  values). The remaining free text is step details and node messages (revision names, `HTTP 503`, provider or exception text); Pulumi masks
  the secrets it knows, and jd adds no scrubbing: that is the residual risk. A test pins that a secret output, config value or changed value
  never appears.
- **Timings:** a workload's node times cannot be summed, because the grants run inside the runtime's grants step.

## 3. Identity and the trust boundary

`identity` is `{directory, principal, name}` ([ADR 0022](../decisions/0022-neutral-deployer-identity.md)): `principal` is the stable key and
the only field a rule compares. On Azure they are the `tid`, `oid` and `appid` claims of an ARM access token obtained in this run through the port
`IDeployerIdentity` (`jd.core`), adapter `ArmDeployerIdentity` in `jd.azure`. Records written before carry `{tenantId, clientId, objectId}`; the
schemas keep that shape as read, never written, and the readers (`RecordIdentity`) take it as directory, name and principal. The credential is `ArmCredentials`: the service principal in
`ARM_TENANT_ID`, `ARM_CLIENT_ID` and `ARM_CLIENT_SECRET` when all three are set, else `AzureCliCredential`, the choice Pulumi's provider makes
too; a partial set is refused, naming the unset variables. A credential Entra refuses becomes a message that carries neither the SDK's text
nor any value, and the run stops before the backend is called.

Reading the claims without validating the signature is sound: the token came straight from Entra over TLS in this call. No definition,
catalog, descriptor, set or stack output flows into the identity; the record builder takes the port's result as an argument.

**Trust boundary.** A person who controls the process environment or can write the records directory controls what the records say.
Records are not signed: an edit to another valid value, a whole forged record with a consistent hash, or a deleted record is not detected.
What is detected is in [approvals.md](approvals.md) §5; the remedy is the audit store (F39).

## 4. OQ

From the runtime node's release steps, no new probing. Per workload `oq.status` is `passed` (probe done), `failed` (probe failed; reason and
attempts in the detail), `unchanged` (probe skipped: the revision is already live, which only an earlier passed probe and shift could make
it) or `notRun` (the dark or grants step failed, or the workload was not started). The probe contract (`path`, `expectedStatus`) comes from the
graph node, which is catalog data.

## 5. IQ: no drift

The preview walk (`Orchestrator.DeployReleaseAsync` with preview), which refreshes and then previews, over **every node of every workload**, the
grants included: a node with any change operation, `PendingUpstream` or `Failed` fails its `drift` check. A persisted refresh does not absorb
drift (verified offline with Pulumi's file backend): the diff against the program stays visible on every later preview. The substrate
(`jd env up`) is not part of a release and is not checked.

## 6. IQ: running digests

Observed from Azure, never from state or the record, through the port `ILiveRevisions` (`jd.core`), adapter `ArmLiveRevisions` over the same
credential as the identity. Given the runtime's resource id (the runtime's `resourceIdOutput`, read from state), it returns the revisions that
hold traffic, each with name, weight and images. The check: every image of every revision with weight > 0 equals the workload's pinned image
(ordinal), and at least one revision holds traffic. Traffic weight is what serves, so a failed dark revision that is the latest does not fail
it, and an old live one with another image does.

The `Qualifier` (`jd.orchestrator`) previews each workload on its own, so one failure does not hide another; turns every node into a `drift`
check and every serving revision into an `image` check; and makes a reader or state error a failed check. A node the walk never reached is a
failed check ("not checked"), never a silent pass. `iq` is `{status, elapsedSeconds, checks[]}`, each check `{workload, kind, subject, status,
detail}`.

*Limit:* Azure reports the image reference configured on the revision, the digest jd pinned, not a digest read from a running replica.

## 7. When IQ runs and how it fails

- `jd release deploy` runs IQ after every workload deployed; if one failed, `iq.status` is `notRun` and the outcome `failed`.
- `jd release qualify` runs the same IQ without deploying (it never calls `DeployAsync`) and writes a `qualify` record (no run, no OQ).
- A deploy reconciles by design, so drift present before it is corrected and a post-deploy IQ cannot see it. That shows in the record as
  `deployed` nodes with change counts instead of `unchanged`. The independent drift detector is `qualify`.
- IQ failure exits 1, runs every check, and leaves the deployment as it is (no rollback; E31). The record reads `outcome: succeeded`,
  `iq.status: failed`; a later passing `qualify` or deploy writes a new record.
- IQ re-previews every node, so a deploy takes roughly one more walk; its time is `iq.elapsedSeconds`, apart from `deploySeconds`.
