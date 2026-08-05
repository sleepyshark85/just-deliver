# Self-Serve Release Platform

## Current Situation

### Context
Multiple teams work on different applications in a suite. Every release requires coordination across Dev, IT Ops, QA, and Management through informal channels (meetings, emails, paper documents).

### Current Release Flow
1. Dev creates a release branch
2. CI runs and builds Docker image artifacts
3. Dev produces a manual document listing new secrets/config changes for IT Ops to fill in
4. Dev requests IT Ops to provision required infrastructure (DBs, secrets, DNS, etc.)
5. Dev and IT Ops hold a handoff meeting to transfer all required work
6. Head of Software Development signs off (paper) before UAT begins
7. IT Ops deploys to UAT; QA conducts testing
8. QA signs off (paper) after UAT completes
9. IT Ops deploys to production

### Key Pain Points
- Approvals are paper-based — no audit trail, easy to skip
- Secrets and infra requirements communicated via unstructured documents
- Handoff between Dev and IT Ops requires synchronous meetings
- No single source of truth for "where is this release right now?"
- IT Ops handles both infra provisioning and app deployment
- Environments created manually, inconsistently

---

## Suggested Approaches

### Option B — Release Lifecycle Tracker + Approval Gates
Structured release records with enforced approval gates. Deployments cannot proceed until approvals are recorded and IT Ops tasks are marked done. Replaces paper sign-offs with digital approvals (timestamped, identity-linked via SSO).

**Best for:** Eliminating approval ambiguity and getting a single source of truth.  
**Limitation:** IT Ops still executes manually; doesn't reduce their workload.

### Option C — Self-Serve for Dev, Structured Work Orders for Ops
Dev teams self-serve everything up to the handoff. The platform auto-generates a structured work order for IT Ops (infra tasks, secrets to fill, image to deploy) from the release — replacing the handoff meeting with an async, trackable checklist.

**Best for:** Reducing coordination overhead without changing how IT Ops executes.  
**Limitation:** Still depends on IT Ops bandwidth.

### Option D (End Goal) — Full Automation, Ops as Gatekeeper
Platform automates infra provisioning and deployment. IT Ops approves and triggers but does not manually execute. Secrets entered into the platform are injected automatically.

**Best for:** Maximum self-serve; IT Ops role shifts to approval and exception handling.  
**Limitation:** Highest investment; requires IT Ops buy-in and significant change management.

---

## Recommended Path: B/C → D

Start with B/C, designed to make D a smooth transition — not a rebuild.

### Design Principles
1. **Separate intent from execution** — model what needs to happen independently of how it happens. Swap manual executors for automated ones later without changing the data model.
2. **Structured data over free text** — capture infra requirements and secrets as typed records, not narrative docs. Automation can't run against prose.
3. **Secrets in a vault from day one** — store secret values in a secrets backend (Vault, AWS Secrets Manager, Azure Key Vault), not the platform DB. In D, apps pull directly from there.
4. **Pluggable deployment actions** — a deployment record has a `trigger` field: `manual_instruction` in B/C, `api_call` in D. Same record, different executor.
5. **Never let work happen outside the platform** — shadow processes break the audit trail and block future automation.

### Transition Stages
```
B/C        → Platform tracks intent, Ops executes manually
B/C+       → Platform generates precise runbooks, less meeting time
D-partial  → Automate low-risk tasks first (staging deploys, secret injection)
D          → Full automation, Ops handles approvals and exceptions only
```
