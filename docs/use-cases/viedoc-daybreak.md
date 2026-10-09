# Use case: Viedoc and Project Daybreak

> Status: investigation, 2026-10-09. Sources: the Daybreak kick-off deck (2026-08-28), the shaping-day
> deck (2026-08-18), Daybreak `PLAN.md` and the Delivery README. Source files are kept locally in
> `docs/real-world-use-case/` and are not committed (internal material). People are named by role.

## 1. Who they are

Viedoc builds a clinical-trial SaaS suite. It is **regulated**: GCP ICH E6, EMA guideline on computerised
systems, FDA 21 CFR Part 11 and PMDA, with GAMP 5 as the validation framework. Validation is the business:
*"without validation of our system there is no business."* Every release must produce evidence that the
system is in a validated state, and customers receive a **VIRP** (13 documents) so they can rely on it.

| Scale | |
|---|---|
| Products / packages / applications | 19 / ~39–52 / 40 (16 with a health endpoint today) |
| Environments | 9: dev (×7+), integration, externaltest (regression), stage, and training + production in 4 regions (one is China) |
| Production instances | 8 (4 regions × training + production) |
| Tooling | Azure DevOps (repos, work items, pipelines, Artifacts feed), Azure App Service with slots, Windows VMs (Worker), some Container Apps, Terraform moving to **Bicep**, MySQL + XPO/EF migrations, SharePoint, DocuSign, Teams |

## 2. How a release works today

Four phases: **active development → stabilisation (code freeze, translations, bundles, regression) →
qualification (handover, stage, IQ/OQ/PQ, approvals) → go live (training + production × 4 regions, VIRP,
down-merge).**

| Measure | Value |
|---|---|
| Activities mapped / manual | **201 / 183** |
| Dependencies / hand-overs between teams | 301 / 104; longest chain 42 activities |
| Stage release | ~1 day |
| Training + production, 4 regions | ~5 days (1–4 h per environment, China up to 1 day); 8 environments in sequence, two people throughout |
| End to end (after handover prep) | 6+ days; never timed |
| PQ | ~3 days: ~11 studies created by hand, 38 suite runs over 4 browsers in sequence (13.5–15 h), manual eTMF/Reports checks, screenshot evidence, signatures |
| Regression | ~175 person-hours, ~30% of a release |
| VIRP | 13 documents, many roles, ~1 week; file names and IDs typed by hand |
| Release notes | ~4 weeks door to door; EN/JP/ZH publishing |
| Release documents | 30; 15 need a number from a spreadsheet; produced on a Report Generator VM and edited by hand |
| Secrets / config | ~200 secrets typed by hand; a manual config gate on 12 of 34 packages (waits 4 days, then rejects) |

**Where it hurts** (consolidated from all walkthroughs):

1. **Hand-overs carry the release.** A configuration report, handover meeting and ops runbook are written
   from fragmented documents; runbook quality depends on chasing developers.
2. **Manual, timing-critical steps.** Copy-pasting bundle numbers between pipelines, approving each step of
   `all.deploy`, running SQL at exactly the right moment, stopping and starting the Worker on 5 VMs, running
   `Viedoc.Worker.exe -RT UpdateDatabase`, manual sanity checks. Repeated per bundle and per environment.
3. **Main is not releasable.** Work items are finished after merge; test cases run on externaltest after
   merge; defects surface from stage onwards.
4. **Evidence is assembled by hand.** IQ reads tags rather than the running system; OQ needs manual
   screenshots; PQ evidence is screenshots; documents are typed, numbered, signed and filed by hand.
5. **No source of truth for state.** Nobody knows what runs on a dev environment; "current IQ" is captured
   by hand; 32,933 of 32,937 package versions were never promoted.
6. **Limited access.** Developers cannot see non-dev logs (China at all), so all support routes through
   Platform.
7. **Individual knowledge.** Release notes, Learning, PQ and the platform release each depend on one or
   two people.

## 3. Their proposal: Project Daybreak (1 Sep – 30 Oct 2026)

**Goal:** from the test lead's regression approval to live and qualified in all four regions within 24 hours.
Hotfixes run the same chain, only smaller.

**Shape:** ~28 Azure DevOps pipelines in a new `Delivery` repository, chained by a release label:

