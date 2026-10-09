# Open Questions & Known Problems

Register of everything not yet decided for just-deliver. IDs are stable global labels, not positions: new items take the next free number and keep their section letter, so references never shift.

**Status labels:** `OPEN` = nothing decided · `PARTIAL` = a position exists, gaps remain · `SETTLED` = a position has been written up as an ADR in [decisions/](decisions/).

**Nothing is accepted until reviewed.** Every ADR derived from a `SETTLED` item is "Proposed — pending review"; a `PARTIAL` "leaning" is a working position, not a decision. When an item settles, write an ADR and replace the entry here with a one-line pointer.

---

## A. Foundational model

These shape everything else.

### A1. Continuously reconciled or applied by a pipeline? — see [ADR 0006](decisions/0006-infra-deployed-with-app.md)
### A2. Where does a workload definition live? — see [ADR 0007](decisions/0007-definition-upload-snapshot.md)
Residual open point: registration flow — explicit registration before first deployment or implicit on it, and who may register.
### A3. Enforcement posture per environment tier — see [ADR 0008](decisions/0008-enforcement-in-landing-zone.md)

### A4. What must be identical between team environments and platform environments? `OPEN`
- If team envs permit what prod forbids, teams build on sand and "it worked in my environment" means nothing; if identical in every respect, unaffordable.
- **Candidate answer:** the *policy set* is identical across tiers; scale, SKU, replica count and data may differ.
- Already leaned on by ADR 0010 (uniform `protect` incl. dev) and ADR 0014 (hooks uniform across tiers).

---

## B. Environments & landing zones

### B5. What is an environment, physically? `OPEN`
Subscription per environment, or resource group within a shared subscription?
- Affects blast radius, quota, policy scope, and environment creation speed.
- **Constrained by E27 ([ADR 0013](decisions/0013-container-apps-runtime.md)):** a Container Apps Environment needs a dedicated subnet whose minimum size depends on the environment type — an IPAM decision before any address space is allocated, or adding environments later means renumbering.
- Subscription creation needs billing-scope permissions and is capped per billing account.
- Azure Policy assignment takes time to propagate, so a fresh subscription is effectively unguarded for a window.
- Tension with [architecture/landing-zone.md](architecture/landing-zone.md), which lists "one landing zone per team" as an anti-pattern. Reconcilable if the real boundary is *criticality tier* rather than team, but must be stated.

### B6. Environment lifecycle: TTL, deletion, provable reprovisionability `OPEN`
- Without a TTL, dev environments are immortal.
- Deletion is harder than creation: Key Vault soft-delete blocks name reuse for 90 days; backups and snapshots survive the "delete"; someone must decide whether data is exported first.
- Disposability holds only if reprovisioning is complete and cheap; if it takes hours or one manual step, teams repair by hand and environments become pets.
- So "rebuildable from its definition" should be verified continuously by rebuilding a reference environment on a schedule.

### B7. Substrate versioning `PARTIAL` (under discussion)
The shared environment tier (SQL server, Log Analytics workspace, Service Bus namespace, VNet, policy assignments) is defined by code that changes, so every environment is implicitly pinned to the definition as of its creation day.
- **Leaning so far:** every definition is a versioned YAML file; a non-backward-compatible change requires a migration step moving data from the old resource to the new one. Upgrades are gated on plan content, not version distance, checked against the protection classes of [ADR 0010](decisions/0010-replacement-protection-classes.md) (C11, its prerequisite, is settled).
- Instances must record which version they are on; the file version only says what is *available*.
- Upgrading an environment must be a separate gated operation; a workload deploy must never move the substrate version (ADR 0006).
- Replace-and-migrate works for dedicated workload resources but not the shared tier: telemetry cannot be migrated into a new workspace; replacing a SQL server hosting forty databases is a coordination project. Shared-tier breaking changes mostly need *in-place upgrade*.
- Schema version (YAML shape, breaks parsing) vs content version (same shape, different config, breaks running systems) need different mechanisms — a converter vs a staged rollout.
- Version skipping: a diff engine reasons endpoint-to-endpoint and won't pass through v5's migration going v3→v7. Versions carrying migrations or ordering constraints must be marked and applied sequentially.
- Rollout in waves (team envs → stage → prod) plus a deprecation policy capping how far behind an environment may be; large gaps are themselves the risk. Same wave mechanism as E51.

