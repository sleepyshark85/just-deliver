# Approvals and the protected tier

> Status: design note for S18 (MVP). **S18a built** (the policy, the gate, `stage`); **S18b built** (the qualification reader, `jd.resolver/release/QualificationReader`); **S18c built** (the approval record and `jd approve`); **S19a built** (the approval reader and the decision `PromotionCheck`, no CLI); S19b (`jd promote`) is not. Addresses [F54](../open-questions.md) for the MVP (F37, the full approval model, stays open);
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
- **Container Apps grant.** From S19 on, `stage` runs the same workloads as `dev`; their apps share the per-subscription monthly Container Apps free grant with `dev`'s
  ([standards](../engineering/standards.md) section 6: Consumption only, `minReplicas: 0`, smallest size), so the grant is split across both environments (*Unverified* that a test run stays far below it).
- **Same subscription, same region (`JD_REGION`).** Real stage is platform-owned and in its own subscription (ADR 0008, landing-zone); the sandbox
  has one subscription and one free Cosmos account, so the MVP shares it. Stated, not hidden.
- **Teardown.** The test's cleanup deletes the environment groups before the shared one (their Container Apps environments log to the shared
  workspace). It currently puts only the `dev` group first; S18a changes that to "every group except the shared one first".

## 2. "Protected": a descriptor with an approval policy

The gate is **data-driven: an environment whose descriptor carries an `approval` block (section 3) accepts deploys only through `jd promote`.** The CLI
tests for the block, never for a tier name; there is no tier constant and no tier rule in C#. `tier: protected` stays what it is today, a value
the schemas enumerate and mappings and policies match on; the only coupling is in the schemas: **if `tier` is `protected` the definition (and the
descriptor) must carry `approval`**, so a protected environment cannot be written without a gate. (A `team` environment may carry one too and is then
gated the same way; nothing forbids it.)