```
build (per repo, PR gate on work items) → readiness → create (pin set, label, docs) → infra (Bicep)
→ regression env: deploy · migrate · tasks · IQ/OQ/smoke · automated suite → test lead approves
→ stage: deploy · IQ/OQ/smoke · PQ · regulatory → compile validation → PM then QA promote → VIRP
→ rollout: per region, training then production, each deployed and qualified → close (record deployed state)
```

**Principles:** one immutable **deployment set** per release, and the same set goes everywhere. The label
is the only input. Three **blocks** are deployed on slots in order (deploy, verify, switch). Qualifying is
part of deploying. Generated documents are final, carry machine identity and are filed by the pipeline.
Azure DevOps work items are the source of truth. Production refuses unpromoted packages. Each release
records the deployed state, and the next one reads it.

**What stays human, on purpose:** the test lead's judgement that regression is complete; PM then QA
approval; reviewing the release note; deciding what goes in a release and when.

**Explicitly out:** regression testing itself, translation, Learning, release-note authoring, buying an
orchestrator, AI as a required component, production container registry, full one-click provisioning.

**Open on their side:** what the orchestrator is (pipelines go green before the pipelines they trigger
finish); how to do IQ; the OQ contract across 40 applications; where secret values live; document numbering;
multi-repository work items; keeping dev and integration current (no owner); Container Apps quota.

## 4. Fit: their needs against our design

✅ covered by design · ◐ partly · ✗ missing. "MVP" means the change is now in the [MVP plan](../plans/mvp.md).

| # | They need | just-deliver today | Gap → action |
|---|---|---|---|
| 1 | **One pinned release set** across many workloads, one label, same set in every environment | ◐ per-workload deploys (ADR [0006](../decisions/0006-infra-deployed-with-app.md), [0007](../decisions/0007-definition-upload-snapshot.md)) | No multi-workload release. **Add a release set** (label + pinned definitions + image digests). New C53. **MVP** |
| 2 | **Deploy order and blocks**: deploy, verify, switch | ◐ dark revision + traffic shift per workload ([0013](../decisions/0013-container-apps-runtime.md), [0014](../decisions/0014-hook-points.md)) | No ordering across workloads. Declare `dependsOn` at workload level in the set; the orchestrator deploys and switches in dependency order. **MVP** |
| 3 | **Environment chain and promotion**; production refuses anything unpromoted | ✗ E34 open | Promotion of the same set between environments, gated. **MVP** (2 environments) |
| 4 | **Approvals as artifacts** (test lead, PM, QA; Entra groups) with signature meaning (Part 11) | ✗ F37 open | Approval policy as data per environment tier. Record identity, time and meaning on the release record. New F54. **MVP** (minimal) |
| 5 | **Qualify on every deploy** (IQ observed, OQ contract, smoke); PQ and regulatory on stage | ◐ verify/postDeploy hooks; health check | **IQ by observation**: refresh with no drift, plus running image digests equal to pinned. **OQ contract** as catalog data (the runtime mapping declares the probe). Evidence on the record. New E52. **MVP** (IQ + OQ) |
| 6 | **30 release documents and the VIRP**, generated, final, with machine identity and a numbering registry | ✗ | The release record is the evidence source. Document templates are catalog-like data; a generator renders from the record and from work items. New F55. Post-MVP; the MVP record carries machine identity now |
| 7 | **Scope from work items**; PR gate on work-item completeness; release notes from work-item fields | ✗ | Work-tracking adapter (Azure DevOps) feeding readiness and documents. New F56. Post-MVP |
| 8 | **Build contract**: artifacts, migrations, DB hash, test report, SBOM per package | ◐ upload = definition + image ([0007](../decisions/0007-definition-upload-snapshot.md)) | A workload version declares its evidence (test report, SBOM, migrations); readiness checks conformance. Release-set format reserves the fields. Post-MVP |
| 9 | **DB migrations** with history, hash verification, expand–contract, dev reconcile | ◐ [0015](../decisions/0015-database-migrations.md) (preDeploy job, app image) | Add DB-hash verification as qualification evidence; dev reconcile. Post-MVP |
| 10 | **~200 secrets and config declared**, no manual gate | ◐ identity-first; D22/D23 open | Secret *names* declared in the definition, values held in Key Vault and set through a controlled path; config from definition variables. Post-MVP |
| 11 | **IaC in Bicep**, drift detection, infra pinned with receipts | ◐ Pulumi proposed ([0002](../decisions/0002-pulumi-automation-api.md), [0003](../decisions/0003-pulumi-yaml-template-library.md)); drift rider in 0006 | **Conflict:** their tech leads chose Bicep. The resolver is engine-agnostic, so a Bicep backend (Azure Deployment Stacks) can be a second provider. **Decision for the user** |
| 12 | **Runtimes**: App Service with slots, Windows VMs (Worker, being migrated), some Container Apps | ◐ Container Apps only ([0013](../decisions/0013-container-apps-runtime.md)) | **Conflict:** an App Service runtime is needed to serve them; VMs are leaving anyway. E27's runtime port is the seam. **Decision for the user** |
| 13 | **Multi-region rollout**: training then production per region, regions in parallel, China a separate cloud; deploy and release decoupled | ✗ one region per environment | Environment *instances* per region; rollout order as data; deploy-dark-then-switch already decouples deploy from release. New E53. Post-MVP |
| 14 | **Orchestration**: one trigger, wait for completion, restart only what failed | ✅ durable step loop by design (E29, [flow](../architecture/workload_deployment_flow.html)) | This is their open T82. MVP proves the loop in-process |
| 15 | **Deployed-state record**; the next release reads it | ✅ resolved graph + deployment record per environment | MVP S17 |
| 16 | **Dev and integration kept current**; set kinds and guardrails (no feature build on regression or production) | ◐ B5/B6 open | Environment admission policy as data (which set kinds an environment accepts); integration auto-deploys on merge. New B57. Post-MVP |
| 17 | **Hotfix = same chain, smaller set**; PQ subset rule | ✅ same chain | Subset rule is data. Nothing to add |
| 18 | **Measurement**: phase timestamps for the 24 h clock | ◐ | Phase stamps on the release record. **MVP** S17 |
| 19 | **Status everyone can follow** (Teams), observed rather than reported | ◐ G42/G44 | Notifications adapter. Post-MVP |
| 20 | **The tool itself is validated** (GAMP 5) with a Part 11 audit trail | ◐ F39 open | Catalog CI, golden tests and fleet dry-run double as the platform's validation evidence; records immutable. New H58 |
| 21 | Developer access to non-dev logs (incl. China) | ✗ H48 | Break-glass / read access is part of the platform. Existing H48 |