### B8. Substrate capacity and placement `OPEN`
- Forty workloads on one shared SQL server hit connection limits, pool DTUs and per-pool database caps. When does an environment get a second server, and how does a workload know which one it landed on? Same for Service Bus namespaces.
- Telemetry: one workload with a logging bug can burn the workspace's daily ingestion cap and blind every other workload. Mitigation: per-component caps.

### B9. Data in disposable environments `OPEN`
- Compute and config reprovision cleanly; a hand-seeded dev database does not.
- Either seed/restore becomes a first-class operation in environment creation, or state lives outside the disposable boundary and we say so plainly.

### B10. RBAC split inside a team-owned subscription — see [ADR 0009](decisions/0009-rbac-split-team-subscription.md)

---

## C. Resources & dependencies

### C11. Replacement detection and gating — see [ADR 0010](decisions/0010-replacement-protection-classes.md)
Two prerequisite spikes remain (when `protect` fails; whether Pulumi YAML can drive `options.protect` from config).

### C12. Shared vs dedicated resources `PARTIAL`
- **Leaning so far:** shared resources belong to the environment and live and die with it — one database server per environment, one Azure Monitor workspace so telemetry correlates; each workload gets its own database and App Insights component inside them. Three ownership tiers: landing zone → environment → workload; the isolation dial is the *environment*, not the resource.
- **Settled within this item:** isolation is expressed as `class`, not by naming or `id`. "Dedicated" means its own *server* (noisy-neighbour isolation, compliance boundary, different engine version) — a different substrate tier, expressed by the `class` field ([architecture/workload-definition.md](architecture/workload-definition.md)). Example in [ADR 0011](decisions/0011-requirement-id.md).
- **Open:** which classes exist per resource type; whether `class: dedicated` in production requires approval given the cost difference.

### C13. Multiple resources of the same type — see [ADR 0011](decisions/0011-requirement-id.md)
- **MVP pattern (S01):** `type`, `id` and `class` share `^[a-z][a-z0-9-]{1,14}[a-z0-9]$`. Hyphens are allowed so a hyphenated type can be its own default id; provider-specific name limits (e.g. storage accounts: 24 chars, no hyphens) are the namer's job (S04), and the final id budget is still open under C14.

### C14. Resource naming `OPEN`
- Global uniqueness (SQL, storage), length limits (storage accounts: 24 chars, lowercase alphanumeric), collisions across environments, name reclaim blocked by soft-delete.
- Names are generated from workload name plus prefixes/suffixes; the budget must be worked out before it bites.
- **Tightened by C13:** the name must also hold a resource `id` (~12 chars) plus workload name, environment and type prefix. With `metadata.name` allowing 40 chars, the budget does not close — derive the scheme from the tightest target rather than from the workload name outward. May force tightening the `metadata.name` pattern (H50).
- **MVP pattern (S01):** `type`/`id`/`class` use `^[a-z][a-z0-9-]{1,14}[a-z0-9]$` (hyphens allowed so a hyphenated type can be its own default id); provider-specific limits (storage accounts: 24 chars, no hyphens) are the namer's job (S04); the final id budget is still open here.

### C15. Cost attribution and control `OPEN`
- Nobody owns cost in the current design.
- A single database on a shared server bills separately, but an elastic pool bills at pool level and a shared workspace as one line — per-workload cost on the shared tier is an allocation rule we invent.
- Missing: per-team budgets and quotas; a cost estimate on the deployment preview screen. "What will this cost" is the first question before approval; "who spent this" the first after go-live.

