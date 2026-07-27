---
name: decisions-and-approach
description: Key architectural and approach decisions made for the just-deliver IDP
metadata: 
  node_type: memory
  type: project
  originSessionId: 9a65fe8e-1427-4f53-a027-ed2afb2f629b
---

## Architecture: API + Client Apps

Decided on a dedicated backend API with multiple client surfaces (CLI for pipelines, web UI for visibility and approvals). Rationale: multiple distinct user types (Dev, Ops, QA, Management) with different interaction patterns; business logic and audit trail belong in one place.

## No Existing Vendor Covers the Full Need

Evaluated: Port.io, Backstage, Humanitec, Azure Deployment Environments. None fully covers the combination of:
1. Dynamic definition-driven provisioning (team + global policy merge)
2. Structured release lifecycle with enforced approval gates
3. IT Ops work orders and secrets handoff workflow

Humanitec is conceptually closest but still requires significant customisation. Custom build is the right call.

## Provisioning: Pulumi with Automation API

**Why Pulumi over Terraform or Bicep:**
- Automation API allows driving provisioning programmatically from within the backend — no subprocess/CLI shelling
- Real code (not HCL/templates) handles dynamic resource graphs naturally
- State backend: Azure Blob Storage (self-hosted, no Pulumi Cloud SaaS dependency)

**Never shell out to CLI from an API.** Enqueue a job → background worker executes via Automation API → status polled/pushed to clients.

## Two-Layer Definition Model

- **Team layer:** each team declares what their workload needs (database, keyvault, service bus, appinsights, etc.)
- **IT Ops global layer:** platform-wide policies applied to all workloads (e.g. all traffic through Azure Application Gateway)
- Both layers are merged at provisioning time

## Build B/C First, Design for D

**Key design principles to make D a smooth transition (not a rebuild):**
1. Separate intent from execution — tasks have an `executor` field (manual → automated), payload shape stays the same
2. Structured data over free text — infra requirements and secrets are typed records, not narrative docs
3. Secrets in a vault from day one (Azure Key Vault) — not stored in platform DB
4. Pluggable deployment actions — `trigger` field: `manual_instruction` now, `api_call` later
5. Never let work happen outside the platform — shadow processes break audit trail and block automation

**Transition stages:**
```
B/C        → Platform tracks intent, Ops executes manually
B/C+       → Platform generates precise runbooks, less meeting time
D-partial  → Automate low-risk tasks first (staging deploys, secret injection)
D          → Full automation, Ops handles approvals and exceptions only
```