**Outside the platform** (integration points only): regression testing, translation (Lokalise), Learning,
release-note authoring, browser matrix.

## 5. The end state, fully automated

People only define, decide and approve:

| Human step | Everything around it |
|---|---|
| A team writes or changes its **workload definition** and merges | PR gate checks work-item completeness; the build publishes the declared evidence; integration auto-deploys |
| Product decides **scope and date**; someone queues the **release label** | Readiness reads work items and conformance; the set is pinned; documents are generated preliminary |
| The **test lead approves** regression | Regression environment deployed and qualified automatically; the clock starts |
| **PM then QA approve** promotion (signed) | Stage deployed, qualified, PQ and regulatory run; validation package and VIRP generated from the record |
| Someone **reviews the release note** | Generated from work items; corrections go back to the source |
| — | Regions roll out in parallel (training then production), each qualified; deployed state recorded; status posted |
| An **exception** (red qualification, failed step) | The orchestrator stops, points at the step, and retries only it |

What the MVP proves of this: data-driven resolution, a multi-workload release set deployed in order,
qualification evidence on every deploy, and promotion between two environments behind a recorded approval.

## 6. Consequences for our design

- **New open questions** C53, E52, E53, F54, F55, F56, B57 and H58 are added to [open-questions.md](../open-questions.md).
- **Decisions to bring to the user** (ADR review): Bicep backend alongside or instead of Pulumi (0002/0003);
  App Service runtime beside Container Apps (0013); the deployment set as the unit of release (0006/0007).
- **ADR 0008/0009 (team-owned environments) do not describe Viedoc's shared stage and production.** The
  platform must also serve platform-owned shared environments that host many teams' workloads. C12's tiers
  already allow this; the ADRs should say so on review.
- **[MVP](../plans/mvp.md) changes:** release set, workload-level ordering, qualification record,
  approvals and promotion between two environments, phase timings.