### C16. Deprovisioning a resource `PARTIAL`
A team removes `- type: database` from a prod workload; today the platform deletes the production database.
- **Leaning so far:** the platform refuses to apply the removal, surfaces it as a destructive diff requiring explicit approval, and on approval performs backup-then-delete. `protect: true` (ADR 0010) is the backstop if bypassed.
- **`retainOnDelete` is not adopted as policy.** It drops the resource from state but leaves it in Azure, producing billable, untracked orphans that squat on the deterministic name their replacement needs. Broken for replacement too: replacement is delete-first because the new resource can't take a name the old one holds.
- **`retainOnDelete` kept as a state-surgery tool:** re-homing a resource between stacks (retain in old, `pulumi import` into new) — needed when substrate stacks split from workload stacks or a resource moves between ownership tiers.
- **Open:** backup semantics per resource type (what "backup" means for a Service Bus namespace or a workspace); retention window before the backup is discarded.

### C52. The resolution layer — mapping definitions to resources `OPEN`
How `requires: [{type, id, class}]` plus environment, mappings and policies becomes a concrete resource graph. Currently unowned — the most load-bearing component not yet specified, and it constrains the schema. **Proposed design in progress: [architecture/resolver.md](architecture/resolver.md).**

**Hard parts**
1. **A matcher, not a map.** Selection depends on type, class, tier, possibly team or region. Needs a specificity ordering with ties as errors, or the same definition resolves unpredictably.
2. **Emits a graph, not a list.** Mixed *create* and *reference* nodes (database needs the environment's server; a grant needs an identity and a target). A mapping is three parts: what to look up, what to create, what wiring it produces.
3. **References resolve in phases:** static → post-infra → post-identity. Principal ids don't exist until the app is created (ADR 0012); a one-pass resolver will need rewriting.
4. **Pure function, separate from provisioning:** `resolve(definition, environment, mappings, policies) → graph + provenance`. Preview screen, audit chain, approval diff and destructive-change gate all consume the resolved graph *before* anything is applied; resolve-as-you-go makes all four impossible.

**Pitfalls**
- **Overrides applied after policies** — present in the current design ([architecture/workload-definition.md](architecture/workload-definition.md)): a whitelisted override beats a compliance policy.
- **Shallow merge, no provenance** — two policies touching one field conflict silently. Provenance per field (`{value, source, rule}`), not per stage.
- **Over-abstraction** — `type: database` breaks when it resolves to an engine the team's driver doesn't expect. The earlier rule was: the type names the interface the code binds to, the class the operational shape the code cannot see (`type: postgres`, `class: dedicated`).
  **User decision (S12a), overriding that rule:** workloads ask for `database`; the platform team's mapping decides the engine (Cosmos DB SQL API today). To keep the code honest the type exports an `engine` value (`cosmos-sql`, from the mapping's data) that the workload can read. Consequence still open: changing the engine behind a type for an existing workload is a data-moving, breaking change and must later route to approval (F54). C52 is not resolved by this.
- **Position-based identity** — deriving names from index in `requires` destroys resources on reorder. Identity is `(workload, id, type)` (ADR 0011).
- **Unversioned mappings** — a deployment must record the mapping version that resolved it, alongside the definition SHA (ADR 0007), or the chain isn't reproducible.

**Recommended shape** — matching criteria + lookup + create + wiring, per mapping:

```yaml
match:   { type: postgres, class: standard, tier: protected }
lookup:  { server: environment.postgres.server }
create:  { template: azure/postgres-database, config: {...} }
wiring:
  env:    { DB_HOST: "${lookup.server.fqdn}", DB_NAME: "${create.name}" }
  grants: [{ identity: workload.runtime, role: db_datawriter }]
```