- **Direct deploys are refused.** `jd release deploy`, `jd deploy` **and `jd release qualify`** on a descriptor with an `approval` block exit 1 and say
  "this environment is gated by an approval policy: use `jd promote`" (S19), **before** the identity, the records directory or the backend is touched.
  `qualify` is refused as well because it creates the label's `set.yaml` from whatever file it is given, and a gated environment's `set.yaml` must come
  only from the kept set of the source environment (section 5). mvp.md asks for no qualify on `stage`: S19's "records on both environments" are the deploy
  records `promote` writes, which carry IQ (S17c2). A qualify of a promoted set on `stage` through `promote`'s kept set is later work. `--preview`
  (nothing is created) is allowed. `jd env up` is allowed: the substrate is not a release (a gate on substrate changes is ADR 0010's plan-content gate, not MVP).
- **Only `jd promote` deploys there.** S19 calls the same release walk after it has evaluated the rule of section 5.
- **The principal running `jd promote` is not checked against `approvers`.** The approvers are the people who approved the set; whoever runs the
  promotion (a person or a pipeline) only has to find the set qualified and approved. Who may run it is Azure RBAC, not this tool.
- **`protect: true` does not change with the policy.** ADR 0010 makes protection uniform, dev included, so a gate adds nothing there (and the
  catalog does not yet set `protect` at all in the MVP). The Azure-side `CanNotDelete` lock that ADR 0008 calls a genuine control in stage is **out**:
  its scope needs care (ADR 0010) and it would block the test teardown.
- **Only the paved road is in the MVP.** Through `jd`, nothing reaches a gated environment unqualified and unapproved. The control that makes that
  true for everyone, RBAC on the stage subscription (ADR 0008), is out of the MVP, and the sandbox, with one principal, cannot show it. Someone who
  removes the `approval` block from a descriptor, or deploys with `pulumi` directly, is outside the guarantee (section 9).

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
  default would be a silent weakening). **Tier coupling, in the schemas only:** `tier: protected` requires `approval`, written as `"anyOf": [ { "properties": { "tier": { "enum": ["team"] } } }, { "required": ["approval"] } ]`
  (NJsonSchema 11.0.2 silently ignores `if`/`then`, so this form is used; no C# rule). That is the
  "policy per tier": the tier decides in the schema whether there must be a policy, the environment definition holds its values.
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
- **Out of MVP:** roles and groups (F54's "required roles"; needs Graph), ordering of approvers, expiry, delegation, a policy on `qualify`, a gate on `team` environments beyond what the block itself does.

## 4. `jd approve <label> --env <stage.env.yaml> --meaning <text>`

`--env` is the **descriptor file**, like `release deploy` and `release qualify` (the brief's `--env stage` reads as the environment; a name would
need another configuration for the descriptor's directory, so the file is the simplest, decisions 5).

**The approver is a service principal, not a person.** The identity comes from the S17a port, a `ClientSecretCredential` over `ARM_*`: an approver is
whoever holds a service principal's secret. `separateFromDeployer` therefore means something only when each approver and each deployer has **its own**
principal (one per person or role), and `approvers` lists principals. With the sandbox's one principal the property is proved offline and cannot be
shown against Azure, and a record says which principal approved, not which human (Open choices, 3).

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
| `environment`, `qualifiedOn` | the descriptor (the target, its policy); `qualifiedOn` is checked against the policy by S19 (section 5), not evidence only |
| `meaning` | the argument, verbatim |

- **`--meaning`** is what the approver asserts by approving (the "meaning of the signature" in F54 / 21 CFR Part 11: who, when, what it means).
  It is required, 1 to 500 characters, with no control characters (so it cannot break a log line or the JSON, which is written by the serializer,
  never by string building). It is recorded and printed and **never interpreted**: no logic reads it, so it cannot approve, loosen or select
  anything. A vocabulary of meanings per policy is out of the MVP.
- **Why the record does not pin a qualifying record.** An approval that names a record file and its hash is not needed: S19 derives "qualified" **now** from the current files (section 5), so a record edited after the approval is re-judged, and a pin
  would prove only that a file was unedited, which the derivation already needs. The approval binds the set; the evidence is re-read when it is used.
  (Decisions, 3.) release-record.md section 1 points here for both the derivation and the approval files.
- **Order** (as a recorded `release deploy`): parse arguments and the label; load the descriptor; `JD_RECORDS_DIR`; the identity (a bad credential
  stops here); the source set and the qualification; the policy; the repeat check; then the one write. Nothing is written on any refusal.
- **Refusals** (exit 1 with a message that names the cause; usage errors, exit 2, are a missing or empty `--meaning`, a bad label, a missing
  `JD_RECORDS_DIR`, an unreadable `--env`):
  1. the descriptor has no approval policy (the environment is not gated);
  2. **unknown label**: `<records>/<qualifiedOn>/<label>/set.yaml` does not exist (a `set.yaml` or directory that cannot be read is refused too, with that reason, not as unknown);
  3. **not qualified** on `qualifiedOn` (section 5), with every reason listed;
  4. the approver is not in `approvers`;
  5. `separateFromDeployer` and the approver deployed this label on `qualifiedOn`;
  6. **repeated**: this `objectId` already has an approval of this set on this environment. This reads the approval files through the one reader of section 5 step 3 (`ApprovalRecord.ReadAsync`, S19a); a file it ignores (unreadable, invalid, naming another environment, label, set or `qualifiedOn`) is not an earlier approval, and `jd approve` does not report it.
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
   `deploy` record and has `iq.status: passed`. (Records written before S17c2 have no `iq` and are never qualified; an old IQ cannot vouch for a newer deploy.)
3. **Approved:** the files `<time>-approve.json` in `<records>/T/L/` that are valid against `approval-record.schema.json` with `environment.name`
   T, `release.label` L, `release.setSha256` `h` and `qualifiedOn` equal to `P.qualifiedOn` (an approval given against another source environment does not count). An approval that does not match is **ignored and reported** (it can only add, so ignoring is
   safe; ignoring a bad *qualifying* record would hide a failure, so those fail closed instead).
4. `Evaluate(P, approvals, deployers(S, L))` is true: the number of distinct `identity.objectId` among approvals that satisfy `Counts` is at least `required`.
5. **Promote** only when 2 and 4 hold (the principal running the command is not checked, section 2), then run the release walk of the kept set on T. Otherwise exit 1 listing every reason, writing nothing.

Qualification and approval are judged at promotion time from the current files, so a drift found by a later `qualify` on S, a failed re-deploy on S, or
an edit of `set.yaml` after approving each withdraw the right to promote without anyone revoking anything.

## 6. Slices (production code lines, not tests, schemas or docs)

| Slice | Contents | Est. |
|---|---|---|
| **S18a policy, gate, stage** | the `approval` block (definition and descriptor schemas with the `protected` coupling, record, parser, composer), `ApprovalPolicy.Counts`/`Evaluate`, the refusal in `release deploy`, `release qualify` and `deploy`, `samples/environments/stage.yaml`, usage.md; Azure test: `stage` up (section 1) | ~110 |
| **S18b qualification** | the reader of a label's records: consistency checks, the qualified rule of section 5 step 2, the deployers, reasons; no CLI | ~120 |
| **S18c approve** | `ApprovalRecord` (builder, schema, write through the store), `jd approve` (arguments, order, refusals, output), and the minimal look at earlier approvals that refuses a repeat and counts (`ApprovalRecord.ApproversAsync`: same environment, set and `qualifiedOn`; replaced by the S19a reader); Azure test: the approve flow on real records | ~150 |
| **S19a approved** | the approval reader (`ApprovalRecord.ReadAsync`: the valid-approval filter of section 5 step 3, ignored files reported, with its tests: edited `setSha256`, another directory, a schema failure, an edited `set.yaml`, another `qualifiedOn`, hand-placed approvals) and `PromotionCheck.CheckAsync` (steps 1 to 4: `h`, qualified, `Evaluate`, every reason); `jd approve` reads earlier approvals through the reader; no CLI | ~100 |
| **S19b promote** | `jd promote` (arguments, order, output, the release walk of the kept set on the target), and the Azure test: a promote to `stage` | |

~430 in total; each part stands alone and keeps the gate green. **Split of S18c (taken):** the approval reader (the valid-approval filter of section 5 step 3) had no consumer until S19 and moved there with its tests (section 7); S18c keeps ~150 lines. S18b has no consumer until S18c, which is
acceptable because its tests are the point. S18a can be built in parallel with S17c2 **except for the Azure gate**: S18a edits `EnvUpAzureTests` (the `stage` steps and the cleanup order),
as S17c2 does, so their Azure runs are serial (one free-tier account, one lock) and the second to merge rebases. S18a needs S17c1 (it refuses `jd release qualify`, which S17c1 adds), S18b needs S17c1 (the `iq` field) and S18c needs S18a and S18b.

## 7. Required tests

Security boundaries are tests, each shown to fail when the guard is removed (standards, mutation proof).

- **S18a:** policy golden (descriptor round trip); a `protected` definition or descriptor without `approval` is an error (schema, the `anyOf` form), and a `team` one with it or without it is accepted; `required` > approvers, `qualifiedOn` = own name,
  a non-GUID, a missing `separateFromDeployer` are errors; `Counts`: an unlisted principal does not count, a deployer does not count when separation is
  on and does when off; `Evaluate`: the same principal twice counts once, `required: 2` needs two. **`release deploy`, `release qualify` and `deploy` on a descriptor with an
  `approval` block exit 1 with zero backend calls, no identity call, no record and no `set.yaml`** (the check reads the block, so a descriptor with a
  block and tier `team` is refused too, and one without a block is not, whatever its tier); `--preview` is not refused. Azure: section 8.
- **S18b (forgery by editing files):** a record copied from another label, from another environment, with another `setSha256`, with a renamed
  (later) name, with an `activity` that disagrees with its name, or failing the schema makes the label **unqualified** (not ignored); a newest failed
  record beats an older passing one; `set.yaml` edited after the records is unqualified; OQ `unchanged` without an earlier `passed` is unqualified;
  an old IQ before a newer deploy, no `iq` at all, `iq: notRun` and `failed` are unqualified; an unreadable file is a reason, not a crash.
- **S18c:**
  - *Forging by editing a file:* an approval file is never opened for write twice (an existing path fails, the old bytes stay). **S19** (the reader): an approval whose `setSha256` was edited, which sits in another label's or
    environment's directory, or that fails the schema (extra property, missing identity) does not count; `set.yaml` edited after the approval makes it not count; an approval whose `qualifiedOn` differs from the policy's does not count.
  - *By a definition or a set:* text in a definition, set or `--meaning` that looks like an identity, a policy or JSON (`","identity":{…}`) cannot change
    the record: `identity` equals the port's value, the policy is the descriptor's, and the meaning appears only as the string; control characters in
    `--meaning` and a path in the label are rejected.
  - *The deployer approving their own set:* refused when `separateFromDeployer`, accepted when not; an unlisted approver is refused. **S19:** a hand-placed approval by a deployer does not
    count in `Evaluate` when separation is on, nor one by an unlisted approver.
  - Refusals 1 to 6 of section 4 each write nothing; a bad credential stops before the records are read; a repeat by the same principal is refused and a second principal is
    accepted with `required: 2`; the golden approval validates against its schema; nothing is written on a refusal.

## 8. Azure tests

Both are steps appended to `EnvUpAzureTests` (one scenario, because of the one free-tier account), no resources beyond section 1.

- **S18a:** `shared`, `dev` and `stage` up; the `stage` descriptor says `protected` and carries the policy; `db-stage` has 400 RU/s, the account limit is 1000, and no
  workload container has its own throughput; running `stage` again changes nothing. Cleanup deletes both environment groups before the shared one.
  The refusal of a direct deploy is proved offline only.
- **S18c:** the test rewrites the `stage` descriptor that `env up` wrote (not the definition), each rewrite failing if its pattern does not match: its `qualifiedOn` to the renamed `dev` (`s12dv<suffix>`) and the all-zero approver to the principal's
  object id, read from a `dev` deploy record. After the existing deploy and qualify on `dev` (a record with `iq` passed): `approve` of an unknown label is refused; with the strict `stage` descriptor
  (the sandbox principal listed, `separateFromDeployer: true`) it is refused because the same principal deployed the set; with a relaxed copy it succeeds, the file's
  `identity.objectId` equals the `dev` deploy record's and `setSha256` equals the hash of `dev`'s `set.yaml`; a repeat is refused. After the existing out-of-band image mismatch step
  (the last step) a new `approve` is refused as not qualified: approval follows the current evidence. (Two principals are not available in the sandbox; separation between
  different principals is proved offline.)

## 9. Out of the MVP, and the trust boundary

- **No tamper-proofing against someone who can write the records directory.** Records and approvals are local files, like release records (E52); the deployer who can write
  `<records>/dev` can also write `<records>/stage`. A forged qualifying record, a whole forged approval with a consistent hash, or a deleted failed record is not detected.
  The remedy is the audit store (F39) with separate write access for approvals and signing; the formats here (write-once, hashes, identity from the token) are what that store would keep.
- No Entra groups or roles, no PIM, no notifications, no expiry, no revocation or rejection record, no approval of a set that was qualified elsewhere than `qualifiedOn`,
  no per-tier SKUs, no separate subscription for stage, no Azure lock, no RBAC on the stage subscription (ADR 0008's control), no gate on substrate changes, no per-person sign-in (Open choices, 3).

## Choices taken by the user (2026-10-17)

The user chose **(a)** for each of the three; the options are kept for the record.

1. **Where the approval policy lives.** (a, recommended) the environment definition and descriptor, as in section 3: one file per environment carries it, and per-installation
   data (environment names, Entra object ids) stays out of a catalog shared across subscriptions. (b) A catalog policy per tier (`kind: ApprovalPolicy`, `match: { tier: protected }`) for the
   shape (`required`, `separateFromDeployer`), with `qualifiedOn` and `approvers` still in the environment definition: more literal to "policy per tier" and platform-team owned, but two places and
   a new catalog kind (~60 more lines). The rest of the note holds under either.
2. **Who may approve.** (a, recommended) A listed set of Entra object ids in the policy. (b) Any principal that authenticates, with only the separation rule: no list to maintain, but then
   "approval" is a record of who clicked, not of who was allowed. Roles or groups (F54) need Graph and are out either way.
3. **Who an approver is.** The identity port is a service principal (section 4), so an approval names a principal, not a person. (a, recommended) The service-principal port in the MVP, one
   principal per approver, per-person sign-in deferred: the sandbox has one principal and the Azure test cannot log in interactively, and nothing in the record format changes later. (b) A user
   credential for `approve` only (interactive sign-in with Azure.Identity; the token's `oid` is the person): the approval means a human, but it adds a second credential type to `jd.azure`
   and the port, the Azure test cannot run it, and the claims of a user token (`appid` is the client's, not the person's) are *Unverified* against the record's `clientId`.

## Decisions (lead, pending the user's review)

1. The approval policy lives in the environment definition and descriptor, not the catalog; the schemas require it for `protected` (section 3).
2. An environment whose descriptor carries an `approval` block accepts no direct `release deploy`, `release qualify` or `deploy` through `jd`; `promote` is the only deploy path. The CLI tests for the block, not the tier; the schemas couple `protected` to the block. In the MVP the only protection is the paved road; the control (RBAC on the stage subscription, ADR 0008) is out of the MVP.
3. An approval binds the set (`setSha256`), not a qualifying record; "qualified" is judged when it is used, and fails closed on any inconsistent record (sections 4 and 5).
4. Approvers are listed Entra object ids; separation from the deployer is an explicit policy field, with the deployer being any principal that ran a `deploy` of the label on the source environment.
5. `--env` is the descriptor file, as in the other release commands; `stage` shares the subscription, the Cosmos account and the workspace with `dev` and has its own database and Container Apps environment.
6. The sample `stage` ships strict (`separateFromDeployer: true`, all-zero approver); the Azure test uses a relaxed copy. The principal running `promote` is not checked against the approvers.
7. Approvals are never revoked; a repeat by the same principal is refused; the number of approvals beyond `required` is not limited.
