# Context: why just-deliver exists

just-deliver is a custom Internal Developer Platform (IDP) for an organisation where multiple application teams build a suite of applications on Azure. This page covers the problem, the people involved, the goals and the chosen path. Decisions are recorded in [decisions/](decisions/); open issues are in [open-questions.md](open-questions.md).

## Current release flow

Every release is coordinated across Dev, IT Ops, QA and Management through meetings, emails and paper documents.

1. Dev creates a release branch.
2. CI runs and builds Docker image artifacts.
3. Dev writes a manual document listing new secrets/config changes for IT Ops to fill in.
4. Dev asks IT Ops to provision required infrastructure (DBs, secrets, DNS, etc.).
5. Dev and IT Ops hold a synchronous handoff meeting to transfer the work.
6. Head of Software Development (HoSD) signs off on paper before UAT begins.
7. IT Ops deploys to UAT; QA tests.
8. QA signs off on paper after UAT.
9. IT Ops deploys to production.

## Pain points

| Pain point | Effect |
|---|---|
| Paper approvals | No audit trail, easy to skip; compliance evidence collected by hand |
| Secrets and infra needs sent as unstructured documents | Error-prone; nothing can be automated against prose |
| Synchronous Dev/IT Ops handoff meetings | Every release waits on a meeting |
| No single source of truth | Nobody can answer "where is this release right now?" |
| IT Ops does both infra provisioning and app deployment | Ops is a bottleneck; adding teams makes it worse, not better |
| Environments created by hand | Inconsistent environments |
| Split ownership (Dev owns code; Ops owns infra, secrets, deployments, config) | Unclear accountability when production breaks |
| Knowledge lives in people | Onboarding depends on tribal knowledge; slow ramp-up |

This is classic "traditional DevOps": Ops is the executor, Dev hands off, work moves through tickets. It works at small/medium scale and breaks down as the number of teams grows (Ops bottleneck, rising context-switching cost). An IDP replaces this with "paved roads": the platform team builds self-serve capabilities, treats them as a product with dev teams as users, and moves from executor to enabler that sets guardrails. DORA research links developer self-service to higher deployment frequency and lower change failure rate. IDPs are becoming standard at organisations with 10+ product teams (Backstage, Port and Humanitec exist because of this demand), though most companies are still mid-transition and few have a mature IDP. Building one is catching up with the industry, not getting ahead of it.

## Stakeholders

| Stakeholder | Today | With the platform |
|---|---|---|
| Dev teams | Write code, hand off to IT Ops | Use the platform; own their application end to end within guardrails |
| IT Ops | Provision infra, manage secrets, deploy every release | Gatekeeper/enabler: set policy and guardrails, approve, handle exceptions |
| QA | Test in UAT, paper sign-off after UAT | Digital sign-off recorded in the platform |
| Head of Software Development (HoSD) | Paper sign-off before UAT | Digital sign-off recorded in the platform |
| Management | Little visibility, depends on manual gates | Visibility through audit trail, metrics and policy enforcement |

## Goals

**Organisation**
- Teams release without waiting on manual handoffs or Ops.
- Full governance: configurable deployment processes with quality gates and approval gates.
- Audit trail, compliance and easy rollback that happen automatically.
- Resources follow organisational best practice (security and other aspects).
- Teams use their resources and quota sensibly; teams' resources do not interfere with each other.

**Dev teams**
- Create and tear down environments quickly, without provisioning requests.
- Deploy workloads quickly and easily within policy.
- Secrets injected automatically in every environment, no manual per-environment setup.
- One source of truth for release status, approvals and audit trail.

**Platform team (IT Ops)**
- Move from executor to enabler: define guardrails once, the platform enforces them for every team.
- Quickly set up a master landing zone for organisation-wide shared services (VPN, Entra, etc.).
- Quickly provision isolated team landing zones with no cross-team interference.
- Scale governance without adding headcount: security baselines, quota limits and compliance policies enforced automatically, not by manual review.
- Less toil: stop approving every deployment and debugging ad-hoc configuration; set policy in advance.
- Visibility into resource usage, deployment patterns and compliance across teams without approving every request.
- Change governance rules easily and have them apply immediately to new deployments.

