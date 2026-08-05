# Cloud Landing Zone — Comprehensive Guide

---

## 1. What is a Landing Zone?

A landing zone is a **pre-configured, governed, and secure cloud environment** that serves as the foundation for deploying workloads. It is not a product or a service you buy — it is a **pattern and a set of configurations** applied on top of cloud infrastructure.

> Think of it like a pre-built office unit. The building (cloud) provides shared infrastructure — power, plumbing, security. Each unit (landing zone) is fitted out differently: one is a lab, one is a call center, one is a secure vault. Teams move in and start working immediately.

A landing zone is **purely logical** — there is no dedicated physical hardware. Isolation is achieved through software-defined boundaries: subscriptions, policies, IAM, and networking.

---

## 2. Why Not Just a Subscription?

A bare subscription gives you isolation, but not governance. Handing a team a subscription without a landing zone is like giving them an empty plot of land and saying "build your office here."

| Bare Subscription | Landing Zone |
|---|---|
| Empty, blank slate | Pre-wired and ready |
| Team sets up everything | Standards enforced from day one |
| Inconsistent across teams | Consistent across the org |
| Security is optional | Security is mandatory |
| You hope teams follow rules | Guardrails prevent mistakes |

---

## 3. Landing Zone vs Physical Infrastructure

| Aspect | Detail |
|---|---|
| **Nature** | 100% logical — policies, configurations, boundaries |
| **Physical layer** | Shared cloud provider hardware underneath |
| **Isolation mechanism** | RBAC, Azure Policy, VNets, subscription boundaries |
| **Exception** | Dedicated Hosts or Sovereign Cloud for extreme isolation needs |

---

## 4. Popular Use Cases

- **Enterprise cloud migration** — consistent foundation for large-scale workload moves
- **Multi-account/subscription governance** — centrally enforce standards across hundreds of accounts
- **Regulatory compliance** — HIPAA, PCI-DSS, FedRAMP baked in from day one
- **DevOps & CI/CD enablement** — teams get ready-to-use environments, no setup required
- **Hybrid cloud connectivity** — secure bridge between on-premises and cloud
- **Mergers & acquisitions** — onboard acquired company workloads without inheriting security debt
- **Data & analytics hubs** — governed data lakes with controlled access and residency
- **Disaster recovery** — pre-built secondary environments meeting the same standards as production

---

## 5. Landing Zone Boundaries

### The Core Principle

> A boundary should reflect a **meaningful difference in governance, risk, or operational requirements** — not just team or project boundaries.

### Boundary Options in Azure

| Boundary | Isolation Level | Best For |
|---|---|---|
| Resource Group | Weak | Logical grouping only |
| **Subscription** | **Strong** | **Most organizations — recommended** |
| Management Group | Strongest | Governing multiple landing zones |

### How They Work Together

```
Management Group          →  "Sets the law for the kingdom"
  └── Subscription        →  "The walled city" — the true LZ boundary
        └── Resource Group →  "Neighborhoods inside the city"
```

### When to Create a Separate Landing Zone

Separate when two workloads differ in:
- Compliance or regulatory requirements
- Risk tolerance or blast radius
- Network topology needs
- Operational ownership
- Environment lifecycle (always separate prod from non-prod)

### Common Anti-Patterns

| Anti-Pattern | Problem |
|---|---|
| One LZ per team | Explosion of zones, hard to govern |
| One LZ for everything | No isolation, compliance nightmare |
| Separating by application | Too granular, operational overhead |
| Separating by tech stack | Wrong driver — governance should drive boundaries |

### Recommended Starting Structure

```
Root
├── Platform          (connectivity, identity, management)
├── Regulated         (compliance-sensitive workloads)
├── General Production
├── Non-Production
└── Sandbox           (experimental, isolated, auto-expiring)
```

---

## 6. Landing Zone Responsibilities

### The Principle

> A landing zone is responsible for everything **beneath the workload**. It ends where workload-specific concerns begin.

### Owns

