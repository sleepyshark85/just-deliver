# Approvals and the protected tier

> Status: design note for S18 (MVP); nothing is built. Addresses [F54](../open-questions.md) for the MVP (F37, the full approval model, stays open);
> S19 (`jd promote`) consumes it. Builds on [release-record.md](release-record.md) (records, `set.yaml`, identity, "qualified"),
> [release.md](release.md) and [ADR 0008](../decisions/0008-enforcement-in-landing-zone.md) (stage and production are platform-owned shared
> environments), [ADR 0010](../decisions/0010-replacement-protection-classes.md).

A set reaches `stage` only when it is **qualified** on the environment below (IQ and OQ passed there) **and approved** for `stage` by people the
policy of `stage` allows. This note decides the `stage` substrate, what "protected" means, where the policy lives, the approval record, and
the exact rule S19 evaluates. Approvals use the same local, write-once evidence as release records and carry the same trust boundary: the tool
protects against mistakes and against edits of what was approved, not against someone who can write the records directory (section 9).

## 1. The `stage` substrate on the free tier

`stage` is one more environment on top of the shared substrate, brought up like `dev` (`jd env up samples/environments/stage.yaml --catalog catalog
--base shared.env.yaml --out stage.env.yaml`). **No catalog change is needed:** the mapping `env-substrate/standard` has no tier in its `match`,
so `stage` (tier `protected`) resolves to the same mapping as `dev`. The A4 candidate (identical policy set across tiers; scale may differ) holds
trivially; per-tier SKUs are out of the MVP.

| Part | `stage` | Shared with `dev`? |
|---|---|---|
| Cosmos DB account | the one free-tier account of the shared tier | **shared** (Azure allows one per subscription) |
| Cosmos DB database | its own: `db-stage`, 400 RU/s shared by its containers (`naming.yaml` `db`: `db-{env}`) | own |
| Container Apps environment, resource group | its own (`cae-stage-…`, `rg-stage-…`), Consumption profile | own |
| Log Analytics workspace | the shared one (its 0.15 GB/day cap) | **shared** |
| App Insights, containers, grants | per workload, as on `dev` | own (per workload) |

- **Own database, not own container.** A container is the workload's (`cosmos-container` naming); the database is the environment's. Workload
  grants are scoped to one container (the `database` mapping), and a database per environment keeps `stage` data and throughput apart from `dev`
  by construction.
- **RU/s:** 400 (`dev`) + 400 (`stage`) = 800 of the free 1,000; the mapping comment already says "two environments use 800". Azure itself refuses
  more than the free total because the account is created with `totalThroughputLimit: 1000`. A third environment would need 1,200 and is refused
  by Azure; it needs a smaller database throughput (400 is Cosmos's minimum for a provisioned database, *Unverified*) and is out of the MVP.
- *Verified* (existing `EnvUpAzureTests`): the account's `totalThroughputLimit` is 1000; the `dev` database has 400; workload containers have no
  throughput of their own; the workspace cap is 0.15 GB/day. *Verified by reading:* the sandbox policy allows Container Apps environments on the
  Consumption profile only ([tools/sandbox](../../tools/sandbox/README.md)), which the mapping's template uses.
- *Unverified* (the S18a Azure test shows them): a second `db-stage` of 400 RU/s fits the free tier on the same account (the cap says yes);
  a second Container Apps environment in the subscription and region is not refused by a quota; two environments logging to one workspace stay
  under its cap or are dropped past it, which costs nothing.
- **Same subscription, same region (`JD_REGION`).** Real stage is platform-owned and in its own subscription (ADR 0008, landing-zone); the sandbox
  has one subscription and one free Cosmos account, so the MVP shares it. Stated, not hidden.
- **Teardown.** The test's cleanup deletes the environment groups before the shared one (their Container Apps environments log to the shared
  workspace). It currently puts only the `dev` group first; S18a changes that to "every group except the shared one first".

## 2. "Protected" tier

`tier: protected` already exists in the environment schemas and in mapping and policy matching. S18 gives it behaviour in the CLI:

- **A direct deploy is refused.** `jd release deploy` and `jd deploy` on an environment whose descriptor says `protected` exit 1 and say
  "protected environment: use `jd promote`" (S19), **before** the identity, the records directory or the backend is touched. `--preview` (nothing is
  created) is allowed, and so is `jd release qualify` (read-only; S19's test needs a qualify record on `stage`). `jd env up` is allowed: the
  substrate is not a release (a gate on substrate changes is ADR 0010's plan-content gate, not MVP).
- **Only `jd promote` deploys there.** S19 calls the same release walk after it has evaluated the rule of section 5. The tier is read from the
  descriptor, one named constant for the closed set the schema already enumerates (`team`, `protected`), not a literal scattered in the CLI.
- **`protect: true` does not change with the tier.** ADR 0010 makes protection uniform, dev included, so "protected" adds nothing there (and the
  catalog does not yet set `protect` at all in the MVP). The Azure-side `CanNotDelete` lock that ADR 0008 calls a genuine control in stage is **out**:
  its scope needs care (ADR 0010) and it would block the test teardown.
- **"Who may deploy" is not enforced by the tool.** ADR 0008: the platform is the paved road, not the fence. The fence is Azure RBAC on the stage
  subscription, which the sandbox cannot show (one principal). The MVP guarantee is: through `jd`, nothing reaches `stage` unqualified and
  unapproved. Someone who edits a descriptor's tier to `team`, or deploys with `pulumi` directly, is outside it (section 9).

## 3. Approval policy as data

**Where:** a block `approval` in the **environment definition**, copied verbatim into the **descriptor** by `jd env up`, so `--env stage.env.yaml`
carries it and no command needs another input. It is not an expression: the leaves are literals.

```yaml
kind: EnvironmentDefinition
name: stage
tier: protected
requires: [{ type: env-substrate, id: substrate }]
values: { ... as dev ... }
approval:
  qualifiedOn: dev                      # the environment whose records must show the set qualified
  required: 1                           # distinct approvers needed
  approvers: ["00000000-0000-0000-0000-000000000000"]   # Entra object ids that may approve
  separateFromDeployer: true            # an approver may not be a principal that deployed this label on qualifiedOn
```

- **Schema and rules** (`environment-definition` and `environment` schemas, the loader for what the schema cannot say): `qualifiedOn` has the
  environment-name pattern and is not the environment's own name; `required` is an integer >= 1 and <= `approvers.length` (a policy that can never
  be met is an error at `env up`); `approvers` are unique GUIDs, at least one; `separateFromDeployer` is **required**, not defaulted (a silent
  default would be a silent weakening). **Tier rule:** `protected` requires `approval`, `team` forbids it. That is the "policy per tier": the
  tier decides whether there is a policy, the environment definition holds its values.
- **Evaluation** is two pure functions in `jd.resolver` (no I/O, golden-tested), over the policy, the distinct approvals and the deployers:
  `Counts(approver, deployers)` (the principal is listed in `approvers`, and is not in `deployers` when `separateFromDeployer`) and
  `Evaluate(approvals, deployers)` (the number of distinct `objectId`s among approvals that `Counts`, against `required`). `jd approve` uses `Counts`
  to refuse early; S19 uses `Evaluate` on the records, so the same rule is applied twice from the same code.
- **Deployers** are the distinct `identity.objectId` of every `deploy` record of the label on `qualifiedOn` (failed attempts included: someone
  who took part in deploying it cannot approve it). `qualify` records do not count.
- **Why not the catalog.** The catalog is shared across subscriptions and tenants; the names of environments and Entra object ids are
  per-installation data. The catalog keeps what is true everywhere. *Option put to the user* (decisions, 1).
- **The sample** `samples/environments/stage.yaml` ships with the all-zero GUID, which matches no principal: safe until the operator edits it.
  The Azure test substitutes the sandbox principal's object id, read from a `dev` deploy record.
- **Out of MVP:** roles and groups (F54's "required roles"; needs Graph), ordering of approvers, expiry, delegation, a policy on `qualify`.

## 4. `jd approve <label> --env <stage.env.yaml> --meaning <text>`

`--env` is the **descriptor file**, like `release deploy` and `release qualify` (the brief's `--env stage` reads as the environment; a name would
need another configuration for the descriptor's directory, so the file is the simplest, decisions 5).

**The approval record** is the `approve` activity of the store of [release-record.md](release-record.md) section 1: JSON, `kind: ApprovalRecord`,
`schemas/approval-record.schema.json`, `additionalProperties: false`, a committed golden validated against it.
File: `<JD_RECORDS_DIR>/<target env>/<label>/<UTC yyyyMMddTHHmmssfffZ>-approve.json`, created with `FileMode.CreateNew` through the
same store (never opened for write twice; a name collision is an error that says to run again). It sits in the **target** environment's
directory, where S19 looks; the set is read from the source environment's directory.

```json
{ "kind": "ApprovalRecord", "approvedAt": "2026-10-17T03:04:05Z",
  "environment": { "name": "stage", "tier": "protected" },
  "release": { "label": "1.0.0", "setSha256": "<sha256 of dev/1.0.0/set.yaml>" },
  "qualifiedOn": "dev",
  "identity": { "tenantId": "...", "clientId": "...", "objectId": "..." },
  "meaning": "Reviewed the test evidence; ready for stage." }
```

| Field | Source |
|---|---|
| `approvedAt` | the injected `TimeProvider` (to the second, UTC) |
| `identity` | `IDeployerIdentity` (S17a), the token claims; never a flag, file, definition or set value. The port keeps its name (one port, two uses) |
| `release.setSha256` | SHA-256 of the exact bytes of `<records>/<qualifiedOn>/<label>/set.yaml`; **the approval binds the set, not a record** |
| `environment`, `qualifiedOn` | the descriptor (the target, its policy) |
| `meaning` | the argument, verbatim |

- **`--meaning`** is what the approver asserts by approving (the "meaning of the signature" in F54 / 21 CFR Part 11: who, when, what it means).
  It is required, 1 to 500 characters, with no control characters (so it cannot break a log line or the JSON, which is written by the serializer,
  never by string building). It is recorded and printed and **never interpreted**: no logic reads it, so it cannot approve, loosen or select
  anything. A vocabulary of meanings per policy is out of the MVP.
- **Why the record does not pin a qualifying record.** release-record.md section 1 sketched an approval that names a record file and its hash. It is
  not needed: S19 derives "qualified" **now** from the current files (section 5), so a record edited after the approval is re-judged, and a pin
  would prove only that a file was unedited, which the derivation already needs. The approval binds the set; the evidence is re-read when it is used.
  (Decisions, 3; section 1's sentence should be aligned when S17c2 is merged.)
- **Order** (as a recorded `release deploy`): parse arguments and the label; load the descriptor; `JD_RECORDS_DIR`; the identity (a bad credential
  stops here); the source set and the qualification; the policy; the repeat check; then the one write. Nothing is written on any refusal.
- **Refusals** (exit 1 with a message that names the cause; usage errors, exit 2, are a missing or empty `--meaning`, a bad label, a missing
  `JD_RECORDS_DIR`, an unreadable `--env`):
  1. the descriptor has no approval policy (a `team` environment is not approved);
  2. **unknown label**: `<records>/<qualifiedOn>/<label>/set.yaml` does not exist;
  3. **not qualified** on `qualifiedOn` (section 5), with every reason listed;
  4. the approver is not in `approvers`;
  5. `separateFromDeployer` and the approver deployed this label on `qualifiedOn`;
  6. **repeated**: this `objectId` already has a valid approval of this set on this environment.
- **A repeat is a UX refusal, not a lock.** Two simultaneous runs by one principal could both write; S19 counts distinct principals, so a duplicate
  file never counts twice. Approvals are never revoked in the MVP; a withdrawn release is a new label (a rejection record is out of the MVP).
- Prints `approved <label> for <env> (<n> of <required>)` and `record: <path>`.

## 5. What S19 reads

`jd promote <label> --to <stage.env.yaml> --catalog <dir>` has no set argument: the set is the **bytes the platform kept** on the source
environment (ADR 0007), `<records>/<qualifiedOn>/<label>/set.yaml`, which the deploy to `stage` writes unchanged as `stage`'s own `set.yaml`
(its existing check refuses any other set under the label). Same bytes, so same digests and SHAs. With policy P, target T, source S = `P.qualifiedOn`,
label L:

1. `h` = SHA-256 of `<records>/S/L/set.yaml`. Missing: unknown label.
2. **Qualified** (derived now, never stored; one function, `jd.resolver/release`, shared with `jd approve`). Read every file in `<records>/S/L/` named
   `<time>-deploy.json` or `<time>-qualify.json`. **Each must be consistent with where it is, or the whole answer is "not qualified" with the file named:**
   valid against `release-record.schema.json`; `activity` equals the name's suffix; `environment.name` is S; `release.label` is L;
   `release.setSha256` is `h`; `finishedAt` equals the name's time to the second. (A record copied from another label or environment, renamed to a later
   time, or edited to another set is caught here. A file that is merely absent is not caught: that is the boundary of section 9.)
   Then: the newest `deploy` record has `outcome: succeeded` and every workload's `oq.status` is `passed` or `unchanged`, an `unchanged` counting only
   when an earlier record in the directory has `passed` for that workload; and the newest record that carries an `iq` is **not older than** the newest
   `deploy` record and has `iq.status: passed`. (This tightens release-record.md section 1 for records written before S17c2, which have no `iq`: they are
   never qualified; an old IQ cannot vouch for a newer deploy.)
3. **Approved:** the files `<time>-approve.json` in `<records>/T/L/` that are valid against `approval-record.schema.json` with `environment.name`
   T, `release.label` L and `release.setSha256` `h`. An approval that does not match is **ignored and reported** (it can only add, so ignoring is
   safe; ignoring a bad *qualifying* record would hide a failure, so those fail closed instead).
4. `Evaluate(P, approvals, deployers(S, L))` is true: the number of distinct `identity.objectId` among approvals that satisfy `Counts` is at least `required`.
5. **Promote** only when 2 and 4 hold, then run the release walk of the kept set on T. Otherwise exit 1 listing every reason, writing nothing.

Qualification and approval are judged at promotion time from the current files, so a drift found by a later `qualify` on S, a failed re-deploy on S, or
an edit of `set.yaml` after approving each withdraw the right to promote without anyone revoking anything.

## 6. Slices (production code lines, not tests, schemas or docs)

| Slice | Contents | Est. |
|---|---|---|
| **S18a policy, tier, stage** | the `approval` block (definition and descriptor schemas, record, parser, composer, tier rule), `ApprovalPolicy.Counts`/`Evaluate`, the `protected` refusal in `release deploy` and `deploy`, `samples/environments/stage.yaml`, usage.md; Azure test: `stage` up (section 1) and a refused release deploy | ~120 |
| **S18b qualification** | the reader of a label's records: consistency checks, the qualified rule of section 5 step 2, the deployers, reasons; no CLI | ~120 |
| **S18c approve** | `ApprovalRecord` (builder, schema, write through the store, reader for S19), `jd approve` (arguments, order, refusals, output); Azure test: the approve flow on real records | ~150 |

~390 in total; each part stands alone and keeps the gate green. S18b has no consumer until S18c, which is acceptable because its tests are the
point; S18a can be built in parallel with S17c2, S18b needs S17c1 (the `iq` field) and S18c needs S18a and S18b.

## 7. Required tests

Security boundaries are tests, each shown to fail when the guard is removed (standards, mutation proof).

- **S18a:** policy golden (descriptor round trip); `protected` without `approval` and `team` with it are errors; `required` > approvers, `qualifiedOn` = own name,
  a non-GUID, a missing `separateFromDeployer` are errors; `Counts`: an unlisted principal does not count, a deployer does not count when separation is
  on and does when off; `Evaluate`: the same principal twice counts once, `required: 2` needs two. **`release deploy` and `deploy` on a protected
  descriptor exit 1 with zero backend calls, no identity call and no record**; `--preview` and `qualify` are not refused; on a `team` descriptor
  nothing changes. Azure: section 8.
- **S18b (forgery by editing files):** a record copied from another label, from another environment, with another `setSha256`, with a renamed
  (later) name, with an `activity` that disagrees with its name, or failing the schema makes the label **unqualified** (not ignored); a newest failed
  record beats an older passing one; `set.yaml` edited after the records is unqualified; OQ `unchanged` without an earlier `passed` is unqualified;
  an old IQ before a newer deploy, no `iq` at all, `iq: notRun` and `failed` are unqualified; an unreadable file is a reason, not a crash.
- **S18c:**
  - *Forging by editing a file:* an approval whose `setSha256` was edited, which sits in another label's or environment's directory, or that fails the
    schema (extra property, missing identity) does not count; `set.yaml` edited after the approval makes it not count; an approval file is never opened for write
    twice (an existing path fails, the old bytes stay).
  - *By a definition or a set:* text in a definition, set or `--meaning` that looks like an identity, a policy or JSON (`","identity":{…}`) cannot change
    the record: `identity` equals the port's value, the policy is the descriptor's, and the meaning appears only as the string; control characters in
    `--meaning` and a path in the label are rejected.
  - *The deployer approving their own set:* refused when `separateFromDeployer`, accepted when not; a hand-placed approval by a deployer does not
    count in `Evaluate` when separation is on; an unlisted approver is refused and a hand-placed approval by one does not count.
  - Refusals 1 to 6 of section 4 each write nothing; a bad credential stops before the records are read; a repeat by the same principal is refused and a second principal is
    accepted with `required: 2`; the golden approval validates against its schema; nothing is written on a refusal.

## 8. Azure tests

Both are steps appended to `EnvUpAzureTests` (one scenario, because of the one free-tier account), no resources beyond section 1.

- **S18a:** `shared`, `dev` and `stage` up; the `stage` descriptor says `protected` and carries the policy; `db-stage` has 400 RU/s, the account limit is 1000, and no
  workload container has its own throughput; running `stage` again changes nothing; `jd release deploy` of the sample set on `stage` exits 1 and the `stage`
  group holds no Container App. Cleanup deletes both environment groups before the shared one.
- **S18c:** after the existing deploy and qualify on `dev` (a record with `iq` passed): `approve` of an unknown label is refused; with the strict `stage` descriptor
  (the sandbox principal listed, `separateFromDeployer: true`) it is refused because the same principal deployed the set; with a relaxed copy it succeeds, the file's
  `identity.objectId` equals the `dev` deploy record's and `setSha256` equals the hash of `dev`'s `set.yaml`; a repeat is refused. After the existing out-of-band image mismatch step
  (the last step) a new `approve` is refused as not qualified: approval follows the current evidence. (Two principals are not available in the sandbox; separation between
  different principals is proved offline.)

## 9. Out of the MVP, and the trust boundary

- **No tamper-proofing against someone who can write the records directory.** Records and approvals are local files, like release records (E52); the deployer who can write
  `<records>/dev` can also write `<records>/stage`. A forged qualifying record, a whole forged approval with a consistent hash, or a deleted failed record is not detected.
  The remedy is the audit store (F39) with separate write access for approvals and signing; the formats here (write-once, hashes, identity from the token) are what that store would keep.
- No Entra groups or roles, no PIM, no notifications, no expiry, no revocation or rejection record, no approval of a set that was qualified elsewhere than `qualifiedOn`,
  no per-tier SKUs, no separate subscription for stage, no Azure lock, no gate on substrate changes.

## Open choices for the user

1. **Where the approval policy lives.** (a, recommended) the environment definition and descriptor, as in section 3: one file per environment carries it, and per-installation
   data (environment names, Entra object ids) stays out of a catalog shared across subscriptions. (b) A catalog policy per tier (`kind: ApprovalPolicy`, `match: { tier: protected }`) for the
   shape (`required`, `separateFromDeployer`), with `qualifiedOn` and `approvers` still in the environment definition: more literal to "policy per tier" and platform-team owned, but two places and
   a new catalog kind (~60 more lines). The rest of the note holds under either.
2. **Who may approve.** (a, recommended) A listed set of Entra object ids in the policy. (b) Any principal that authenticates, with only the separation rule: no list to maintain, but then
   "approval" is a record of who clicked, not of who was allowed. Roles or groups (F54) need Graph and are out either way.
3. **Separation default for the sample `stage`.** The sample sets `separateFromDeployer: true`, so the sandbox's single principal cannot approve its own set; the Azure test uses a relaxed copy.
   Recommended: keep the strict sample, since that is the property the user will want to see.

## Decisions (lead, pending the user's review)

1. The approval policy lives in the environment definition and descriptor, not the catalog; the tier decides whether it exists (section 3).
2. `protected` means: no direct `release deploy` or `deploy` through `jd`; `promote` is the only deploy path; protection is by the paved road, not by RBAC (ADR 0008).
3. An approval binds the set (`setSha256`), not a qualifying record; "qualified" is judged when it is used, and fails closed on any inconsistent record (sections 4 and 5).
4. Approvers are listed Entra object ids; separation from the deployer is an explicit policy field, with the deployer being any principal that ran a `deploy` of the label on the source environment.
5. `--env` is the descriptor file, as in the other release commands; `stage` shares the subscription, the Cosmos account and the workspace with `dev` and has its own database and Container Apps environment.
6. Approvals are never revoked; a repeat by the same principal is refused; the number of approvals beyond `required` is not limited.