## Management case

- **Ownership.** A team truly owns an application when it is responsible for, and able to act on, the whole stack: code, infrastructure, secrets/config, availability and performance, and the release lifecycle. Teams can only fairly be held accountable for production outcomes when they have the tools to influence them.
- **Empowered ownership, not autonomy.** Autonomy means doing anything. Empowered ownership means doing anything inside a clear, safe boundary. The platform enforces the boundary (guardrails set by IT Ops and Management); the team owns everything inside it.
- **Speed and quality.** No tickets to change config or scale a service, so decisions that take days take minutes. Teams that feel the pain of a bad release build more carefully. Less time spent chasing status across Ops, Dev and QA.
- **Compliance.** The platform keeps a structured, tamper-evident audit log automatically (who approved what, when, from which identity), so collecting evidence for auditors is straightforward.
- **Onboarding.** Documented, self-serve workflows ("the paved road") replace tribal knowledge, so new teams and engineers ramp up much faster.
- **Approval gates.** HoSD and QA sign-offs exist because management lacks visibility and confidence. As the platform matures, automated policy enforcement, metrics and audit trails can replace some manual checkpoints, so governance happens through the platform rather than through meetings and paper.

## Options considered

| Option | What it is | Best for | Limitation |
|---|---|---|---|
| B: Release lifecycle tracker + approval gates | Structured release records; deployment blocked until approvals are recorded and IT Ops tasks are done. Digital approvals (timestamped, tied to an SSO identity) replace paper | Removing approval ambiguity; single source of truth | IT Ops still does the work by hand; their workload does not drop |
| C: Self-serve for Dev, structured work orders for Ops | Dev self-serves up to the handoff; the platform generates a structured work order for IT Ops (infra tasks, secrets to fill in, image to deploy), an async, trackable checklist that replaces the meeting | Less coordination without changing how IT Ops works | Still limited by IT Ops capacity |
| D (end goal): Full automation, Ops as gatekeeper | Platform provisions infra and deploys; IT Ops approves and triggers but does not do the work. Secrets entered into the platform are injected automatically | Maximum self-serve; IT Ops handles approvals and exceptions | Biggest investment; needs IT Ops buy-in and significant change management |

**Chosen path: build B/C first, designed so that moving to D is a smooth transition, not a rebuild.** See [ADR 0005](decisions/0005-build-bc-first-design-for-d.md).

### Transition stages

```
B/C        -> Platform tracks intent, Ops executes manually
B/C+       -> Platform generates precise runbooks, less meeting time
D-partial  -> Automate low-risk tasks first (staging deploys, secret injection)
D          -> Full automation, Ops handles approvals and exceptions only
```

### Design principles

1. **Separate intent from execution.** Model what needs to happen separately from how it happens. Tasks have an `executor` field (manual, later automated) and the payload shape stays the same, so manual executors can be swapped for automated ones without changing the data model.
2. **Structured data over free text.** Infra requirements and secrets are typed records, not narrative documents. Automation cannot run against prose.
3. **Secrets in a vault from day one.** Secret values go in a secrets backend (Azure Key Vault for this platform; Vault and AWS Secrets Manager were listed as generic examples), never in the platform DB. In D, apps read them directly from the vault.
4. **Pluggable deployment actions.** A deployment record has a `trigger` field: `manual_instruction` in B/C, `api_call` in D. Same record, different executor.
5. **Never let work happen outside the platform.** Shadow processes break the audit trail and block future automation.

## Related

- Decisions: [0001 custom build](decisions/0001-custom-build-api-and-clients.md), [0002 Pulumi Automation API](decisions/0002-pulumi-automation-api.md), [0004 two-layer definitions](decisions/0004-two-layer-definition-model.md), [0005 B/C first](decisions/0005-build-bc-first-design-for-d.md)
- Architecture: [landing-zone.md](architecture/landing-zone.md), [provisioning.md](architecture/provisioning.md), [workload-definition.md](architecture/workload-definition.md)
- Plans: [mvp.md](plans/mvp.md)
