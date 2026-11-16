# Approvals and gated environments

A set reaches a gated environment (`stage`) only when it is **qualified** on the environment below (OQ and IQ passed there) **and approved** for
the target by principals its policy allows. Approvals are local, write-once evidence in the store of release records and share their trust
boundary ([release-record.md](release-record.md) §1, §3). Addresses [F54](../open-questions.md) for the MVP; F37, the full approval model,
stays open. Stage and production are platform-owned environments ([ADR 0008](../decisions/0008-enforcement-in-landing-zone.md)). Commands:
[usage.md](../usage.md#approvals-and-promotion).

## 1. The `stage` substrate on the free tier

`stage` is one more environment on the shared layer, brought up like `dev` (`samples/environments/stage.yaml`, type `stage`). It offers what
`dev` offers, and the components' implementations serve both types, so it deploys the same parts.

| Part | `stage` | Shared with `dev`? |
|---|---|---|
| Cosmos DB account | the one free-tier account of the shared layer | **shared** (Azure allows one per subscription) |
| Cosmos DB database | its own, `db-stage`, 400 RU/s shared by its containers | own |
| Container Apps environment, resource group | its own, Consumption profile | own |
| Log Analytics workspace | the shared one, with its daily cap | **shared** |
| App Insights, containers, grants | per workload | own |

- **A database per environment** keeps `stage` data and throughput apart from `dev` by construction; a workload's grants are scoped to its container.
- **RU/s:** 400 (`dev`) + 400 (`stage`) = 800 of the free 1,000. The account is created with `totalThroughputLimit: 1000`, so Azure itself refuses
  more; a third environment does not fit (400 is Cosmos's minimum for a provisioned database).
- **Container Apps:** both environments share the subscription's monthly free grant ([standards](../engineering/standards.md) §6: Consumption only,
  `minReplicas: 0`, the smallest size).
- **Same subscription and region.** A real stage is in its own subscription (ADR 0008); the sandbox has one subscription and one free Cosmos
  account, so the MVP shares them.
- **Teardown** deletes the environment groups before the shared one, because their Container Apps environments log to the shared workspace.

## 2. The gate: a descriptor with an approval block

**An environment whose descriptor carries an `approval` block (§3) is deployed to only through `jd promote`.** The use cases test for the block,
never for a type name. The coupling to types is the catalog's: `catalog.yaml` declares each environment type, and a type declared with
`approval: true` (`stage`, `prod` in the seed) requires its definitions and descriptors to carry the block, so such an environment cannot be
written ungated (`EnvironmentTypeCheck`, at `env up` and before any resolve). Any other environment may carry a block and is then gated the same way.

- **Direct deploys are refused:** `jd deploy`, `jd release deploy` and `jd release qualify` exit 1 before the identity, the records or the backend
  is touched. `qualify` is refused too, because it would create the label's `set.yaml` from whatever file it is given, and a gated environment's
  `set.yaml` must be the kept set of the source environment (§5). `--preview` creates nothing and is allowed; so is `jd env up` (the substrate is
  not a release).
- **Whoever runs `jd promote` is not checked against `approvers`.** The approvers vouch for the set; who may run the promotion is Azure RBAC.
- **Only the paved road is gated.** Through `jd`, nothing reaches a gated environment unqualified and unapproved. The control that makes that true for
  everyone, RBAC on the stage subscription (ADR 0008), is not built; someone who removes the block from a descriptor, or runs `pulumi` directly,
  is outside the guarantee.

## 3. Approval policy as data

The `approval` block lives in the **environment definition** and is copied verbatim into the **descriptor** by `jd env up`, so the descriptor
alone carries it. Fields and their constraints: [`approval.schema.json`](../../schemas/approval.schema.json).

- `qualifiedOn`: the environment whose records must show the set qualified; not the environment's own name.
- `required`: distinct approvals needed, 1 to the number of `approvers` (a policy that can never be met is an error at `env up`).
- `approvers`: the Entra object ids (unique GUIDs) that may approve.
- `separateFromDeployer` (**required**, no default, since a silent default would be a silent weakening): an approver may not be a principal that
  deployed the label on `qualifiedOn`.

**Evaluation** is two pure functions of `ApprovalPolicy` (`jd.resolver`): `Counts(approver, deployers)` (listed, and not a deployer when
separation is on) and `Evaluate(approvals, deployers)` (distinct `objectId`s that count, against `required`). `jd approve` uses `Counts` to
refuse early; `jd promote` uses `Evaluate` on the records: one rule, one code. **Deployers** are the distinct `identity.objectId` of every
`deploy` record of the label on `qualifiedOn`, failed attempts included; `qualify` records do not count.

**Why not the catalog:** the catalog is shared across subscriptions and tenants; environment names and object ids are per installation. The catalog
says whether a type must carry a policy; the definition holds its values.

The sample `stage.yaml` ships with the all-zero GUID, which matches no principal: nothing can be promoted until the operator lists real ones.

## 4. Approving

`jd approve <label> --env <descriptor> --meaning <text>` writes an **approval record**: JSON, `kind: ApprovalRecord`,
[`approval-record.schema.json`](../../schemas/approval-record.schema.json), at `<records>/<target>/<label>/<time>-approve.json`, write-once
through the same store. It sits in the **target's** directory, where `promote` looks; the set is read from the source's.

- **The approver is a service principal.** The identity comes from the same port and credential as a deployer's (release-record.md §3), so
  `approvers` lists principals, and separation from the deployer means something only when each approver and each deployer has its own.
  A record says which principal approved, not which person.
- **The approval binds the set, not a record:** `release.setSha256` is the SHA-256 of the kept `<records>/<qualifiedOn>/<label>/set.yaml`.
  It does not pin a qualifying record, because "qualified" is judged again from the current files whenever it is used (§5).
- The record also holds the target environment, the label, `qualifiedOn`, the identity, `approvedAt` and the meaning.
- **`--meaning`** is what the approver asserts (the "meaning of the signature" of 21 CFR Part 11): 1 to 500 characters, no control characters.
  It is recorded and printed, never interpreted: no logic reads it.
- **Order:** the label and the meaning; the descriptor and its policy; the identity; the kept set and its qualification; the approver listed;
  separation; the repeat check; then the one write. Every refusal comes before the write, so nothing is written on any.
- **A repeat is refused as a convenience, not a lock:** two simultaneous runs by one principal could both write, but approvals are counted by
  distinct principal. Earlier approvals are read through the reader of §5 step 3; a file it ignores is not an earlier approval.
- Approvals are never revoked; a withdrawn release gets a new label.

## 5. What `promote` reads

`jd promote <label> --to <descriptor> --catalog <dir>` takes no set file: the set is the bytes kept on the source (ADR 0007), which the walk
on the target writes unchanged as the target's own `set.yaml`, so both run the same digests. With policy P, target T, source S = `P.qualifiedOn`
and label L (`PromotionCheck`, `QualificationReader`, `ApprovalRecord`):

1. `h` = SHA-256 of `<records>/S/L/set.yaml`. Missing: unknown label.
2. **Qualified** (derived now, never stored; the same function `jd approve` uses). Every `<time>-deploy.json` and `<time>-qualify.json` in
   `<records>/S/L/` must be consistent with where it is, **or the label is not qualified, with the file named**: valid against the record schema;
   `activity` equals the name's suffix; `environment.name` is S; `release.label` is L; `release.setSha256` is `h`; `finishedAt` equals the
   name's time. Then the newest `deploy` record has `outcome: succeeded` and every workload's `oq.status` is `passed`, or `unchanged` when an
   earlier record in the directory has `passed` for that workload; and the newest record carrying an `iq` is not older than that deploy and has
   `iq.status: passed`.
3. **Approved:** the `<time>-approve.json` files in `<records>/T/L/` valid against the approval schema, naming T, L, `h` and `P.qualifiedOn`.
   One that does not match is **ignored and reported**: an approval can only add, so ignoring it is safe, whereas ignoring a bad qualifying
   record would hide a failure, so those fail closed.
4. `Evaluate(approvals, deployers(S, L))` holds.
5. Only when 2 and 4 hold is the kept set walked on T, its bytes re-hashed against `h` first. Otherwise exit 1 with every reason, nothing written.

Both are judged at promotion time from the current files, so a drift found by a later `qualify` on S, a failed re-deploy on S, or an edit of
`set.yaml` after approving each withdraws the right to promote, without anyone revoking anything.