- **Identity & access foundation** — SSO integration, role definitions, managed identities
- **Network foundation** — VNets, hub-spoke topology, DNS, firewall, on-premises connectivity
- **Security baseline** — mandatory policies, encryption standards, threat detection
- **Compliance & governance** — policy enforcement, regulatory guardrails, tagging standards
- **Observability foundation** — centralized logging, audit trails, baseline alerting
- **Cost management** — budgets, spending alerts, quota management

### Does Not Own

- Application code and business logic
- Application-level security (auth, input validation)
- Workload-specific monitoring and dashboards
- Database schemas and data management
- CI/CD pipelines for applications

### Quick Test

> *"Would every workload in this zone need this, regardless of what it does?"*
> - Yes → Landing zone responsibility
> - No → Workload responsibility

---

## 7. Identity — Entra ID & Tenant Design

### Key Concept

Entra ID is **tenant-scoped**, not subscription-scoped. All subscriptions under one tenant share the same Entra ID. The distinction is:

```
Entra ID (Tenant Level)  →  WHO someone is
RBAC (Subscription Level) →  WHAT they can do and where
```

### Isolation Within a Single Tenant

| Tool | What It Provides |
|---|---|
| **Administrative Units (AU)** | Scoped identity admins per landing zone |
| **RBAC scoping** | Users access only their assigned subscriptions |
| **Directory enumeration restrictions** | Users cannot browse or discover other users |
| **Conditional Access** | Zone-specific access rules and MFA policies |
| **Multiple custom domains** | Zone-specific UPNs (john@zone-a.com vs john@zone-b.com) |

### Domain Flexibility

A single Entra ID tenant supports multiple verified domains — they do not need to be subdomains:

```
Single Tenant
├── payments-corp.com          ← separate root domain
├── zone-a.company.com         ← subdomain
└── acquired-company.com       ← entirely different domain
```

You must prove DNS ownership of any domain added.

### Same Username, Different Domains

`john@zone-a.com` and `john@zone-b.com` are **completely separate identities** in Entra ID. The full UPN is what makes a user unique — not just the username prefix.

### Single Tenant vs Multiple Tenants

| Requirement | Single Tenant | Multiple Tenants |
|---|---|---|
| Separate subscription access | ✅ via RBAC | ✅ |
| Scoped identity admins | ✅ via Admin Units | ✅ |
| Users can't see each other | ⚠️ Requires hardening | ✅ Complete |
| Dedicated domain per zone | ✅ Multiple domains | ✅ |
| Shared global admin acceptable | ✅ | ❌ Not applicable |
| Complete identity boundary | ❌ | ✅ |

**Use multiple tenants only for:** regulated/sovereign environments, M&A isolation, MSPs managing separate clients, or when a shared global admin is not acceptable.

---

## 8. Controls Available Per Landing Zone

### Cost Controls
- Budgets with tiered alerts (50% / 80% / 100%)
- Mandatory cost allocation tags
- VM size limits to prevent abuse
- Auto-shutdown schedules for non-production VMs
- Resource expiry policies

### Security Controls
- Azure Policy — deny non-compliant resource creation
- Microsoft Defender for Cloud
- No public IP enforcement on sensitive resources
- Just-in-time VM access via PIM
- MFA and Conditional Access enforcement

### Resource Limits
- Allowed resource types per zone
- Allowed regions
- Maximum VM sizes and counts
- Azure Quota limits per subscription

### Network Controls
- Inbound/outbound traffic rules
- Blocked destinations (production networks, malicious IPs)
- Private endpoint enforcement
- DNS and routing controls

### Policy Enforcement Modes

| Mode | Behavior | Use Case |
|---|---|---|
| Deny | Blocks non-compliant creation | Hard security requirements |
| Audit | Flags but allows | Monitoring, gradual rollout |
| Append | Adds required settings | Tag enforcement |
| Modify | Auto-remediates resources | Fixing existing resources |
| DeployIfNotExists | Deploys companion resources | Ensuring diagnostics enabled |