**Fork 1 — mappings as code or data?** Recommend **data**. Code means a mapping change needs a platform release, and audit can only say "resolved by platform build 1.2.7". E51's rollout waves and B7's pinning assume mappings version independently. **Mappings and policies are data; the resolution engine is code**; a plugin interface covers rare dynamic cases. Current docs blur this (mappings shown as TypeScript objects — data wearing code's clothes). Avoid the DSL cost by **replacing conditionals with matching criteria** (two narrow mappings rather than one `if`), substitution only, no logic. Bonus: "mapping `postgres/protected` matched" reads better than "branch 3", and fleet dry-run ("which workloads would this change?") becomes a query.

**Fork 2 — resolve to a graph or straight to stack configs?** Current architecture emits `(template, stack, config)` triples, losing dependency edges. Recommend an explicit graph that the orchestrator topologically sorts; otherwise ordering lives implicitly in the orchestrator loop and breaks on the third resource type needing a parent.

**Review of the first flow sketch** ([architecture/workload_deployment_flow.html](architecture/workload_deployment_flow.html), redrawn from the excalidraw). Skeleton is buildable — schemas, Pulumi templates and `IBackEndProvider` exist — with these changes:
1. **Parse emits a graph with open references, not finished backend yml.** The orchestrator fills each step's inputs from prior outputs (hard part 3), matching the config-passing choice in [architecture/provisioning.md](architecture/provisioning.md).
2. **The resolver is backend-agnostic.** The sketch put the parser inside the backend provider, making resolution Pulumi-specific and unusable for preview, approval diff and destructive gate. Only rendering a graph node into template + config belongs to the provider.
3. **The orchestrator is a durable step loop, not one call.** Each step in ADR 0006's order is one queue round trip, checkpointed (E29). A queue session keyed on workload + environment gives E30's serialization. End states: live; degraded (postDeploy failed); failed-partial (infra kept, traffic unchanged, retry re-enters at the failed step — E31).
4. **Added:** IT Ops policies and the environment as resolver inputs; a preview and approval gate between resolve and deploy (ADR 0007, ADR 0010); recording definition SHA and mapping version with the deployment.
5. **Backend × cloud-provider frames dropped.** Keep the `IBackEndProvider` seam, build only Pulumi + Azure; don't design for N×M yet.

---

## D. Identity & secrets

The differentiator, so scrutiny concentrates here.

### D17. User-assigned vs system-assigned identity — see [ADR 0012](decisions/0012-system-assigned-identity.md)
Residual open point: ACR pull bootstrap (suggested: one environment-scoped user-assigned identity with `AcrPull`).

### D18. Data-plane grants `OPEN`
Where does the data-plane grant step run, as what identity, over what network path?
- Azure SQL access is not an ARM role assignment: requires connecting as the server's Entra admin and running `CREATE USER [<identity>] FROM EXTERNAL PROVIDER`. Postgres flexible server has its own equivalent; Cosmos uses a separate data-plane role assignment API.
- The database sits behind a private endpoint, so the step executes from inside the environment's network, not the control plane.
- Storage, Key Vault and Service Bus are plain RBAC and work first time — which is what makes this trap effective.

### D19. Platform privilege and the resolver as a security boundary `OPEN`
- Assigning roles needs `roleAssignments/write` (User Access Administrator or Owner) across every environment — the platform credential becomes the highest-value target in the estate.
- A workload definition becomes a privilege-granting primitive. Nothing structural stops a team declaring a dependency that resolves to another team's database — only resolution logic, which is therefore a security control (C52).

### D20. Role assignment scale ceiling `OPEN`
- Role assignments are capped per subscription (4,000 currently). 50 workloads × 5 dependencies × 3 environments = 750 before humans and platform identities. Fine now, fatal at 5–10×.
- Mitigations — resource-group-scope assignment, or granting to Entra groups — must be chosen early; changing grant granularity later is miserable. Groups add propagation delay and token-size concerns.
- Orphaned assignments (D26) and hook grants (ADR 0014) count against it.

### D21. Propagation delay and revocation lag `OPEN`
- **Raised in priority by D17:** with system-assigned identities the grant always follows the resource, so propagation delay is on the critical path of every deployment. Must verify effective permissions before traffic shift, with a bounded grace period and a failure message distinguishing "grants have not propagated" from "the application is broken". (The retrying `verify` hook in ADR 0014 is intended to provide this discriminator.)
- New role assignments take seconds to minutes; an app starting immediately gets intermittent 403s on first boot — needs retry with backoff or a readiness gate.
- Revocation: removing an assignment doesn't invalidate issued tokens; access persists for the token's remaining lifetime. "We revoked it" is false for a window — matters for offboarding and incident response.

### D22. Auth-mode coverage table `OPEN`
- **Pending check from E27:** does Container Apps refresh a Key Vault secret reference without a new revision? Decides whether secret rotation is a platform operation or a deployment.
- Storage, Key Vault, Service Bus, Event Hubs, SQL, Postgres, Cosmos support Entra auth to varying degrees; Redis got it late and many clients don't handle token refresh; third-party SaaS never will.
- Needs a first-class `(resource type, auth mode) → wiring mechanism` table with an explicit fallback: generate a secret into Key Vault, grant the identity *read on that secret*, reference by URI rather than copying the value. Honest claim: "no human handles secrets", not "no secrets".

### D23. Third-party secrets `OPEN`
- The acknowledged residual. Needs: who may write and read them; rotation; hard guarantees values never reach Pulumi state in plaintext, logs, or the resolution-chain UI.

### D24. Local development and CI access `OPEN`
- A laptop has no managed identity. Without a parallel grant path — a developer group holding the equivalent data-plane role in dev environments only — someone creates a password out of band within a week and the model is dead.

### D25. Platform bootstrap `OPEN`
- What credential provisions the first environment?
- Where does the Pulumi state encryption key live once state moves to Blob Storage? An empty `PULUMI_CONFIG_PASSPHRASE` is fine locally, not otherwise. See [plans/phase-0-bootstrap.md](plans/phase-0-bootstrap.md).

### D26. Identity cleanup on deletion `OPEN`
- **Reduced but not eliminated by D17:** system-assigned identities die with their resource, so no orphaned *identities*.
- Orphaned *role assignments* still accumulate (principals deleted by replacement or workload deletion) and count against D20's cap. A periodic sweep for assignments whose principal no longer resolves is required — a genuine audit finding if skipped.

---

## E. Deployment process

### E27. What runs the container? — see [ADR 0013](decisions/0013-container-apps-runtime.md)
Residual open point: Key Vault secret-reference refresh without a new revision (D22).
### E28. Where does the platform's engine stop? — see [ADR 0014](decisions/0014-hook-points.md)

### E29. Durable execution `OPEN`
- A deployment is a long-running, multi-step, resumable workflow; an in-memory loop in a background service loses it if the platform restarts between "infra provisioned" and "workload deployed".
- Needs checkpointed state, idempotent steps, step-level retry. C52's sketch review proposes one queue round trip per step.

### E30. Concurrency `OPEN`
- Two deployments of the same workload and environment at once. Pulumi's state lock errors rather than queues, so a per-(workload, environment) serialization point is needed (C52 suggests a queue session keyed on workload + environment).

### E31. Partial failure semantics `OPEN`
- Pulumi leaves partial state on a failed `up`; there is no transaction. "Deployment failed" ≠ "nothing changed".
- Needs a defined workload state after mid-sequence failure and re-entrant retry rather than restart-from-scratch.
- **Raised in priority by A1:** infra and app deploy in one operation, so the clean "provisioning failed, nothing deployed" state no longer exists.
- C52 proposes end states live / degraded / failed-partial (infra kept, traffic unchanged, retry re-enters at the failed step).

### E32. Rollback `PARTIAL`
- **Leaning so far:** rollback reverts the app only, via a revision switch; infra is forward-only (ADR 0006 rider 2). Mechanism: multiple-revision mode, so rollback is a traffic-weight change to a warm previous revision (ADR 0013). Migrations are not rolled back, hence expand/contract (ADR 0015).
- **Open:** infra provisioned by a deployment that then failed at the app step — left in place (consistent with forward-only) or cleaned up?
- **Open:** how the UI communicates that a rollback restored the app but not the infrastructure deployed alongside it.

### E33. Database migrations — see [ADR 0015](decisions/0015-database-migrations.md)
Residual open points: `timeout` ceiling in protected environments; verifying PITR retention before destructive migrations.

### E34. Promotion between environments `OPEN`
- Same artifact promoted, or one per environment? Who triggers promotion? Do gates differ per environment? How is environment-specific config handled across the boundary?
- **Where `postDeploy` gets its teeth (E28):** it runs after traffic shift and can't revert, so its value is gating promotion — a failed post-deploy suite in staging marks the deployment degraded and the promotion gate refuses to advance it to production. Without this link `postDeploy` is fire-and-forget.

### E35. Deployment strategy `OPEN`
- Blue/green, rolling, canary, or immediate cutover? Per workload, per environment, or global policy?
- Determines blast radius and whether automated rollback on error threshold is possible. Multiple-revision mode (ADR 0013) enables these later without changing the model.

### E36. Test suite integration `PARTIAL`
- **Leaning so far (from E28):** `verify` is the blocking gate before traffic shift; `postDeploy` reports and feeds the promotion gate. Timeouts and retry are per-hook; pass/fail is the job's exit code.
- **Open:** nothing prevents a flaky suite in `verify`, blocking every deployment. "`verify` stays smoke-only in protected environments" is a convention, not a mechanism. Should the platform track `verify` flake rate per workload and surface it, rather than police it?

### E51. Platform-initiated redeploy `OPEN`
- Falls out of A1: policy and mapping changes apply only on a workload's next deploy, so compliance rollout is deploy-gated; a service unshipped for months runs stale resolution indefinitely.
- Needs a platform-initiated redeploy — same image, re-resolved definition, rolled out in waves. Same wave mechanism as B7: build once, use for both.
- **Open:** who authorises a fleet-wide redeploy; whether teams can defer; what happens when a policy-driven redeploy fails because the image no longer builds or tests now fail.

---

## F. Approvals, audit & governance

### F37. Approval model `OPEN`
- Unanimous or majority; fate of a rejected release; resubmission unchanged; notification, delegation, out-of-office; time limits and deadlock prevention; comments on rejection.

### F38. Why not Azure DevOps / GitHub environments for gates? `OPEN`
- They already give timestamped, identity-linked approvals with an audit trail.
- The answer must be about approvals spanning provisioning *and* deployment, with non-CI actors (QA, HoSD), against a release record crossing repos — and must be convincing, because a bespoke approval engine is where custom platforms usually die.

### F39. Audit scope, retention and access `OPEN`
- What is logged, for how long, who may read sensitive records such as secret change history.
- **Constrained by A2:** the release record holds the full submitted definition, so definition retention is bounded by audit retention, not a team repo's lifetime.
- The resolution chain needs per-field provenance (`{value, source, rule}`), not before/after snapshots, or the audit can't show *why* a value ended up as it did.

### F40. The DORA tension `OPEN`
- The platform case cites DORA on self-service while enshrining two manual human gates per release; DORA finds external approval bodies correlate with worse delivery performance.
- Needs an explicit, measured deprecation plan: digitise the gates so latency is visible, then delete them against a metric.

### F41. What is enforced in Azure Policy vs the platform? `PARTIAL`
- **Leaning so far:** guardrails belong to the landing zone; the platform is the paved road (ADR 0008). Anything enforced only in the merge function is defeated by portal access, so platform-side validation is UX, not control. Applied in ADR 0010 (`CanNotDelete` locks alongside `protect`).
- **Open:** the actual split — which rules are Azure Policy deny, which platform validation, which both.

---

## G. Visibility

### G42. Does the graph show intent or reality? `PARTIAL`
- **Leaning so far:** per ADR 0006 rider 1, drift is detected and reported continuously, never auto-corrected, so the graph shows intent, reality and the divergence between them. Drift detection makes the screen trustworthy (an earlier note also claimed it "answers B10"; the intended cross-reference is unclear).
- **Open:** read reality via scheduled `pulumi preview` (accurate about what a deploy would change; costs a plugin startup per stack) or Azure Resource Graph (cheap, fleet-wide, not expressed in plan terms)? Probably both, for different screens.

### G43. Portal deep-link permissions — see [ADR 0016](decisions/0016-portal-deep-links.md)

### G44. How much live data on the graph? `OPEN`
- "See exactly how the workload is working" implies health and latency, i.e. an observability integration.
- Cost per component is the highest-value addition and cheap via the Cost Management API.
- Scope-creep risk is real.

### G45. Build vs buy for the catalog layer `OPEN`
- Backstage and Port exist for exactly this; it is the least organisation-specific part.
- Argument for building: our graph is generated from our own resolution chain rather than hand-maintained catalog YAML — a better story, but it must be made explicitly.

---

## H. Platform as a product

### H46. Will Ops accept being a gatekeeper rather than an executor? `OPEN`
- The design succeeds or fails on this; a change-management problem, not a technical one.

### H47. Is the paved road faster than raising a ticket on day one? `OPEN`
- If not, adoption is zero regardless of architecture — and adoption is optional by design (A3).
- **Sharpened by A1:** every deploy pays infra reconciliation even when nothing changed; `azure-native` plugin initialisation is slow per stack (five resource-type stacks = five plugin startups on a no-op deploy). Most likely route to the road being slower than a ticket.
- Mitigations: hash config + template per stack and skip unchanged ones; warm plugin cache; parallelise independent stacks.

### H48. Platform SLO, on-call, and break-glass `OPEN`
- What happens when the platform is down and prod needs a hotfix? The answer cannot be "wait".
- **Sharpened by A3:** no standing data-plane access in stage/prod, so break-glass is the *routine* route to debugging a production database, not just an outage path. If slow or humiliating, people route around the platform. Needs fast, audited, time-bound PIM elevation.

### H49. Build capacity, bus factor, and exit path `OPEN`
- What is kept if the custom build is abandoned in eighteen months?
- Credible build/buy position: buy the commodity layers (Pulumi, CI, Key Vault, Azure Policy); build only the seam — the release object spanning provisioning, identity wiring, deployment and gates. See [ADR 0001](decisions/0001-custom-build-api-and-clients.md).

### H50. Definition schema evolution `OPEN`
- How team-authored definitions migrate across `apiVersion` changes, how many versions are supported at once, how validation errors reach teams so they can fix them unaided.
- **Shaped by A2:** under a push model validation must be available *before* upload (CLI or CI step against their own file), not only as a deployment-time rejection.
- **Changes to `workload.schema.json`** (1 and 2 applied in S01, the rest pending):
  1. **Remove `metadata.environment`** (A1, A2) — *applied (S01)*: currently required while its own description says the same definition deploys everywhere. Environment is a deployment parameter. Weaker same argument for `name` and `team` (registration identity; can drift if duplicated in the file).
  2. **Add `id` and `class` to `requires` items** (C12, C13) — *applied (S01)*: `id` optional, defaults to type name, unique within workload (checked in code), pattern `^[a-z][a-z0-9-]{1,14}[a-z0-9]$`.
  3. **Add a `hooks` block** (E28, E33): `preDeploy`, `verify`, `postDeploy`, each with `command`, optional `image` (defaults to workload image for `preDeploy`), `timeout` default 10 minutes, optional tier restriction, `destructive` on `preDeploy`.
  4. *Possible:* tighten the `metadata.name` pattern, since C14's naming budget does not close.
- C52's "type names the interface" rule was overridden by a user decision (S12a): `database` stays a type, the mapping picks the engine, and an `engine` export tells the code.

---

## R. Raised by the Viedoc use case

From [use-cases/viedoc-daybreak.md](use-cases/viedoc-daybreak.md) (2026-10-09). A real regulated suite
releasing ~40 applications together through 9 environments and 4 regions.

### C53. The unit of release: a pinned set of workloads `OPEN`
- Viedoc releases one immutable **deployment set** (all pinned versions, one label) and installs the same set
  everywhere. Our model deploys workloads one at a time (A1, A2).
- Proposed: a **release set** of workload definitions, image digests and workload-level `dependsOn`, under
  one label. The orchestrator deploys and switches in dependency order. Per-workload deploys become a set of
  one, so nothing is lost.
- Cross-workload *resource* references stay forbidden (D19). Ordering is not access.
- Open: whether a set may mix teams' workloads freely; how a set relates to a team's own environment.

### E52. Qualification evidence on every deploy `OPEN`
- Regulated customers need IQ (what is installed equals what was pinned, observed rather than recorded), OQ
  (each application meets one health contract) and smoke on every deploy; PQ and the regulatory suite on stage.
- Proposed: IQ = a refresh shows no drift, plus running image digests equal to pinned. The OQ contract is
  catalog data (the runtime mapping declares the probe). Results go on the release record with machine
  identity. Relates to E28 (`verify`) and E36.

### E53. Multi-region rollout `OPEN`
- An environment can have several regional instances (Viedoc: 4 regions, training then production each, China
  in a separate cloud). Rollout order and parallelism are data. Deploy-dark-then-switch already lets
  "deploy Monday, switch Japan Thursday" happen.
- Open: separate-cloud regions (credentials, agents, registry), and whether instances share one resolved graph.

### F54. Approvals with signature meaning `OPEN`
- Regulated approvals (test lead, PM, QA) must record who, when and what the signature means (21 CFR Part 11),
  bound to the exact set approved. Extends F37.
- Proposed: approval policy as data per environment tier (required roles, order). The approval record is
  immutable on the release record. Identity comes from Entra. Promotion refuses without it.

### F55. Release documents and evidence packages `OPEN`
- Viedoc produces 30 documents per release (13 in a customer-facing VIRP), and many need a registry number.
- Proposed: the release record is the evidence source. Document templates are versioned data like the catalog.
  A generator renders final documents with machine identity, immutable and filed. A numbering registry is data.
  Outside the core engine; consumes its records.

### F56. Work-tracking integration `OPEN`
- Release scope, PR gating on work-item completeness, and release notes all come from work items (Azure DevOps).
- Proposed: a work-tracking adapter feeding release readiness and documents. The platform does not become a
  work tracker.

### B57. Environment admission rules `OPEN`
- Which kinds of set an environment accepts (integration: latest main; regression and production: release sets
  only; dev: main plus a branch under test). Integration auto-deploys on merge.
- Proposed: admission policy as data, enforced by the orchestrator. Relates to B5 and B6.

### H58. Validating the platform itself `OPEN`
- In a GAMP 5 context the platform is a validated computerised system. Catalog CI, golden tests and fleet
  dry-run double as its validation evidence; records and the audit trail must be immutable (F39).
- Open: change control for catalog and policy changes (a policy edit is a change to a validated system).

---

## Suggested order

Blocking items first, then the differentiator, then scope control:

1. **B7** — substrate versioning (in progress; its prerequisite C11 is settled).
2. **D18** — data-plane grants. The differentiator and the deepest risk (D17 settled).
3. **B5** — what an environment is physically, including the subnet sizing E27 constrains.
4. **E31** — partial failure semantics, raised in priority by A1.
5. **C52** — the resolution layer. Load-bearing, unowned, constrains the schema.
6. Everything else.

*Settled (ADRs 0006–0016, all pending review): A1, A2, A3, B10, C11, C13, D17, E27, E28, E33, G43. Partial: B7, C12, C16, E32, E36, F41, G42.*
