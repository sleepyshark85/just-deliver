# Open Questions & Known Problems

Register of everything not yet decided for just-deliver. IDs are stable global labels, not positions: new items take the next free number and keep their section letter, so references never shift.

**Status labels:** `OPEN` = nothing decided · `PARTIAL` = a position exists, gaps remain · `SETTLED` = a position has been written up as an ADR in [decisions/](decisions/).

A `PARTIAL` "leaning" is a working position, not a decision. When an item settles (an ADR, or it is built), its entry becomes a one-line pointer. ADR status: [decisions/README.md](decisions/README.md).

---

## A. Foundational model

These shape everything else.

### A1. Continuously reconciled or applied by a pipeline? — see [ADR 0006](decisions/0006-infra-deployed-with-app.md)
### A2. Where does a workload definition live? — see [ADR 0007](decisions/0007-definition-upload-snapshot.md)
Residual open point: registration flow — explicit registration before first deployment or implicit on it, and who may register.
### A3. Enforcement posture per environment tier — see [ADR 0008](decisions/0008-enforcement-in-landing-zone.md)

### A4. What must be identical between environments? — principle 10 ([context.md](context.md#design-principles)) and [ADR 0018](decisions/0018-environment-offers-runtime.md): one workload definition everywhere; differences live in the catalog's implementations per environment type

---

## B. Environments & landing zones

### B5. What is an environment, physically? `OPEN`
Subscription per environment, or resource group within a shared subscription?
- Affects blast radius, quota, policy scope, and environment creation speed.
- **Constrained by E27 ([ADR 0013](decisions/0013-container-apps-runtime.md)):** a Container Apps Environment needs a dedicated subnet whose minimum size depends on the environment type — an IPAM decision before any address space is allocated, or adding environments later means renumbering.
- Subscription creation needs billing-scope permissions and is capped per billing account.
- Azure Policy assignment takes time to propagate, so a fresh subscription is effectively unguarded for a window.
- "One landing zone per team" is a known anti-pattern; a subscription per team environment is reconcilable only if the real boundary is criticality rather than team, and that must be stated.
- What an environment offers and its type are catalog data ([ADR 0018](decisions/0018-environment-offers-runtime.md)); its physical form is what stays open here.

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

### B59. Landing zone shape `OPEN`
- How a landing zone is defined and provisioned: its definition format, management-group hierarchy, tenant model (one Entra tenant with administrative units, or several), budgets and the policy set per zone. Ownership is [ADR 0008](decisions/0008-enforcement-in-landing-zone.md)'s.

### B60. Network topology and address plan `OPEN`
- Hub-and-spoke per region (Virtual WAN, firewall, private DNS) or something lighter; the CIDR plan allocated up front (a Container Apps environment needs its own subnet, B5); a separate-cloud region such as China (E53).

---

## C. Resources & dependencies

### C11. Replacement detection and gating — see [ADR 0010](decisions/0010-replacement-protection-classes.md)
Two prerequisite spikes remain (when `protect` fails; whether Pulumi YAML can drive `options.protect` from config).

### C12. Shared vs dedicated resources `PARTIAL`
- **Built:** shared resources are named and defined once, and `jd env up` deploys them in the shared or the environment layer; each workload gets its own resources inside them (its container, its App Insights component) ([model.md](architecture/model.md)). The isolation dial is the environment, not the resource.
- **Settled within this item:** isolation is expressed as `class`, not by naming or `id`. "Dedicated" means its own *server* (noisy-neighbour isolation, compliance boundary, different engine version) — a different substrate tier, expressed by the requirement's `class` ([workload.schema.json](../schemas/workload.schema.json)). Example in [ADR 0011](decisions/0011-requirement-id.md).
- **Open:** which classes exist per resource type; whether `class: dedicated` in production requires approval given the cost difference.

### C13. Multiple resources of the same type — see [ADR 0011](decisions/0011-requirement-id.md)

### C14. Resource naming `PARTIAL`
- Names are catalog data ([naming.yaml](../catalog/naming.yaml)): a pattern, a maximum length and the allowed characters per target; a name over its target's length is cut and keeps its hash.
- **Open:** global uniqueness (Cosmos, storage) across deployments; name reclaim blocked by soft-delete (Key Vault, B6).

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

### C52. The resolution layer — built: [architecture/model.md](architecture/model.md), [ADR 0017](decisions/0017-catalog-layer-policies-require.md), [ADR 0019](decisions/0019-engine-knows-concepts.md)
- **Open:** changing the engine behind a component for an existing workload (a workload asks for `database`; the catalog decides the engine and exports it as `engine`) is a data-moving, breaking change that must route to approval (F54).

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
- Where does the Pulumi state encryption key live once state moves to Blob Storage? Local state is settled: jd generates its key in its data directory ([provisioning.md](architecture/provisioning.md)); a remote state needs the key kept elsewhere.

### D26. Identity cleanup on deletion `OPEN`
- **Reduced but not eliminated by D17:** system-assigned identities die with their resource, so no orphaned *identities*.
- Orphaned *role assignments* still accumulate (principals deleted by replacement or workload deletion) and count against D20's cap. A periodic sweep for assignments whose principal no longer resolves is required — a genuine audit finding if skipped.

---

## E. Deployment process

