# Landing Zone Schema Redesign — Shared Infrastructure Only

## What Changed

The landing zone definition schema was simplified to include **only shared infrastructure** — the foundation that all workloads run on.

### What Teams Now Provide

**Required (core shared infrastructure):**
```yaml
metadata:
  name: "lz-team-a"
  displayName: "Team A"
  owner:
    team: "Team A"
    email: "team-a-leads@litmos.com"

spec:
  identity:
    domain: "team-a.litmos.com"
    accessGroups:
      - name: "grp-team-a-developers"
        roles: ["Contributor"]

  governance:
    tags:
      CostCenter: "CC-12345"
      Team: "TeamA"
      Environment: "development"

  subscriptions:
    - name: "sub-team-a-dev"
      displayName: "Team A - Dev"
      environment: "development"

  networking:
    primaryRegion: "westeurope"
    vnets:
      - subscriptionName: "sub-team-a-dev"
        name: "vnet-team-a-dev"
        addressSpace: "10.1.1.0/24"
        subnets:
          - name: "subnet-apps"
            addressPrefix: "10.1.1.0/25"
```

**Optional:**
- `budget.monthlyLimitUsd` — if not provided, uses environment default

---

## What Was Removed (and Why)

### Removed: Individual Customization of Organizational Policies

These are organization-wide and should not vary per team:

| Removed Field | Reason | Org Policy Applies |
|---|---|---|
| `identity.administrativeUnit` | Auto-generated from team name | Yes |
| `identity.conditionalAccess` | Org-wide access policy | Yes |
| `governance.compliance` | Org compliance baseline | Yes |
| `security.*` all fields | Encryption, threat detection, policies | Yes |
| `observability.*` all fields | Logging, monitoring baseline | Yes |
| `policies.*` customization | Resource limits per environment | Yes |
| `networking.secondaryRegions` | Org determines DR strategy | Implicit |
| `networking.securityPolicies` | Org network rules apply | Yes |
| `networking.vnets[].flowLogs` | Always enabled, not optional | Yes |
| `networking.vnets[].peeringToHub` | Always enabled, not optional | Yes |
| `networking.subnets[].serviceEndpoints` | Workload-specific, not shared | N/A |
| `networking.subnets[].privateEndpointEnabled` | Workload choice, not zone | N/A |

### Removed: Operational and Automation Details

These are not part of shared infrastructure definition:

| Removed Field | Reason | Applies By |
|---|---|---|
| `automation.scheduledTasks` | Operational detail | Platform |
| `automation.selfHealing` | Operational policy | Platform |
| `automation.provisioning` | Standard method (Pulumi) | Platform |
| `dependencies` | Auto-detected by platform | Platform |

### Removed: Workload-Specific Configuration

These are not shared infrastructure; they're application resources:

| Removed Field | Reason |
|---|---|
| `resources.*` | Workloads defined separately |
| Application-specific monitoring | Applied per workload |
| Custom security rules | Zone-wide rules via policy |

---

## What Gets Auto-Applied Now

**All organizational policies are automatically applied to every landing zone.** Teams don't define them; the platform enforces them.

### Identity
- ✅ Entra ID domain verified
- ✅ Administrative Unit created (auto-named)
- ✅ Security groups created
- ✅ RBAC roles assigned
- ✅ Conditional Access policies (org-wide)

### Governance
- ✅ Mandatory tags added (CostCenter, Team, Environment, ManagedBy)
- ✅ Compliance frameworks (org defaults: SOC2, etc.)
- ✅ Budget alerts (80%, 100% thresholds)
- ✅ Cost allocation tracking

### Networking
- ✅ Secondary subnets auto-provisioned (for data isolation)
- ✅ VNet peering to regional hub (automatic)
- ✅ Network security rules (org-wide)
- ✅ Private DNS zones (auto-configured)
- ✅ Flow logs (always enabled)
- ✅ Network Watcher (always enabled)

### Security
- ✅ Encryption at rest: AES-256
- ✅ Transport encryption: TLS 1.2+
- ✅ Azure Policies: all org policies enforced
- ✅ Azure Defender: enabled
- ✅ Threat detection: enabled
- ✅ Just-In-Time VM access: enabled
- ✅ Key Vault: created with CMK rotation

### Observability
- ✅ Log Analytics: centralized (law-platform-shared)
- ✅ Diagnostics: auto-enabled
- ✅ Application Insights: auto-enabled
- ✅ Monitoring: standard baselines
- ✅ Alerting: standard thresholds

### Management
- ✅ Management Group: auto-assigned by environment
- ✅ Subscription policies: auto-assigned
- ✅ Resource quotas: auto-enforced

---

## Design Principle

**Teams define:** What makes them different from the standard  
**Platform provides:** Everything standard via organizational policies

### Example

```
Team asks: "We need a dev subscription, these access groups, this network"
Platform provides: Everything else
├── Security: encryption, TLS, threat detection
├── Compliance: SOC2, GDPR, audit logging
├── Monitoring: centralized logging, dashboards, alerts
├── Networking: peering to hub, DNS, firewall rules
├── Governance: cost tracking, resource limits, policies
└── Identity: conditional access, MFA, RBAC
```

---

## Impact on Schema

| Metric | Before | After | Change |
|---|---|---|---|
| **Lines per definition** | 200+ | 30–60 | -85% |
| **Fields teams define** | 40+ | 8 | -80% |
| **Auto-applied policies** | 0% | 100% | Complete |
| **Org consistency** | Loose (team override options) | Tight (no overrides) | ✅ |
| **Team effort** | High (must think about security, monitoring) | Low (define only infrastructure) | ✅ |
| **Platform complexity** | Medium | High | Acceptable |

---

## What Stays in Landing Zone Definition

Only the **shared infrastructure foundation** that varies by team:

| Category | Example |
|---|---|
| **Identity** | Team name, domain, access groups |
| **Governance** | Cost center, team identifier |
| **Subscriptions** | How many environments (dev/test/prod) |
| **Networking** | VNet CIDRs, region, subnet structure |

---

## What Moved Out

### To Organizational Policies
- All security configuration
- All compliance configuration
- All monitoring and alerting
- All network security rules
- All encryption standards
- All resource limits

**Result:** Org policies are enforced everywhere, automatically, consistently.

### To Workload Definitions (separate process)
- Databases
- App Services
- Functions
- Storage Accounts
- Application-specific resources

**Result:** Workloads are defined and deployed separately, on top of the zone.

### To Platform Operations
- Self-healing policies
- Scheduled maintenance tasks
- Automation orchestration
- Backup and disaster recovery

**Result:** Teams don't need to think about operations; platform handles it.

---

## Migration Path

If you have existing definitions with removed fields, they're automatically ignored:

```yaml
# Old field (ignored, org policy applies)
identity:
  conditionalAccess:
    requireMfa: true
# ↓
# Platform ignores this; enforces org policy instead
```

No manual migration needed — old definitions still work, they just don't customize the removed fields anymore.

---

## Summary

**Landing Zone Definition is now:**
- ✅ Simpler (33 lines minimum)
- ✅ Focused (shared infrastructure only)
- ✅ Compliant (org policies always apply)
- ✅ Maintainable (fewer moving parts)
- ✅ Consistent (no team override of policies)
