# Landing Zone Definitions

A **Landing Zone Definition** is a declarative specification that a team submits to the IDP to request their cloud environment. It describes **what** the team needs, **not how** it will be built.

---

## What is a Landing Zone Definition?

A landing zone definition is a YAML file that sets up the **shared platform foundation** a team needs:

- **Identity & access:** Who can access the zone and what they can do
- **Subscriptions:** How many environments (dev/test/prod) and where they live
- **Networking:** VNets, subnets, security rules, peering to hub
- **Governance:** Compliance requirements, cost budgets, tagging policies
- **Security:** Encryption, threat detection, privilege access management
- **Observability:** Logging, monitoring, alerting foundation
- **Policies:** What teams can and cannot do within their zone

## What is NOT in Landing Zone Definition

Landing zones do **not** include application workloads:
- ❌ Databases
- ❌ App Services or Functions
- ❌ Storage Accounts
- ❌ Application-specific configurations

These are provisioned separately via **workload definitions** — which is different from landing zone definitions. A landing zone is the foundation; workloads are the applications that run on it.

## Why Not Just Use Azure Portal?

Manual portal provisioning is:
- **Inconsistent** — different teams set up resources differently
- **Error-prone** — easy to miss security, compliance, or tagging requirements
- **Not auditable** — no record of who created what and why
- **Slow** — setting up each resource one-by-one takes hours
- **Impossible to version control** — configuration changes aren't tracked

A definition file solves all of these.

---

## How It Works: The Two-Layer Model

The IDP uses a **two-layer definition model**:

```
Team Layer (what they submit)
├── Identity & access groups
├── Subscriptions (dev/test/prod)
├── Networking topology
├── Workload-specific resources
└── Budget & cost controls

    ↓ [IDP merges]

Global IT Ops Layer (platform-wide policies)
├── All traffic routes through Azure Application Gateway
├── All logs sent to centralized Log Analytics
├── All Key Vaults use customer-managed keys
├── All VNets connected via Azure Virtual WAN
├── Mandatory tagging on all resources
├── All databases use Azure Defender
└── ...

    ↓ [Output]

Complete Landing Zone Configuration
├── Merged definitions
├── Global policies applied
├── Ready for Pulumi provisioning
└── Deployed to Azure
```

**Teams don't need to know about** or duplicate global policies — they just declare what they need, and the platform adds the organization-wide governance automatically.

---

## Structure of a Landing Zone Definition

### Top-level sections:

| Section | Purpose |
|---------|---------|
| `metadata` | Name, owner, timestamps |
| `spec.identity` | Entra ID groups, domains, conditional access |
| `spec.governance` | Compliance, budgets, tagging, cost controls |
| `spec.subscriptions` | Dev/test/prod subscriptions |
| `spec.networking` | VNets, subnets, security rules, DNS |
| `spec.security` | Policies, encryption, threat detection |
| `spec.observability` | Logging, monitoring, alerting |
| `spec.resources` | Databases, key vaults, app services, etc. |
| `spec.policies` | What teams can and cannot do |
| `spec.automation` | How Pulumi should provision this zone |
| `spec.dependencies` | What this zone depends on |

---

## Example: Team A Submits a Landing Zone Definition

Team A submits a single definition file requesting:
- 3 subscriptions (dev, test, prod)
- Entra ID groups for developers and DevOps engineers
- PostgreSQL database, Key Vault, App Service
- €5,000/month budget with alerts at 50%, 80%, 100%
- SOC2 and GDPR compliance requirements
- Centralized logging to shared Log Analytics workspace

```yaml
apiVersion: "v1"
kind: "LandingZoneDefinition"

metadata:
  name: "lz-team-a"
  owner:
    team: "Team A"
    email: "team-a-leads@litmos.com"

spec:
  subscriptions:
    - name: "sub-team-a-dev"
      environment: "development"
    - name: "sub-team-a-test"
      environment: "test"
    - name: "sub-team-a-prod"
      environment: "production"

  identity:
    domain: "team-a.litmos.com"
    accessGroups:
      - name: "grp-team-a-developers"
        roles: ["Contributor"]
      - name: "grp-team-a-devops"
        roles: ["Owner"]

  governance:
    compliance: ["SOC2", "GDPR"]
    budget:
      monthlyLimitUsd: 5000

  resources:
    - type: "database"
      kind: "PostgreSQL"
      name: "postgres-team-a"
    - type: "keyvault"
      name: "kv-team-a"
    - type: "appservice"
      name: "app-team-a"
```

