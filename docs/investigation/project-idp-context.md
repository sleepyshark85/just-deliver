---
name: project-idp-context
description: "Core context for the just-deliver IDP project — what we're building, why, and the current state of the organisation"
metadata: 
  node_type: memory
  type: project
  originSessionId: 9a65fe8e-1427-4f53-a027-ed2afb2f629b
---

We are building a custom Internal Developer Platform (IDP) called "just-deliver" for an organisation running multiple application teams on a suite of applications. The platform targets Azure infrastructure.

**Why:** The current process is manual — paper approvals, unstructured handoff docs, synchronous meetings between Dev and IT Ops, no audit trail, and no single source of truth for release status.

**Key stakeholders:**
- Dev teams — consume the platform, own their applications end to end
- IT Ops — currently own infra provisioning and app deployment; will shift to approver/enabler role
- QA — sign off post-UAT
- Head of Software Development (HoSD) — signs off pre-UAT

**Current release flow:**
1. Dev creates release branch
2. CI builds Docker image artifacts
3. Dev produces manual secrets/config document for IT Ops
4. Dev requests IT Ops to provision infra (DBs, secrets, DNS, etc.)
5. Synchronous handoff meeting with IT Ops
6. HoSD signs off (paper) → UAT begins
7. QA signs off (paper) → prod deployment
8. IT Ops deploys to production

**End goal:** Full self-serve platform where Dev teams define what their workload needs, the platform provisions it, and IT Ops acts as gatekeeper rather than executor.

**Transition path:** B/C (lifecycle tracker + structured work orders) → D (full automation). See [[decisions-and-approach]] for details.

**Documentation files in repo:**
- `PLATFORM.md` — current situation, options B/C/D, transition principles
- `PROVISIONING.md` — dynamic definition-driven provisioning design
- `IDP-VS-TRADITIONAL-DEVOPS.md` — IDP vs traditional DevOps, management case