### Control Inheritance

Controls cascade downward and cannot be overridden by lower levels:

```
Management Group policy → applies to all subscriptions below
  └── Subscription policy → applies to all resource groups below
        └── Resource Group policy → applies to all resources
```

---

## 9. Setting Up a Landing Zone — Steps

### Step 1 — Define Requirements
Answer before touching the cloud: compliance standards, number of environments, connectivity needs, ownership model.

### Step 2 — Set Up Management Hierarchy
Azure: Management Groups → Subscriptions. This is the skeleton everything hangs off.

### Step 3 — Configure Identity & Access
Centralized identity provider, role definitions, least privilege enforcement.

### Step 4 — Establish Networking
Hub-spoke topology, VNet segmentation, DNS, firewall rules, on-premises connectivity.

### Step 5 — Apply Security Guardrails
Azure Policy assignments, threat detection, encryption standards.

### Step 6 — Enable Logging & Monitoring
Centralized Log Analytics Workspace, audit trails, alerting.

### Step 7 — Set Up Cost Management
Budgets, alerts, tagging policies, quota limits.

### Step 8 — Deploy via Infrastructure as Code
Never set up a landing zone manually. Use IaC for repeatability:
- Azure → Azure Landing Zone Accelerator (Bicep / Terraform)
- AWS → Control Tower + Account Factory
- GCP → Cloud Foundation Toolkit

### Step 9 — Test & Validate
Compliance scans, network connectivity tests, security scenario simulations.

### Step 10 — Onboard Workloads & Teams
Self-service provisioning, operating model documentation, team training.

---

## 10. Infrastructure as Code Coverage

IaC handles approximately **95%** of landing zone setup. The remaining 5% requires a one-time manual bootstrap.

### What IaC Handles
Networking, policies, IAM roles, logging, subscription creation, security configurations, cost management, management hierarchy.

### Minimal Manual Bootstrap (Azure)

| Step | Why Manual |
|---|---|
| Create Azure tenant & first Global Admin | Nothing exists yet |
| Create service principal for IaC | IaC needs an identity to run as |
| Create storage account for Terraform state | IaC needs a state backend |
| Link billing account | Portal-only in most cases |

The entire manual process takes **under 30 minutes** and happens **only once**. After that, everything is code-driven.

---

## 11. Landing Zone as a Cloud Provider Service?

Landing zones are a **concept and pattern**, not a native billable service. Cloud providers offer accelerators and frameworks to help build them.

| Provider | Offering | Notes |
|---|---|---|
| **Azure** | Azure Landing Zone Accelerator, CAF | Reference architecture + Bicep/Terraform templates |
| **AWS** | AWS Control Tower, Account Factory | Closest to a managed LZ service |
| **GCP** | Cloud Foundation Toolkit, Setup Hub | Terraform templates + guided setup |

You always own and operate the landing zone. Cloud providers give you blueprints and pre-cut materials — you still build and maintain the house.

---

## 12. IDP Integration

An Internal Developer Platform should **orchestrate** landing zone provisioning, not own the landing zone definition.

```
Platform Team owns:       IDP owns:
├── IaC templates         ├── Self-service portal/API
├── Guardrails & policies ├── Workflow & approvals
└── Network topologies    ├── Triggering IaC pipelines
                          └── Reporting status to requester
```

### Developer Flow via IDP

```
Developer requests environment in IDP portal
  → IDP validates against zone policies
  → IDP triggers IaC pipeline
  → Landing zone provisioned in Azure
  → Developer notified with access details
```

An IDP without environment provisioning is just a documentation portal. Landing zone self-service is what makes it a true platform.

---

## 13. Developer Landing Zone Configuration

### Core Philosophy

> Give developers maximum autonomy within a safe sandbox. The landing zone itself is the guardrail — not the approval process.

### What "Dangerous" Actually Means

**Block these (truly dangerous):**
- Exposing sensitive data publicly
- Connecting to production systems
- Storing real customer/PII data
- Creating backdoor access to other zones
- Disabling audit logging
- Cost abuse (crypto mining, oversized resources)