The IDP then:
1. ✅ Validates the definition against organizational policies
2. ✅ Merges global IT Ops policies (network backbone, encryption, monitoring, etc.)
3. ✅ Enqueues a Pulumi provisioning job
4. ✅ Creates 3 subscriptions, VNets, NSGs, Entra ID groups, Key Vault, PostgreSQL, etc.
5. ✅ Configures centralized logging, cost budgets, alerts
6. ✅ Notifies Team A when complete with access details

**Total time:** 15–30 minutes (automated)  
**Manual work required:** Zero  
**Consistency:** 100% — every landing zone follows the same pattern

---

## Key Properties of Definitions

### 1. **Declarative, Not Imperative**
The definition describes the **desired end state**, not the steps to get there.

```yaml
# ✅ Correct (declarative)
resources:
  - type: "database"
    name: "postgres-team-a"
    configuration:
      serverVersion: "14"
      backupRetentionDays: 7

# ❌ Wrong (imperative)
steps:
  - step1: "Create resource group"
  - step2: "Create storage account"
  - step3: "Create server"
```

Declarative definitions are **idempotent** — applying the same definition twice produces the same result.

### 2. **Version Controlled**
Definitions live in Git. Every change is tracked:
```
commit abc1234: "Add PostgreSQL to team-a landing zone"
commit def5678: "Increase budget from $3k to $5k"
commit ghi9012: "Add GDPR compliance tag"
```

When the infrastructure drifts (e.g., someone manually changes a firewall rule), the definition and current state disagree — the IDP detects this and can auto-remediate or alert.

### 3. **Mergeable**
Team definitions are merged with global policies at provisioning time:

```yaml
# Team submits
team:
  resources:
    - database
  budget: $5000

# Global IT Ops policy
global:
  encryption: AES-256
  monitoring: centralized
  compliance: ["SOC2", "GDPR"]

# Result after merge
merged:
  resources:
    - database           # from team
  budget: $5000          # from team
  encryption: AES-256    # from global
  monitoring: centralized # from global
  compliance: ["SOC2", "GDPR"] # from global
```

### 4. **Gated by Approvals**
Definitions go through approval workflow:
- Platform team validates syntax and policies
- Team lead approves
- Provisioning only begins after approval

### 5. **Immutable History**
Once provisioned, the definition cannot be changed retroactively — only new definitions with updated timestamps are accepted. This maintains an audit trail.

---

## Workflow: From Definition to Running Infrastructure

```
1. Team prepares landing zone definition
   ↓
2. Team submits via IDP API or UI
   ↓
3. IDP validates definition
   - Syntax check
   - Policy compliance check
   - Resource quota check
   ↓
4. Platform team reviews and approves
   (or auto-approves if policy-compliant)
   ↓
5. IDP merges with global policies
   ↓
6. Pulumi Automation API generates infrastructure
   ↓
7. Background worker executes Pulumi
   - Creates subscriptions
   - Configures Entra ID groups
   - Deploys VNets, NSGs, firewalls
   - Creates databases, key vaults, app services
   - Configures monitoring, logging, alerting
   ↓
8. Team notified: "Your landing zone is ready"
   - Access credentials
   - Initial resource IPs/hostnames
   - Documentation links
   ↓
9. Team begins deploying their applications
```

---

## What Teams Define vs Platform Provides

### ✅ Teams Define (Shared Infrastructure Only):
- Team name and domain
- Access groups and RBAC roles
- Subscriptions needed (dev/test/prod)
- Network topology (VNet CIDRs, regions, subnets)
- Cost center tag
- Monthly budget (optional — has environment defaults)

### ✅ Platform Auto-Applies (Org Policies — Not Team Customizable):

**Identity & Security:**
- Conditional Access policies
- MFA enforcement
- Just-In-Time VM access
- Privilege Access Management

**Governance & Compliance:**
- Mandatory tags (CostCenter, Team, Environment, ManagedBy)
- Compliance frameworks (SOC2, GDPR, etc.)
- Audit logging
- Budget alerts (80%, 100%)

**Network Foundation:**
- VNet peering to regional hub
- Network security rules
- Private DNS zones
- Azure Firewall rules
- Flow logs and Network Watcher

**Security Baseline:**
- Encryption at rest: AES-256
- Transport encryption: TLS 1.2+
- Azure Policies: all org policies
- Azure Defender: enabled
- Threat detection: enabled
- Key Vault: CMK rotation

**Observability:**
- Centralized Log Analytics workspace
- Diagnostic logging: auto-enabled
- Application Insights: auto-enabled
- Monitoring baselines
- Standard alert thresholds

**Management:**
- Management Group assignment (by environment)
- Resource quotas and limits
- Subscription policies

### 🚫 Workloads are COMPLETELY SEPARATE:
Landing zones define the **shared foundation**.  
Workloads (databases, app services, functions) are defined and provisioned **separately**.

- One team can have **many workloads** in a single landing zone
- Workloads are deployed on top of the zone's infrastructure
- Different approval workflow than landing zones