### E27. What runs the container? — see [ADR 0013](decisions/0013-container-apps-runtime.md)
The runtime choice moved to the environment: [ADR 0018](decisions/0018-environment-offers-runtime.md). Residual open point: Key Vault secret-reference refresh without a new revision (D22).
### E28. Where does the platform's engine stop? — see [ADR 0014](decisions/0014-hook-points.md)

### E29. Durable execution `OPEN`
- A deployment is a long-running, multi-step, resumable workflow; an in-memory loop in a background service loses it if the platform restarts between "infra provisioned" and "workload deployed".
- Needs checkpointed state, idempotent steps, step-level retry; candidate: one queue round trip per step.

### E30. Concurrency `OPEN`
- Two deployments of the same workload and environment at once. Pulumi's state lock errors rather than queues, so a per-(workload, environment) serialization point is needed (candidate: a queue session keyed on workload + environment).

### E31. Partial failure semantics `OPEN`
- Pulumi leaves partial state on a failed `up`; there is no transaction. "Deployment failed" ≠ "nothing changed".
- Needs a defined workload state after mid-sequence failure and re-entrant retry rather than restart-from-scratch.
- **Raised in priority by A1:** infra and app deploy in one operation, so the clean "provisioning failed, nothing deployed" state no longer exists.
- Candidate end states: live / degraded / failed-partial (infra kept, traffic unchanged, retry re-enters at the failed step).

### E32. Rollback `PARTIAL`
- **Leaning so far:** rollback reverts the app only, via a revision switch; infra is forward-only (ADR 0006 rider 2). Mechanism: multiple-revision mode, so rollback is a traffic-weight change to a warm previous revision (ADR 0013). Migrations are not rolled back, hence expand/contract (ADR 0015).
- **Open:** infra provisioned by a deployment that then failed at the app step — left in place (consistent with forward-only) or cleaned up?
- **Open:** how the UI communicates that a rollback restored the app but not the infrastructure deployed alongside it.
- **Revert:** going back to an earlier config redeploys it as a new revision (a new suffix, probed and shifted like any other), because Azure rejects a template change whose suffix names an existing revision. The traffic-switch rollback to a retained revision stays open.
- **Known limitation:** the release flow never deactivates old revisions. In multiple-revision mode a 0%-traffic revision stays active (scaled to zero, no cost) and deactivation is an API action Pulumi YAML cannot express, so rollback targets any retained revision but the active set only shrinks through `maxInactiveRevisions` once revisions become inactive. See [provisioning.md](architecture/provisioning.md).

### E33. Database migrations — see [ADR 0015](decisions/0015-database-migrations.md)
Residual open points: `timeout` ceiling in protected environments; verifying PITR retention before destructive migrations.

### E34. Promotion between environments — built: [architecture/approvals.md](architecture/approvals.md), `jd promote` ([usage.md](usage.md))
- **Open:** `postDeploy` results gating promotion (needs hooks, E28, [ADR 0014](decisions/0014-hook-points.md)).

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

### F37. Approval model `PARTIAL`
- Built: [architecture/approvals.md](architecture/approvals.md) — a gated environment requires a count of approvals from listed identities, each recording who, when and its meaning.
- **Open:** rejection and resubmission; delegation and out-of-office; time limits and deadlock prevention; notification.

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
- **Shaped by A2:** under a push model validation must be available *before* upload (`jd validate`), not only as a deployment-time rejection.
- A format change is a schema change, not a C# change ([ADR 0019](decisions/0019-engine-knows-concepts.md)); `metadata.environment` is gone and requirements carry `id` and `class` (ADR 0011).
- **Open schema change:** a `hooks` block (E28, E33): `preDeploy`, `verify`, `postDeploy`, each with `command`, optional `image` (defaults to workload image for `preDeploy`), `timeout` default 10 minutes, optional environment-type restriction, `destructive` on `preDeploy`.

---

## R. Raised by the Viedoc use case

From [use-cases/viedoc-daybreak.md](use-cases/viedoc-daybreak.md) (2026-10-09). A real regulated suite
releasing ~40 applications together through 9 environments and 4 regions.

### C53. The unit of release: a pinned set of workloads — built: [architecture/release.md](architecture/release.md)
- **Open:** whether a set may mix teams' workloads freely; how a set relates to a team's own environment.

### E52. Qualification evidence on every deploy — built (IQ, OQ): [architecture/release-record.md](architecture/release-record.md)
- **Open:** smoke, PQ and the regulatory suite on stage (E28, E36).

### E53. Multi-region rollout `OPEN`
- An environment can have several regional instances (Viedoc: 4 regions, training then production each, China
  in a separate cloud). Rollout order and parallelism are data. Deploy-dark-then-switch already lets
  "deploy Monday, switch Japan Thursday" happen.
- Open: separate-cloud regions (credentials, agents, registry), and whether instances share one resolved graph.

### F54. Approvals with signature meaning `PARTIAL` — extends F37
- Built: who, when and meaning, bound to the exact set approved and kept on the release record; promotion refuses without it ([architecture/approvals.md](architecture/approvals.md)).
- **Open:** required roles (test lead, PM, QA) instead of listed identities, and their order; approval expiry; delegation; per-person Entra sign-in (the approver today is a service principal); e-signature (21 CFR Part 11).

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