**Allow these (merely uncomfortable):**
- Deploying unusual resource types
- Making configuration mistakes in their own environment
- Spending more than expected (with alerts)
- Breaking their own environment
- Trying experimental architectures

### Access Model

```
Developers get:
├── Contributor on their subscription      ✅
├── User Access Admin scoped to their RG   ✅
└── Key Vault Secrets Officer              ✅

Developers cannot:
├── Modify subscription policies           ❌
├── Access other teams' resource groups    ❌
├── Modify network hub/firewall            ❌
└── Connect to production networks         ❌
```

### Network Setup

```
Hub (Platform team)          Dev Spoke (Developer team)
├── Firewall             →   ├── VNet with pre-configured subnets
├── DNS                      ├── Outbound internet → Allowed (logged)
└── No prod connection        ├── Inbound internet → Denied by default
                             └── Production connection → Hard blocked
```

### Policy Configuration

| Policy Type | Examples |
|---|---|
| Hard Deny | No public IPs on DBs, no unencrypted storage, no VM sizes above threshold, no prod network peering |
| Audit Only | Unused resources, non-standard SKUs, resources without tags |
| Auto-Remediate | Apply mandatory tags, enable diagnostics, enable Defender on new VMs |

### Cost Controls

```
Budget: $X/month
├── 50% alert  → Notify team lead (informational)
├── 80% alert  → Notify platform team (review)
├── 100% alert → Notify everyone (no auto-stop)
└── VM size limit → e.g. max 8 cores (prevents GPU abuse)
└── Auto-shutdown → VMs stop at 8PM, restart 8AM (overridable)
└── Resource expiry → auto-delete after 120 days unless renewed
```

### The Golden Rule

> **If a mistake is recoverable and contained within the dev zone — allow it.**
> **If a mistake could impact production, cause significant cost, or expose sensitive data — block it.**

---

## 14. Demo Setup on Azure — Cost Estimate

### Minimal Demo Architecture
- 1 Entra ID tenant (free)
- 2 custom domains (you own them)
- 2 Administrative Units
- 2 subscriptions (one per landing zone)
- VNet, NSG, Key Vault, Storage Account, Log Analytics per zone

### Cost Breakdown

| Scenario | Estimated Monthly Cost |
|---|---|
| No VMs, no custom domains | ~$0–5 |
| Full demo with custom domains + Entra ID P1 | ~$26 |
| Full demo with VMs + custom domains + P1 | ~$40–50 |
| Using Azure free account credits | $0 |

### Entra ID Licensing

| Tier | Monthly Per User | Key Features |
|---|---|---|
| Free | $0 | Users, groups, AUs, custom domains, basic MFA |
| P1 | ~$6 | Conditional Access, self-service password reset |
| P2 | ~$9 | Full PIM, Identity Protection, Access Reviews |

> **Quickest start:** Azure free account gives $200 in credits for 30 days — enough to build and demonstrate everything. Spin VMs up for demos, tear them down after. Identity and policy configuration costs virtually nothing to keep running.

---

## 15. Summary — Key Principles

| Principle | Detail |
|---|---|
| **Logical, not physical** | Landing zones are configuration boundaries, not hardware |
| **Subscription is the boundary** | The natural, recommended isolation unit in Azure |
| **IaC everything** | 95%+ automatable; manual bootstrap is a one-time, 30-minute task |
| **Governance drives boundaries** | Not org charts, not applications, not tech stacks |
| **Start broad, split later** | Easier to divide one zone than merge two |
| **Identity is tenant-scoped** | RBAC controls access per subscription; Entra ID is shared |
| **Dev zones maximize freedom** | Block only truly dangerous things; let developers self-serve |
| **IDP orchestrates, IaC builds** | Clean separation of concerns between portal and infrastructure |
| **Day-2 is part of the job** | Landing zones require ongoing drift detection, patching, and versioning |