### Design Principle

**Teams ask:** "What makes us different from standard?"  
**Platform provides:** Everything standard via org policies

Result: 100% compliance, zero manual security configuration needed

---

## Validation & Policy Enforcement

The IDP validates definitions against:

1. **Syntax** — valid YAML, required fields present
2. **Schema** — matches the landing zone definition schema
3. **Naming conventions** — resource names follow org standards
4. **Quotas** — don't exceed subscription limits, cost budgets, VM counts
5. **Compliance** — required security policies and tagging present
6. **Regions** — only deploy to approved regions
7. **Duplication** — can't reuse names across teams

If validation fails, the IDP returns errors:
```
Error: Definition 'lz-team-b' failed validation
  - Budget 15000 exceeds maximum 10000
  - Resource 'storage-prod' missing required tag: DataClassification
  - Region 'australiaeast' not approved
  - Compliance framework 'PCI-DSS' requires encryption: AES-256
```

---

## Updating a Landing Zone Definition

Once provisioned, teams can submit updated definitions:

```yaml
# Version 1.0 (original)
budget:
  monthlyLimitUsd: 3000

# Version 1.1 (updated)
budget:
  monthlyLimitUsd: 5000  # Increased budget
```

The IDP:
1. Compares old and new definitions
2. Detects differences (budget increased)
3. Applies only the delta (updates budget)
4. Doesn't reprovisioning unchanged resources
5. Maintains audit trail of the change

---

## Integration with Pulumi

Landing zone definitions are the **input** to Pulumi. The IDP converts definitions into Pulumi code at provisioning time:

```yaml
# Definition (team's intent)
resources:
  - type: "database"
    kind: "PostgreSQL"
    name: "postgres-team-a"
    configuration:
      serverVersion: "14"
      storage: 32
```

↓ [IDP converts to Pulumi code]

```typescript
// Pulumi (infrastructure as code)
const database = new azure.sql.SqlServer("postgres-team-a", {
  location: "westeurope",
  version: "14",
  storage: 32,
  administratorLogin: "dbadmin",
  administratorLoginPassword: vault.secret("db-password"),
  tags: { /* global tags applied */ }
});
```

↓ [Pulumi provisions]

```
Azure resources created:
  ✅ PostgreSQL Server
  ✅ Database
  ✅ Firewall rules
  ✅ Monitoring configured
  ✅ Backups enabled
```

---

## Design Principles

### 1. **Separate Intent from Execution**
- **Intent** (definition): "We need a database"
- **Execution** (Pulumi): "Create PostgreSQL server, add firewall rules, enable monitoring"

Decoupling these allows IT Ops to swap execution mechanisms (e.g., Azure CLI → Terraform → Pulumi) without teams rewriting definitions.

### 2. **Structured Data Over Free Text**
Don't ask teams to write:
> "Our app needs a database, some storage, and the usual security stuff. Budget around 2-3k, maybe 4k in peak months."

Ask them to fill a structured form:
```yaml
resources:
  - type: "database"
    kind: "PostgreSQL"
    configuration:
      serverVersion: "14"
      storage: 32
budget:
  monthlyLimitUsd: 3000
  alerts:
    - threshold: 80
      escalate: true
```

Structured data is automatable.

### 3. **Never Let Work Happen Outside the Platform**
If teams can request infrastructure outside the IDP (email, Slack, portal), you lose:
- Audit trail
- Consistency
- Ability to automate
- Cost visibility

Everything goes through definitions, everything is tracked.

### 4. **Defaults for Everything**
Most teams want the same thing. Define sensible defaults so definitions can be minimal:

```yaml
# Minimal definition (use defaults)
metadata:
  name: "lz-team-b"

# Expands to (defaults applied)
metadata:
  name: "lz-team-b"
governance:
  compliance: ["SOC2"]  # default
  tags: { Team: "TeamB", ManagedBy: "IDP" }  # default
networking:
  primaryRegion: "westeurope"  # default
  backupRegions: ["eastus"]  # default
security:
  encryption: "AES-256"  # default
  threatDetection: true  # default
```

---

## File Locations

| File | Purpose |
|------|---------|
| `definitions/landing-zone-definition.schema.yaml` | Full schema with all available fields and explanations |
| `definitions/landing-zone-example.yaml` | Real example showing what teams actually submit |
| `docs/LANDING-ZONE-DEFINITIONS.md` | This document |

---

## Next Steps for Teams

1. **Review** the schema and example definitions
2. **Draft** your landing zone definition based on your team's needs
3. **Submit** via the IDP portal/API
4. **Wait** for platform team approval
5. **Receive** notification when provisioning is complete
6. **Deploy** your applications into the ready-made infrastructure
