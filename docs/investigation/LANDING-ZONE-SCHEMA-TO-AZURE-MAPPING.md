# Landing Zone Schema → Azure Mapping

This document maps the **shared infrastructure fields** in the landing zone definition schema to the corresponding Azure services, resources, and configurations.

**Note:** Organizational policies (security, compliance, monitoring, governance) are automatically applied to every landing zone and are not individually mapped here. Teams don't define these; the platform enforces them organization-wide.

---

## Table of Contents
1. [Metadata](#metadata)
2. [Identity & Access](#identity--access)
3. [Governance](#governance)
4. [Subscriptions](#subscriptions)
5. [Networking](#networking)
6. [Auto-Applied Organizational Policies](#auto-applied-organizational-policies)

---

## Metadata

### Definition Fields
```yaml
metadata:
  name: "lz-team-a-dev"
  displayName: "Team A - Development"
  owner:
    team: "Team A"
    email: "team-a-leads@litmos.com"
```

### Azure Mapping

| Definition Field | Azure Service | Implementation |
|---|---|---|
| `name` | Azure Subscription tags | Stored as tag `LandingZoneName: lz-team-a-dev` on subscription |
| `displayName` | Azure Subscription | Subscription display name in portal |
| `owner.team` | Azure Subscription tags | Tag `Team: TeamA` |
| `owner.email` | Azure Subscription tags | Tag `OwnerEmail: team-a-leads@litmos.com` |

### Result in Azure Portal
```
Subscription: Team A - Development
├── Tags:
│   ├── LandingZoneName: lz-team-a-dev
│   ├── Team: TeamA
│   ├── OwnerEmail: team-a-leads@litmos.com
│   └── ManagedBy: IDP
```

---

## Identity & Access

### Definition Fields
```yaml
spec:
  identity:
    domain: "team-a.litmos.com"
    administrativeUnit:
      name: "au-team-a"
      description: "Team A identity scope"
    accessGroups:
      - name: "grp-team-a-developers"
        description: "Team A developers"
        roles: ["Contributor"]
      - name: "grp-team-a-devops"
        description: "Team A DevOps"
        roles: ["Owner"]
    conditionalAccess:
      requireMfa: true
      trustedNetworksOnly: false
```

### Azure Mapping

| Definition Field | Azure Service | Implementation |
|---|---|---|
| `domain` | **Entra ID Custom Domain** | Domain added to tenant via `Entra ID → Custom domain names → Add` |
| `administrativeUnit` | **Entra ID Administrative Units** | Creates AU scoped to this team; scoped identity admins can manage only this AU's users |
| `accessGroups[*].name` | **Entra ID Security Groups** | Creates `grp-team-a-developers` and `grp-team-a-devops` as Azure AD security groups |
| `accessGroups[*].roles` | **Azure RBAC Role Assignments** | Assigns roles to groups at subscription scope |
| `accessGroups[*].roles: Contributor` | **Azure Built-in Role** | Group gets `Contributor` on subscription (can create/modify resources) |
| `accessGroups[*].roles: Owner` | **Azure Built-in Role** | Group gets `Owner` on subscription (full control including RBAC) |
| `conditionalAccess.requireMfa` | **Entra ID Conditional Access** | Policy: "Require MFA when accessing subscription" |
| `conditionalAccess.trustedNetworksOnly` | **Entra ID Conditional Access** | Policy: "Only allow access from trusted network IPs" |

### Result in Azure

**Entra ID:**
```
Custom Domains
├── litmos.com (primary)
├── team-a.litmos.com (verified)

Administrative Units
├── au-team-a
│   └── Members: team-a-developers, team-a-devops

Security Groups
├── grp-team-a-developers (members: Alice, Bob, Carol)
├── grp-team-a-devops (members: Dave, Eve)

Conditional Access Policies
├── "Team A - MFA Required"
│   └── Users: grp-team-a-developers, grp-team-a-devops
│   └── Condition: Any cloud app
│   └── Grant: Require MFA
```

**Subscription RBAC:**
```
Subscription: sub-team-a-dev
├── Role: Contributor
│   └── Principal: grp-team-a-developers
├── Role: Owner
│   └── Principal: grp-team-a-devops
```

---

## Governance & Compliance

### Definition Fields
```yaml
spec:
  governance:
    compliance: ["SOC2", "GDPR"]
    tags:
      CostCenter: "CC-12345"
      Environment: "Development"
      Team: "TeamA"
    budget:
      monthlyLimitUsd: 5000
      alerts:
        - threshold: 50
          notificationEmail: "team-a-leads@litmos.com"
        - threshold: 80
          escalate: "finance@litmos.com"
```

### Azure Mapping

| Definition Field | Azure Service | Implementation |
|---|---|---|
| `compliance: ["SOC2", "GDPR"]` | **Azure Subscription tags** + **Azure Policy** | Tags: `Compliance: SOC2,GDPR`; Policies enforce encryption, retention, audit logging |
| `tags.*` | **Azure Resource Tags** | Applied to ALL resources in subscription via policy; mandatory |
| `tags.CostCenter` | **Cost Management + Billing** | Tag used for cost allocation and chargeback |
| `tags.Environment` | **Azure Policy evaluation** | Used in policies that differ by environment (e.g., backup retention) |
| `tags.Team` | **Cost Management + Billing** | Tag used to filter costs by team |
| `budget.monthlyLimitUsd` | **Azure Cost Management → Budgets** | Creates budget alert at subscription level |
| `budget.alerts[0].threshold: 50` | **Azure Cost Management → Budget Alerts** | Alert triggered when spend reaches 50% of budget |
| `budget.alerts[0].notificationEmail` | **Azure Cost Management → Alert Notification** | Email sent to team lead at 50% threshold |
| `budget.alerts[1].escalate` | **Azure Cost Management → Alert Notification** | Email escalated to finance team at 80% threshold |

### Result in Azure

**Cost Management + Billing:**
```
Budgets
├── Team A Dev Budget
│   ├── Amount: $5,000/month
│   ├── Scope: sub-team-a-dev
│   ├── Status: $1,200 spent (24%)
│   ├── Alerts:
│   │   ├── 50%: team-a-leads@litmos.com
│   │   ├── 80%: team-a-leads@litmos.com, finance@litmos.com
```

**Subscription Tags:**
```
Subscription: sub-team-a-dev
├── CostCenter: CC-12345
├── Environment: Development
├── Team: TeamA
├── Compliance: SOC2,GDPR
├── ManagedBy: IDP
```

**Azure Policy Assignments:**
```
Management Group: NonProduction
├── Policy: "Enforce Required Tags"
│   └── Tags: [CostCenter, Team, Environment, ManagedBy]
│   └── Effect: Append (add missing tags)
├── Policy: "Enforce SOC2 Controls"
│   └── Effect: Audit (or Deny based on config)
├── Policy: "Enforce GDPR Retention"
│   └── Storage accounts: minimum 90-day retention
│   └── Log Analytics: minimum 90-day retention
```

---

## Subscriptions

### Definition Fields
```yaml
spec:
  subscriptions:
    - name: "sub-team-a-dev"
      displayName: "Team A - Development"
      environment: "development"
      parentManagementGroup: "NonProduction"
    - name: "sub-team-a-prod"
      displayName: "Team A - Production"
      environment: "production"
      parentManagementGroup: "Production"
```

### Azure Mapping

| Definition Field | Azure Service | Implementation |
|---|---|---|
| `name` | **Azure Subscription** | Subscription ID (permanent, unchangeable) |
| `displayName` | **Azure Subscription** | Subscription name shown in portal |
| `environment: development` | **Azure Subscription tags** | Tag: `Environment: development` |
| `environment: production` | **Azure Subscription tags** | Tag: `Environment: production` |
| `parentManagementGroup` | **Azure Management Groups** | Subscription placed under specified management group |

### Result in Azure

**Subscriptions:**
```
Azure Tenant
├── Subscription: sub-team-a-dev (ID: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)
│   ├── Display Name: Team A - Development
│   ├── Billing Account: default
│   └── Status: Active
│
├── Subscription: sub-team-a-prod (ID: yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy)
│   ├── Display Name: Team A - Production
│   ├── Billing Account: premium
│   └── Status: Active
```

**Management Group Hierarchy:**
```
Root
└── Org (litmos)
    ├── Platform
    ├── Production
    │   └── Subscription: sub-team-a-prod
    ├── NonProduction
    │   ├── Subscription: sub-team-a-dev
    │   └── Subscription: sub-team-a-test
    └── Sandbox
```

**Policies Applied by Management Group:**
```
NonProduction (has different budget/retention limits than Production)
├── Budget limit: $10,000/month per subscription
├── VM size limit: up to 8 cores (not allowed: Premium_v4, etc.)
├── Backup retention: 7 days

Production (stricter policies)
├── Budget limit: $100,000/month per subscription
├── VM size limit: up to 16 cores
├── Backup retention: 30 days
├── Encryption: mandatory, customer-managed keys only
```

---

## Networking

### Definition Fields
```yaml
spec:
  networking:
    primaryRegion: "westeurope"
    secondaryRegions: ["eastus"]
    vnets:
      - subscriptionName: "sub-team-a-dev"
        name: "vnet-team-a-dev"
        addressSpace: "10.1.1.0/24"
        subnets:
          - name: "subnet-apps"
            addressPrefix: "10.1.1.0/26"
            serviceEndpoints: ["Microsoft.Sql"]
            privateEndpointEnabled: true
          - name: "subnet-data"
            addressPrefix: "10.1.1.64/26"
    securityPolicies:
      inbound:
        - description: "Allow HTTPS from internet"
          protocol: "tcp"
          destinationPort: 443
          sourceAddressPrefix: "*"
          action: "Allow"
```

### Azure Mapping

| Definition Field | Azure Service | Implementation |
|---|---|---|
| `primaryRegion: westeurope` | **Azure Regions** | All resources deployed to West Europe by default |
| `secondaryRegions: [eastus]` | **Azure Cross-Region Replication** | Resources replicated to East US for DR |
| `vnets[*].name` | **Azure Virtual Network (VNet)** | Creates VNet `vnet-team-a-dev` |
| `vnets[*].addressSpace: 10.1.1.0/24` | **Azure VNet CIDR** | VNet allocated IP range 10.1.1.0–10.1.1.255 |
| `subnets[*].name` | **Azure Subnet** | Creates subnets within VNet |
| `subnets[*].addressPrefix: 10.1.1.0/26` | **Azure Subnet CIDR** | Subnet allocated IPs 10.1.1.0–10.1.1.63 |
| `serviceEndpoints: [Microsoft.Sql]` | **Azure Service Endpoints** | Allows direct connectivity to SQL without internet routing |
| `privateEndpointEnabled: true` | **Azure Private Endpoints** | Resources in this subnet can use private endpoints (no public IPs) |
| `securityPolicies.inbound[*]` | **Azure Network Security Group (NSG)** | Creates inbound rules |
| `protocol: tcp`, `destinationPort: 443` | **NSG Inbound Rule** | Rule: Allow TCP port 443 (HTTPS) |
| `sourceAddressPrefix: *` | **NSG Inbound Rule** | Allow from any source IP |

### Result in Azure

**Virtual Networks:**
```
Subscription: sub-team-a-dev
├── Virtual Network: vnet-team-a-dev
│   ├── Address Space: 10.1.1.0/24
│   ├── Region: West Europe
│   ├── Subnet: subnet-apps (10.1.1.0/26)
│   │   ├── Service Endpoints: Microsoft.Sql
│   │   ├── Private Endpoints: Enabled
│   │   └── NSG: nsg-subnet-apps (attached)
│   │
│   └── Subnet: subnet-data (10.1.1.64/26)
│       ├── Private Endpoints: Enabled
│       └── NSG: nsg-subnet-data (attached)

├── Network Security Group: nsg-subnet-apps
│   ├── Inbound Rules:
│   │   ├── Rule 1: Allow TCP 443 from 0.0.0.0/0 (HTTPS)
│   │   ├── Rule 2: Deny all other inbound
│   │
│   └── Outbound Rules:
│       ├── Rule 1: Allow TCP 443 to 0.0.0.0/0 (HTTPS)
│       ├── Rule 2: Allow UDP 53 to 0.0.0.0/0 (DNS)
│       └── Rule 3: Deny all other outbound

├── VNet Peering: vnet-team-a-dev ↔ vnet-hub-westeurope
│   ├── Gateway Transit: Enabled
│   ├── Allow Forwarded Traffic: Enabled
│   └── Status: Connected
```

**Network Topology (at Azure Virtual WAN level):**
```
Azure Virtual WAN (Global)
├── Hub: hub-westeurope
│   ├── Hub VNet: vnet-hub-westeurope (10.0.0.0/24)
│   ├── Firewall: azfw-westeurope
│   ├── VPN Gateway: vpn-gw-westeurope
│   ├── Spokes:
│   │   ├── spoke-team-a-dev (10.1.1.0/24) ← from definition
│   │   ├── spoke-team-b-dev (10.1.2.0/24)
│   │   └── spoke-shared-prod (10.1.10.0/24)
│
└── Hub: hub-eastus
    ├── Hub VNet: vnet-hub-eastus (10.2.0.0/24)
    ├── Spokes...
```

---

## Security

### Definition Fields
```yaml
spec:
  security:
    policies:
      - name: "require-encryption-at-rest"
        effect: "Deny"
    encryption:
      storageEncryption: "AES-256"
      transportEncryption: "TLS1.2+"
      keyVault:
        enabled: true
        cmkRotation: true
    threatDetection:
      defenderForCloud: true
      defenderPlans: ["SqlOnMachines", "SqlServers"]
    pam:
      justInTimeAccess: true
      requireApproval: true
```

### Azure Mapping

| Definition Field | Azure Service | Implementation |
|---|---|---|
| `policies[*].name: require-encryption-at-rest` | **Azure Policy** | Policy definition that enforces encryption at rest |
| `effect: Deny` | **Azure Policy Mode** | Non-compliant resources are blocked from creation |
| `storageEncryption: AES-256` | **Azure Storage Encryption** | Storage accounts use AES-256 for encryption (default in Azure) |
| `transportEncryption: TLS1.2+` | **Azure Policy** | Policy enforces minimum TLS 1.2 on all resources |
| `keyVault.enabled: true` | **Azure Key Vault** | Creates Key Vault for secrets storage |
| `keyVault.cmkRotation: true` | **Azure Key Vault → Key Rotation** | Customer-managed keys rotated automatically |
| `defenderForCloud: true` | **Microsoft Defender for Cloud** | Enables Defender on subscription |
| `defenderPlans: [SqlOnMachines]` | **Microsoft Defender for SQL** | Defender plan enabled for SQL Server on VMs |
| `justInTimeAccess: true` | **Azure Just-In-Time VM Access (JIT)** | VMs only allow SSH/RDP when explicitly requested |
| `requireApproval: true` | **Azure PIM (Privileged Identity Management)** | JIT access requires approval from admins |

### Result in Azure

**Azure Policy Assignments:**
```
Management Group: NonProduction
├── Assignment: "Require Encryption at Rest"
│   ├── Policy: "Deny storage without encryption"
│   ├── Effect: Deny
│   ├── Scope: All subscriptions under NonProduction
│   └── Status: Enforced
│
├── Assignment: "Enforce TLS 1.2 Minimum"
│   ├── Policy: "Deny resources using TLS < 1.2"
│   ├── Effect: Deny
│   └── Resources affected: App Services, Databases, App Gateways, etc.
```

**Key Vault:**
```
Subscription: sub-team-a-dev
├── Key Vault: kv-team-a-dev
│   ├── Keys:
│   │   └── Key: team-a-cmk (customer-managed key)
│   │       ├── Rotation: Enabled
│   │       ├── Rotation frequency: 90 days
│   │       └── Auto-rotated: Yes
│   │
│   ├── Secrets:
│   │   ├── db-password
│   │   ├── api-key
│   │   └── connection-string
│   │
│   └── Access Policies:
│       ├── grp-team-a-developers: Read secrets
│       └── grp-team-a-devops: Read + Manage keys
```

**Microsoft Defender for Cloud:**
```
Subscription: sub-team-a-dev
├── Defender Status: On
├── Enabled Plans:
│   ├── Defender for Servers
│   ├── Defender for SQL
│   ├── Defender for App Service
│   └── Defender for Storage
│
├── Findings:
│   ├── Open RDP ports detected
│   ├── Unencrypted storage account
│   └── Missing system updates on VM
```

**Just-In-Time VM Access:**
```
Subscription: sub-team-a-dev
├── VM: vm-app-server
│   ├── JIT Status: Enabled
│   ├── Open ports: None (RDP 3389 is closed)
│   ├── Request history:
│   │   └── 2026-07-13 10:00 - Alice requested RDP access for 4 hours
│   │       ├── Approver: Dave (grp-team-a-devops)
│   │       ├── Status: Approved
│   │       └── Access granted: 10:15 - 14:15
```

---

## Observability

### Definition Fields
```yaml
spec:
  observability:
    logAnalytics:
      workspaceName: "law-team-a-shared"
      retentionDays: 90
    monitoring:
      enableApplicationInsights: true
      enableNetworkWatcher: true
    alerting:
      criticalThreshold:
        cpuPercent: 90
        memoryPercent: 90
        errorRatePercent: 5
      notificationChannels:
        - type: "email"
          recipients: ["team-a-leads@litmos.com"]
        - type: "slack"
          channel: "#team-a-dev"
```

### Azure Mapping

| Definition Field | Azure Service | Implementation |
|---|---|---|
| `logAnalytics.workspaceName` | **Azure Log Analytics Workspace** | Central workspace for all logs from this team's zone |
| `logAnalytics.retentionDays: 90` | **Log Analytics Data Retention** | Logs kept for 90 days (searchable), then archived to cold storage |
| `enableApplicationInsights: true` | **Azure Application Insights** | App Insights resource created; auto-linked to Log Analytics |
| `enableNetworkWatcher: true` | **Azure Network Watcher** | Network Watcher enabled for VNet; flow logs sent to Log Analytics |
| `cpuPercent: 90` | **Azure Monitor Alert Rule** | Alert triggered when CPU > 90% |
| `memoryPercent: 90` | **Azure Monitor Alert Rule** | Alert triggered when memory > 90% |
| `notificationChannels[type=email]` | **Azure Monitor Action Group** | Sends email to recipients when alert triggers |
| `notificationChannels[type=slack]` | **Logic App / Webhook** | Posts to Slack channel via webhook when alert triggers |

### Result in Azure

**Log Analytics Workspace:**
```
Subscription: sub-team-a-shared (or central platform subscription)
├── Log Analytics Workspace: law-team-a-shared
│   ├── Region: West Europe
│   ├── Data Retention: 90 days
│   ├── Pricing Tier: Pay-As-You-Go
│   ├── Data Sources:
│   │   ├── VMs (Windows Event Logs, Syslog)
│   │   ├── Databases (SQL Query Stats, Audit Logs)
│   │   ├── App Services (Application Logs)
│   │   ├── Network Watcher (Flow Logs)
│   │   ├── Key Vault (Access Logs)
│   │   └── Azure Firewall (Traffic Logs)
│   │
│   └── Saved Queries:
│       ├── "Failed logins in last 24h"
│       ├── "Top 10 error messages by app"
│       └── "Network traffic by source IP"
```

**Application Insights:**
```
Subscription: sub-team-a-dev
├── Application Insights: ai-team-a-dev
│   ├── Linked to: Log Analytics Workspace (law-team-a-shared)
│   ├── Instrumented Applications: 3
│   ├── Metrics:
│   │   ├── Request Rate: 150 req/sec
│   │   ├── Response Time: 250ms (p95)
│   │   ├── Error Rate: 0.5%
│   │   └── Dependency Calls: 450 calls/sec
│   │
│   └── Availability Tests:
│       ├── Homepage availability: 99.9%
│       └── API health check: 99.95%
```

**Alert Rules:**
```
Management Group: NonProduction
├── Alert Rule: "CPU > 90%"
│   ├── Scope: All VMs in NonProduction
│   ├── Condition: CPU utilization > 90% for 5 minutes
│   ├── Severity: 2 (Warning)
│   ├── Action Groups: email, slack
│   └── Notification:
│       ├── Email to: team-a-leads@litmos.com
│       └── Slack to: #team-a-dev

├── Alert Rule: "Error Rate > 5%"
│   ├── Scope: Application Insights (ai-team-a-dev)
│   ├── Condition: Error rate > 5% for 10 minutes
│   ├── Severity: 3 (Error)
│   ├── Action Group: email, pagerduty
│   └── Notification:
│       ├── Email to: team-a-leads@litmos.com
│       └── PagerDuty incident created
```

---

## Resources / Workloads

**NOT INCLUDED in Landing Zone Definition.**

Landing zones define the **shared infrastructure foundation only**. Workload resources (databases, app services, functions, storage accounts) are provisioned separately via **workload definitions**.

### Why Separate?

| Aspect | Landing Zone | Workload |
|---|---|---|
| **Scope** | Team-scoped | Application-scoped |
| **Lifetime** | Long-lived, rarely changes | Frequently changing |
| **Quantity** | One per team | Many per team |
| **Provisioning** | Platform team approves | Automation once zone ready |
| **Examples** | Subscriptions, VNets, identity, security, logging | Databases, app services, functions, storage |

### Example

```
Landing Zone: Team A (defined once)
├── Subscription: sub-team-a-dev
├── VNet: vnet-team-a-dev
├── Identity: grp-team-a-developers, grp-team-a-devops
├── Security: encryption, TLS, Azure Policies
├── Logging: law-team-a-shared

Workload 1: Payment Service (defined and deployed by Team A dev)
├── PostgreSQL database
├── App Service
├── Storage account

Workload 2: Notification Service (defined and deployed by Team A dev)
├── MongoDB
├── Function App
├── Queue Storage
```

Both workloads run in the same landing zone infrastructure, but are provisioned separately from the landing zone definition.

---

## Policies

### Definition Fields
```yaml
spec:
  policies:
    allowedActions:
      - "CreateVMs"
      - "CreateDatabases"
      - "ManageKeyVaults"
    deniedActions:
      - "ModifySubscriptionPolicies"
      - "DisableAuditLogging"
    limits:
      maxVmsPerSubscription: 20
      maxVmCoresTotal: 64
      allowedRegions: ["westeurope", "eastus"]
      deniedRegions: ["chinaeast2"]
```

### Azure Mapping

| Definition Field | Azure Service | Implementation |
|---|---|---|
| `allowedActions` | **Azure Policy (informational)** | Documentation of allowed actions; enforced via role assignments |
| `deniedActions` | **Azure Policy + RBAC** | Custom policies deny creation/modification of resources |
| `maxVmsPerSubscription: 20` | **Azure Policy** | Policy with `Audit` effect if > 20 VMs exist |
| `maxVmCoresTotal: 64` | **Azure Policy** | Policy prevents creating VMs that would exceed 64 cores |
| `allowedRegions: [westeurope, eastus]` | **Azure Policy** | Policy denies resources created in any other region |
| `deniedRegions: [chinaeast2]` | **Azure Policy** | Policy explicitly blocks China region |

### Result in Azure

**Azure Policy Assignments:**
```
Subscription: sub-team-a-dev
├── Policy Assignment: "Allowed Actions - Inform"
│   ├── Description: Team A can create VMs, databases, key vaults
│   ├── Effect: None (informational only)
│   ├── Status: Enabled
│
├── Policy Assignment: "Deny Subscription Policy Modification"
│   ├── Effect: Deny
│   ├── Scope: sub-team-a-dev
│   ├── Excluded principals: grp-team-a-devops (they can still modify)
│   └── Result: Users cannot modify subscription-level policies
│
├── Policy Assignment: "Enforce VM Count Limit"
│   ├── Policy: "VM count must be ≤ 20"
│   ├── Effect: Deny
│   ├── Current state: 3 VMs deployed
│   ├── Compliance: Compliant (3 < 20)
│
├── Policy Assignment: "Enforce Total CPU Cores Limit"
│   ├── Policy: "Total CPU cores must be ≤ 64"
│   ├── Effect: Deny
│   ├── Current state: 12 cores allocated
│   ├── Compliance: Compliant (12 < 64)
│
├── Policy Assignment: "Allowed Regions Only"
│   ├── Policy: "Resources must be created in [westeurope, eastus]"
│   ├── Effect: Deny
│   ├── Current state: All resources in westeurope
│   ├── Compliance: Compliant
│
└── Policy Assignment: "Deny China Region"
    ├── Policy: "Resources cannot be created in chinaeast2"
    ├── Effect: Deny
    └── Current state: No resources in China
```

**RBAC Role Assignments (enabling allowed actions):**
```
Subscription: sub-team-a-dev
├── Role: Contributor (grp-team-a-developers)
│   └── Allows:
│       ├── Create/modify/delete VMs
│       ├── Create/modify/delete databases
│       ├── Create/modify Key Vaults
│       └── But NOT: modify subscriptions, policies, or RBAC
│
└── Role: Owner (grp-team-a-devops)
    └── Allows: Everything (but constrained by policies)
```

---

## Automation

### Definition Fields
```yaml
spec:
  automation:
    provisioning:
      method: "pulumi"
      languageStack: "typescript"
      autoApprove: false
      requireApprovals:
        - role: "landingZoneAdmin"
        - team: "platform-engineering"
    scheduledTasks:
      - name: "backup-verification"
        cronSchedule: "0 2 * * *"
        action: "verify-all-backups"
    selfHealing:
      enabled: true
      policies:
        - detectUntaggedResources: true
          autoTag: true
```

### Azure Mapping

| Definition Field | Azure Service | Implementation |
|---|---|---|
| `provisioning.method: pulumi` | **Pulumi Automation API** | Infrastructure-as-code framework driving provisioning |
| `provisioning.languageStack: typescript` | **Pulumi TypeScript** | Pulumi programs written in TypeScript |
| `autoApprove: false` | **Azure Approval Workflow (custom)** | IDP requires manual approval before provisioning |
| `requireApprovals[role]` | **Entra ID RBAC** | Only users in `landingZoneAdmin` role can approve |
| `requireApprovals[team]` | **Entra ID Groups** | Only members of `platform-engineering` group can approve |
| `scheduledTasks[*].cronSchedule` | **Azure Automation / Logic Apps** | Scheduled job runs at specified time |
| `action: verify-all-backups` | **Azure Automation Runbook** | Runbook executed to verify backup completion |
| `detectUntaggedResources: true` | **Azure Policy** | Policy finds untagged resources |
| `autoTag: true` | **Azure Policy (DeployIfNotExists)** | Policy automatically applies default tags to new resources |

### Result in Azure

**Pulumi State Backend:**
```
Subscription: sub-platform-management
├── Storage Account: stpulumistate[suffix]
│   ├── Blob Container: pulumi-state
│   │   ├── Blob: stack-lz-team-a-dev.json
│   │   │   └── Contains: Current state of all resources for this landing zone
│   │   ├── Blob: stack-lz-team-a-prod.json
│   │   └── ...
│   │
│   ├── Blob Versioning: Enabled
│   ├── Soft Delete: Enabled (30-day recovery window)
│   └── Access: Only sp-just-deliver-idp service principal can read/write
```

**Audit Log (who provisioned what):**
```
Azure Activity Log
├── Event: Landing zone provisioning started
│   ├── User: sp-just-deliver-idp
│   ├── Time: 2026-07-13 10:15:00 UTC
│   ├── Action: Microsoft.Subscription/subscriptions/write
│   ├── Target: sub-team-a-dev
│   └── Status: Success
│
├── Event: VNet created
│   ├── User: sp-just-deliver-idp (via Pulumi)
│   ├── Time: 2026-07-13 10:17:35 UTC
│   ├── Action: Microsoft.Network/virtualNetworks/write
│   ├── Target: vnet-team-a-dev
│   └── Status: Success
│
└── ... [hundreds of resource creation events]
```

**Azure Automation:**
```
Subscription: sub-platform-management
├── Automation Account: aa-platform-idp
│   ├── Runbook: "verify-all-backups"
│   │   ├── Type: PowerShell
│   │   ├── Schedule: Daily at 2:00 AM UTC
│   │   ├── Last run: 2026-07-13 02:00:10 UTC
│   │   ├── Status: Completed successfully
│   │   └── Output: All 45 backups verified
│   │
│   └── Runbook: "cost-budget-check"
│       ├── Type: PowerShell
│       ├── Schedule: Monday 9:00 AM UTC
│       ├── Last run: 2026-07-08 09:00:15 UTC
│       ├── Status: Completed successfully
│       └── Output: Team A budget at 42% (green)
```

**Self-Healing Policies:**
```
Subscription: sub-team-a-dev
├── Azure Policy Assignment: "Auto-Tag Untagged Resources"
│   ├── Policy: DeployIfNotExists
│   ├── Trigger: Resource created without required tags
│   ├── Action: Automatically add default tags
│   ├── Example:
│   │   └── New storage account created without tags
│   │       → Policy auto-applies: [Team: TeamA, Environment: development, ManagedBy: IDP]
│   │       → No downtime, resource fully functional
│   │
├── Alert: "Untagged Resource Detected"
│   ├── Type: Audit-only (informational)
│   ├── Notification: team-a-leads@litmos.com
│   ├── Message: "Storage account 'stteamadebug' created without tags (auto-tagged)"
│   └── Required Action: None (auto-remediated)
```

---

## Summary: Definition → Azure Services Mapping

| Definition Section | Primary Azure Services | Key Resources Created |
|---|---|---|
| **Metadata** | Subscription tags | Tags on subscription |
| **Identity & Access** | Entra ID, RBAC, Conditional Access | Groups, roles, access policies |
| **Governance** | Cost Management, Tags, Budget Alerts | Budgets, alert rules, cost allocation |
| **Subscriptions** | Subscriptions, Management Groups | 2–3 subscriptions (dev/test/prod) |
| **Networking** | VNet, NSG, VNet Peering, Azure Firewall | VNets, subnets, firewall rules, peering |
| **Security** | Azure Policy, Key Vault, Defender | Policies, key vaults, security alerts |
| **Observability** | Log Analytics, Application Insights, Monitor | Workspaces, app insights, alert rules |
| **Policies** | Azure Policy, RBAC | Custom policies, role assignments |
| **Automation** | Pulumi, Automation Accounts, Logic Apps | Runbooks, scheduled tasks |

**NOT included:** Workload resources (databases, app services, functions) — provisioned separately via workload definitions

---

## Example: Full Landing Zone Definition Instantiation

When Team A submits their landing zone definition, here's what gets created in Azure:

```
Azure Tenant (litmos)
├── Management Groups
│   └── NonProduction
│       └── Subscription: sub-team-a-dev
│           ├── Resource Group: rg-team-a-core (networking & identity infrastructure)
│           │   ├── VNet: vnet-team-a-dev (10.1.1.0/24)
│           │   │   ├── Subnet: subnet-apps (10.1.1.0/26)
│           │   │   ├── Subnet: subnet-data (10.1.1.64/26)
│           │   │   └── NSGs with inbound/outbound rules
│           │   ├── Log Analytics Workspace: law-team-a-shared
│           │   ├── VNet Peering: to hub-westeurope
│           │   └── Network Watcher enabled
│           │
│           ├── Resource Group: rg-team-a-security (shared security foundation)
│           │   ├── Key Vault: kv-team-a-dev (for app secrets)
│           │   └── Private Endpoint: pe-keyvault
│           │
│           └── Policy Assignments (enforced on all resources)
│               ├── Require encryption at rest
│               ├── Enforce TLS 1.2+
│               ├── Limit VMs to 20
│               ├── Limit cores to 64
│               ├── Auto-tag untagged resources
│               ├── Budget alert at 50%, 80%
│               └── Require MFA for access

├── Entra ID
│   ├── Custom Domain: team-a.litmos.com
│   ├── Administrative Unit: au-team-a
│   ├── Security Groups
│   │   ├── grp-team-a-developers (Contributor role)
│   │   └── grp-team-a-devops (Owner role)
│   └── Conditional Access Policy
│       └── Require MFA for Team A access

└── Cost Management
    ├── Budget: Team A Dev ($5,000/month)
    ├── Alerts: 50%, 80%, 100%
    └── Cost Tags: Team, CostCenter, Environment, ManagedBy
```

**Infrastructure created:** ~15–20 Azure resources  
**Deployment time:** 15–30 minutes (fully automated)  
**Workloads:** Team A can now deploy applications (databases, app services, etc.) on top of this foundation

This entire infrastructure is deployed **automatically, consistently, and in 15–30 minutes** from a single YAML file.

Once ready, Team A uses separate **workload definitions** to provision:
- PostgreSQL databases
- App Services
- Storage Accounts  
- Functions
- etc.

...all within the governance, security, and networking foundation created by the landing zone.
