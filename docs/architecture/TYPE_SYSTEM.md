# Resource Type System

How a workload definition becomes provisioned infrastructure, why it is shaped this way, and what was tried and rejected getting here.

Companion to [Open Issues](../OPEN_ISSUES.md), which lists what is broken or unfinished. This covers what is deliberate — so decisions are not silently reversed by someone who assumes they were accidents.

---

## 1. The model

```
workload.yaml                          a team declares named resources by intent
     │                                 metadata + resources[{name, type, ...properties}]
     ▼
src/jd.core/types/*.yml                what a resource type promises
     │                                 properties (with defaults, overridable) + outputs (with secret)
     │                                 backend-neutral: no cloud, no SKU, no service name
     ▼
jd.bp.pulumi/types/*.yml               how THIS backend delivers that promise
     │                                 deployments → parameters (1-1) + parameter_sets (1-n)
     ▼
WorkloadResolver                       expand → validate → substitute → order
     │                                 pure: no cloud, no Pulumi, no filesystem
     ▼  WorkloadPlan
WorkloadExecutor                       resolve deferred tokens → call the backend
     │                                 the only component that touches anything real
     ▼
samples/provisioner/*/Pulumi.yaml      the actual IaC
```

Two layers, two catalogs. Core says *what*; the backend says *how*. A workload never names an Azure service, and no core **property or output** carries cloud vocabulary.

Core *descriptions* do mention Azure and SKUs in four places — always to explain what is deliberately excluded ("never a SKU", "says nothing about engine, SKU, sizing"), or to warn a reader about a backend-specific consequence, such as the Application Insights connection string embedding an ingestion key. Prose explaining an exclusion is not a leak; a *property* only one backend could honour would be.

---

## 2. Decisions and why

### Type contracts are data, not C# classes

They were briefly C# classes. Moving them to YAML means adding a resource type needs no platform build — which is the point, since types are the thing that changes most.

The cost is real: nothing is compiler-checked, so a typo in a property name is a runtime surprise rather than a build error. That is bought back by the coherence checks in §4. **If those checks are ever deleted, this decision becomes a bad one.**

### Resources are named, not keyed by type

`resources: [{name: primary, type: database}]`, not `requires: [{type: database}]`.

One extra field buys: two databases in one workload, unambiguous `${resource.<name>.<key>}` references, and non-colliding generated resource names. Retrofitting names later would mean editing every workload and every reference plus a naming-scheme change that orphans state. Matches SCORE, which `score-evaluation.md` keeps open as an option.

### `computing` is a resource like any other

There is no `container:` block. The runtime is just a named resource with an `image` property.

This removed a special case in every layer: no "which deployment hosts the container" marker, no separate handling of `container.variables`, and a second runtime (api + worker) becomes expressible for free.

### Variation is expressed as a class, not knobs

`Database` has one property: `class ∈ {standard, high-availability, high-performance}`. Not `sku`, not `throughput`, not `backupRetentionDays`.

A class is a promise about behaviour that any backend can interpret for its own engine. The moment a property appears that only one backend could honour, the abstraction has leaked — that is the review signal, and no test catches it.

### `parameters` and `parameter_sets` are separate blocks

Both produce parameters. They were briefly merged into one block keyed either by parameter name or by a `${property.x}` selector — that made sibling keys mean different things, put an expression in key position, and needed ~45 lines of hand-written unpacking. Separating them let typed deserialisation handle both and deleted that code.

Shape difference warrants separate blocks even when purpose is shared.

### Grants are separate deployments from what they grant on

`cosmos-db-access` is its own deployment, not part of `cosmos-db`. This is **load-bearing for acyclicity**: the account supplies the endpoint the app needs, and the grant consumes the identity the app produces. Merge them and a single node both needs and provides — an unresolvable cycle.

Anyone tempted to tidy this up should read that twice.

### The workload identity is workload-scoped, not a `computing` output

Every resource that grants access needs a principal id. Modelling it as an output of `computing` would make `database` reach into another resource type's outputs — the messiest possible edge. `${identity.principalId}` is resolved once by the executor instead.

(With two runtimes this becomes ambiguous — recorded in Open Issues §7.)

### The resolver is pure; only the executor touches reality

`${property.*}`, `${resource.name}`, `${workload.*}`, `${env.*}` resolve at plan time. `${deployment.*}` and `${identity.*}` cannot — those values do not exist until something has been provisioned — so they survive as tokens for the executor.

This is what makes the whole pipeline testable against real type files with no Azure, and it is why `--plan` works offline.

### Deployment keys are scoped per resource instance

Translation ids (`account`, `access`) are unique per *translation*, not per workload. `{resource}.{deployment}` — so two databases do not collide, and `${deployment.account.x}` inside `database.yml` means *this instance's* account. Foundation ids stay workload-scoped.

### The resource group lives in the backend, not core

Resource groups are an Azure concept. Putting `foundation` in the backend's `_shared.yml` keeps core free of them, and a team never learns they exist.

### Ordering is derived, never declared

Deployment order comes from the reference graph. Nothing states a sequence; file order is meaningless. Declared order is a second source of truth that drifts the moment someone reorders a list.

Two of the real edges appear in no file at all: `api.app → primary.account` (derived two hops through a workload variable and the type's `outputs:` block) and `primary.access → api.app` (via the identity). Both are tested.

---

## 3. Approaches tried and rejected

Recorded so they are not re-proposed as fresh ideas.

**A YAML mapping layer** (`definitions/mappings/*.yml`) — a per-resource-type mapping with its own expression language, environment overlays and output contracts. Worked, and is on the **`mappings-sample-approach` branch** with a design doc.

Rejected because it *looked* backend-neutral while being implicitly Pulumi-specific: it referenced Pulumi definition folder names, bound to Pulumi config keys, and its `${deployment.x.output}` were Pulumi stack outputs. A Terraform backend could not have reused a line. The current split puts the seam where it actually falls.

**Concrete Pulumi names in the workload** — `requires: [{type: cosmos-db}]`, no abstraction layer at all. Genuinely simpler, and defensible for a single-team single-environment MVP.

Rejected because it costs portability the project has already needed once: Postgres → Cosmos happened mid-development because Flexible Server has no permanent free tier. Under concrete typing that is an edit to every team's workload rather than one platform change. It also puts composites on the team — `cosmos-db` alone grants no access, so a team would have to know `cosmos-db-access` exists and is a separate RBAC mechanism.

**Type contracts as C# classes** — typed `Database`, `Computing`, `Observability` with attributes for secret/overridable. Built, then replaced with YAML at the user's direction. The trade is stated in §2.

---

## 4. How correctness is maintained

Since neither layer is compiler-checked, everything rests on checks that grow automatically.

**Catalog-driven, never per-type.** Every coherence check is a `[Theory]` fed by `ResourceTypeLoader.LoadDefault()`. Adding `queue.yml` inherits every check with no new test code. Per-type tests rot; these cannot.

**Mutation-verified.** A passing suite that cannot fail is worthless, so each check was confirmed to fire by deliberately breaking the translation — mapping a property core does not declare, removing a class branch, reading an output a definition does not produce, putting a non-enum property under `parameter_sets`. Each produced a precise message.

One process note worth carrying forward: a mutation that *silently does not apply* is indistinguishable from a check that works. One of mine missed because a search string did not match, and appeared to pass. Always confirm the file actually changed.

**Gaps are declared, not omitted.** `unmapped:` in a translation names each core property the backend cannot honour, with the reason. That turns a blocker into a checked fact and lets the test insist every property is either mapped or explicitly waived.

**A fake backend covers execution.** `FakeBackend` returns the outputs each definition actually declares, so the whole execution path — deferred substitution, identity resolution, role expansion, output publication, fail-fast — is verified deterministically without Azure.

What this can never cover is in Open Issues, and matters: unknown ARM constraints, whether a class promise actually holds, and whether the abstraction has gone leaky.

---

## 5. Where to pick up

Ordered by what unblocks the most.

1. **The container runtime decision.** Everything else is downstream. `app-service` has no image or `appSettings` parameter, so the pipeline provisions infrastructure that does not run the team's container and never receives its configuration. Either teach `app-service` Linux containers, or add Container Apps and repoint `computing` — the latter also gives `definitions/compute.yml`'s `cpu`/`memory`/`replicas` a home and resolves that duplicate. Needs a container registry definition either way (on the other branch).

2. **Generate `workload.schema.json` from the catalog.** It currently describes the retired `container:`/`requires:` shape. Dormant today because nothing validates against it — but wire it up unchanged and it rejects every valid workload. Generating it also removes a permanent drift source.

3. **Add the "class branches must differ" check.** Cheap, and it catches the live case where `database`'s three classes are structurally valid and semantically identical.

4. **Decide `environment`: workload field or deploy argument.** Blocks any story about promotion, and contradicts the strategy doc as it stands.

5. **Resource tags.** Nothing applies any, `enforce-compliance-tags` specifies three, and backfilling tags onto existing resources is far more work than setting them at creation.

Then: policy layer (which is what would deliver the observability connection string), destroy/reconciliation, and the CLI.
