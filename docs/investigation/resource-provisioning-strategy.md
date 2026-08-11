# Resource Provisioning & Workload Definition Strategy

## Overview

This document outlines how just-deliver handles resource provisioning, from team-submitted workload definitions through platform mapping and global IT Ops policies to final provisioned resources. It balances **simplicity for development teams** with **transparency and control for platform/ops teams**.

---

## 1. Design Philosophy

### Principle 1: Simple Definitions for Teams
Development teams should declare what they need with minimal boilerplate. They specify *what*, not *how*.

```yaml
requires:
  - type: database
  - type: cache
  - type: queue
```

**Why:** Lower friction, faster onboarding, less cognitive load. Teams focus on business logic, not infrastructure configuration.

### Principle 2: Platform Team Maintains Mappings
All the "how to provision correctly" logic lives in one place: the platform team's mapping definitions. Teams don't duplicate this knowledge.

**Why:** Consistency, centralized expertise, easier to evolve without changing team definitions.

### Principle 3: Transparency Over Abstraction
When a resource is provisioned, show the full resolution chain: team definition → platform mapping → global policies → final config.

**Why:** Debugging, audit trail, learning, and accountability. Both teams and ops understand what was decided and why.

### Principle 4: Escape Hatches for Urgency
Teams can override specific resource settings when they hit edge cases the platform doesn't yet support.

**Why:** Prevents blocking; teams can ship urgent changes without waiting for platform team to add a new mapping class. Also provides feedback loop to improve mappings.

---

## 2. Workload Definition Schema (MVP)

### Simple Format

```yaml
apiVersion: just-deliver/v1
kind: Workload
metadata:
  name: my-service
  team: platform-team
  environment: production

# Container runtime (SCORE-compatible subset)
container:
  image: my-registry/my-service:1.2.3
  variables:
    LOG_LEVEL: info
    DATABASE_HOST: ${resources.database.host}  # runtime substitution
  ports:
    - port: 8080
      protocol: TCP

# Infrastructure requirements (simple declarations)
requires:
  - type: database
  
  - type: cache
  
  - type: queue
```

### Design Rationale

- **No resource sizing, SKU, or configuration.** Teams declare need; platform provides defaults.
- **Container spec is minimal.** Only image, variables, and port. Health checks and resource limits come from global policy.
- **Resource names are implicit.** Platform generates resource IDs based on workload name + type.
- **Environment-aware.** Same workload definition deploys to dev/staging/prod; platform adjusts resource sizing per environment.
- **No approval or release workflow.** Who signs off is a property of the release process, not of the workload, and is defined separately. Keeping it out means a workload definition describes only what to build, and sign-off rules can change without teams editing their definitions.

### What Teams Don't Specify (Yet)

- Database SKU or size
- Cache eviction policy
- Queue partitioning strategy
- Backup retention
- Encryption settings
- Monitoring configuration
- Network/security policies
- Replica count or scaling rules

All of these come from platform mappings + global policies. If a team needs to customize, they use overrides (see Section 5).

---

## 3. Platform Mapping Layer

### What Platform Team Maintains

Platform team defines resource type mappings. These are the "blessed" configurations for each resource type.

```typescript
// platform/mappings/resources.ts

const resourceMappings = {
  database: {
    type: 'azure-sql-database',
    properties: {
      sku: 'Standard_S1',
      backup_retention_days: 30,
      encryption_at_rest: true,
      high_availability: false,
      connection_timeout: 30,
      monitoring: true,
    },
  },
  
  cache: {
    type: 'azure-redis',
    properties: {
      sku: 'Basic',
      eviction_policy: 'allkeys-lru',
      persistence: false,
      monitoring: true,
    },
  },
  
  queue: {
    type: 'azure-service-bus',
    properties: {
      sku: 'Standard',
      max_message_size: 256,  // KB
      default_ttl: 14,  // days
      monitoring: true,
    },
  },
}
```

### Environment-Specific Overrides

Same mapping can be environment-aware:

```typescript
const resourceMappings = {
  database: {
    dev: {
      sku: 'Standard_S1',
      backup_retention_days: 7,
      high_availability: false,
    },
    staging: {
      sku: 'Standard_S2',
      backup_retention_days: 14,
      high_availability: false,
    },
    production: {
      sku: 'Premium_P2',
      backup_retention_days: 30,
      high_availability: true,
    },
  },
}
```

### Governance

- **Single source of truth.** All resource configs centralized in one place (or small set of files).
- **Version controlled.** Changes to mappings are tracked, reviewed, approved.
- **Documented.** Each mapping explains why it's configured this way.
- **Testable.** Platform team validates that mappings work before deploying.

---

## 4. Global IT Ops Policies

### What Policies Do

IT Ops defines platform-wide policies that apply to *all workloads*, overriding individual mappings when necessary. These enforce compliance, security, and organizational standards.

```typescript
// platform/policies/global.ts

const globalPolicies = [
  {
    name: 'enforce-encryption',
    applies_to: ['database', 'cache', 'queue'],
    enforce: {
      encryption_at_rest: true,
      encryption_in_transit: true,
    },
    reason: 'Compliance: all data at rest and in transit must be encrypted',
  },
  
  {
    name: 'enforce-geo-redundancy-prod',
    applies_to: ['database'],
    condition: { environment: 'production' },
    enforce: {
      high_availability: true,
      geo_redundancy: true,
    },
    reason: 'SLA requirement: production databases must be highly available',
  },
  
  {
    name: 'enforce-monitoring',
    applies_to: ['database', 'cache', 'queue'],
    enforce: {
      monitoring: true,
      app_insights: true,
    },
    reason: 'Observability: all resources must report to App Insights',
  },
  
  {
    name: 'enforce-compliance-tags',
    applies_to: ['*'],  // all resources
    inject: {
      tags: {
        'cost-center': 'platform-ops',
        'compliance': 'sox-2-compliant',
        'data-classification': 'internal',
      },
    },
    reason: 'Billing and compliance tracking',
  },
]
```

### Policy Precedence

1. **Team definition** (lowest priority)
2. **Platform mapping** (same environment)
3. **Global IT Ops policy** (highest priority)

Example:
- Team says: `requires: [database]` (just the type)
- Platform mapping says: `encryption_at_rest: false` (for cost in dev)
- Global policy says: `encryption_at_rest: true` (non-negotiable)
- **Result:** Database will have encryption enabled (policy wins)

---

## 5. Resource Resolution & Transparency

### Full Resolution Chain

When a workload is submitted for deployment, the platform resolves resources step-by-step, showing the full chain:

```
Deployment: my-service v1.2.3 → staging

──────────────────────────────────────────────────────────

Resource: database

1. TEAM DEFINITION
   requires:
     - type: database

2. PLATFORM MAPPING (database, environment: staging)
   sku: Standard_S2
   backup_retention_days: 14
   encryption_at_rest: false
   high_availability: false
   monitoring: true

3. GLOBAL IT OPS POLICIES APPLIED
   ✓ enforce-encryption: encryption_at_rest = true (enforced)
   ✓ enforce-monitoring: already enabled (no change)
   ✓ enforce-compliance-tags: added tags (cost-center, compliance, data-classification)

4. TEAM OVERRIDES (if provided)
   (no overrides)

5. FINAL RESOURCE CONFIGURATION
   resource_id: sql-my-service-staging-001
   sku: Standard_S2
   backup_retention_days: 14
   encryption_at_rest: true          ← from policy
   encryption_in_transit: true       ← from policy
   high_availability: false
   monitoring: true
   app_insights: enabled             ← from policy
   tags:
     cost-center: platform-ops
     compliance: sox-2-compliant
     data-classification: internal
   connection_string: Server=sql-my-service-staging-001.database.windows.net;...

──────────────────────────────────────────────────────────
```

### What's Shown

This full chain is shown to:
1. **Before provisioning:** deployment preview screen (teams see what they're getting)
2. **After provisioning:** deployment status page (ops confirms what was created)
3. **Audit trail:** release record (permanent record of what was decided)

### Why This Matters

- **Teams understand:** "why is my database configured this way?"
- **Ops understand:** which policies were applied and where conflicts were resolved
- **Debugging:** if something is wrong, the resolution chain shows what happened
- **Compliance:** audit trail shows that policies were enforced
- **Feedback loop:** if teams see a policy they disagree with, they know who to talk to

---

## 6. Team Overrides (Escape Hatch)

### When to Use Overrides

Teams use overrides when:
- **They hit an edge case** the mapping doesn't support (e.g., "need Redis 6 for Streams support, but mapping defaults to Redis 5")
- **They need urgent customization** that would normally require platform team to add a new mapping class
- **They're experimenting** in dev/staging and want to try a different configuration

### Override Syntax

```yaml
apiVersion: just-deliver/v1
kind: Workload
metadata:
  name: my-service

requires:
  - type: database
    # Standard case: no overrides
    
  - type: cache
    # Urgent case: override specific fields
    overrides:
      sku: Premium_P1                    # need better performance
      persistence: enabled               # not in standard mapping
      eviction_policy: volatile-lru      # experiment with different policy
    override_reason: |
      Handling Q4 traffic spike. Standard_Basic not sufficient for peak load.
      Testing with Premium_P1 and persistence for session caching.
      Platform team: please add a 'high-performance' cache class.
```

### What Can Be Overridden

**Whitelist approach:** Platform team defines which fields teams can override. Not every field is overrideable.

Example whitelist:
```typescript
const overrideWhitelist = {
  database: ['backup_retention_days', 'connection_timeout'],
  cache: ['sku', 'eviction_policy', 'persistence'],
  queue: ['max_message_size', 'default_ttl'],
}
```

**Why whitelist:** Prevents teams from accidentally breaking critical configs (e.g., encryption settings, compliance tags).

### Approval Gates for Overrides

| Environment | Approval Required? | Rationale |
|-------------|-------------------|-----------|
| dev | No | Teams should experiment freely |
| staging | No | Safe to try different configs |
| production | Yes | Production consistency is critical; ops must approve deviations |

### Logging & Feedback Loop

Every override is logged with:
- `override_reason` (team explains why)
- `resource_type` and `field`
- `requested_value`
- `environment`
- `deployment_id` and timestamp

Platform team reviews weekly:
```
Override Report (week of July 28):
• cache.sku = Premium_P1 (5 times, reason: high-traffic apps)
  → Action: add 'high-performance' cache class to mapping
  
• database.backup_retention_days = 90 (3 times, reason: compliance requirement)
  → Action: create 'compliance' database class with 90-day retention
  
• queue.max_message_size = 512 (1 time, reason: experiment)
  → Action: monitor; likely one-off experiment
```

This feedback loop ensures the mapping evolves to cover real needs.

### Override Persistence

**Rule:** Overrides do NOT persist across deployments.

```
Deployment 1: my-service v1.2.3 with cache overrides
→ Provisioned with Premium_P1

Deployment 2: my-service v1.2.4 (same team, same workload)
→ Uses standard mapping again (no overrides unless re-specified)
```

**Why:** Prevents accidental drift. Forces explicit decision each time. If a team always needs Premium cache, they should ask platform team to add it to the mapping—don't hide it in an override.

---

## 7. Implementation Flow

### Provisioning Algorithm

```typescript
function provisionWorkload(teamDefinition, environment, globalPolicy) {
  
  // Step 1: Validate team definition
  validateWorkloadSchema(teamDefinition)
  
  // Step 2: For each resource requirement
  const resources = teamDefinition.requires.map(requirement => {
    
    // 2a. Get platform mapping for this resource type + environment
    const mapping = platformMappings[requirement.type][environment]
      || platformMappings[requirement.type]['default']
    
    if (!mapping) {
      throw new Error(`No mapping for resource type: ${requirement.type}`)
    }
    
    // 2b. Merge in global IT Ops policies
    let config = { ...mapping }
    const applicablePolicies = globalPolicy.filter(p => 
      p.applies_to.includes(requirement.type) && 
      (!p.condition || matchesCondition(p.condition, teamDefinition, environment))
    )
    
    for (const policy of applicablePolicies) {
      config = { ...config, ...policy.enforce }
      if (policy.inject) {
        config.tags = { ...config.tags, ...policy.inject.tags }
      }
    }
    
    // 2c. Apply team overrides (if provided and approved)
    if (requirement.overrides) {
      if (environment === 'production') {
        const approved = checkApprovalStatus(requirement)
        if (!approved) {
          throw new Error(`Override not approved for production: ${requirement.type}`)
        }
      }
      
      for (const [field, value] of Object.entries(requirement.overrides)) {
        if (!overrideWhitelist[requirement.type]?.includes(field)) {
          throw new Error(`Cannot override field ${field} on ${requirement.type}`)
        }
        config[field] = value
      }
    }
    
    // 2d. Validate final config
    validateResourceConfig(config, requirement.type, environment)
    
    // 2e. Generate resource ID and metadata
    const resourceId = generateResourceId(teamDefinition.metadata.name, requirement.type)
    
    // 2f. Log full resolution (for transparency)
    logResolution({
      workload: teamDefinition.metadata.name,
      resourceType: requirement.type,
      environment: environment,
      resolutionChain: {
        teamDefinition: requirement,
        platformMapping: mapping,
        afterPolicies: config,
        afterOverrides: config,
        finalResourceId: resourceId,
      },
      overrideReason: requirement.override_reason,
    })
    
    return {
      resourceId,
      type: requirement.type,
      config,
      resolutionChain: {
        teamDefinition: requirement,
        platformMapping: mapping,
        afterPolicies: config,
        finalResourceId: resourceId,
      },
    }
  })
  
  // Step 3: Construct Pulumi program and execute
  const pulumiProgram = generatePulumiCode(resources, environment)
  const job = enqueueProvisioningJob(pulumiProgram, teamDefinition.metadata.name)
  
  return {
    jobId: job.id,
    resources: resources,
    estimatedDuration: '5-10 minutes',
  }
}
```

### Resolution Chain Output (as JSON)

Stored in the release record for audit/debugging:

```json
{
  "workload": "my-service",
  "environment": "staging",
  "timestamp": "2026-07-31T14:22:00Z",
  "resources": [
    {
      "type": "database",
      "resourceId": "sql-my-service-staging-001",
      "resolutionChain": {
        "teamDefinition": {
          "type": "database"
        },
        "platformMapping": {
          "sku": "Standard_S2",
          "backup_retention_days": 14,
          "encryption_at_rest": false,
          "high_availability": false,
          "monitoring": true
        },
        "globalPoliciesApplied": [
          "enforce-encryption",
          "enforce-monitoring",
          "enforce-compliance-tags"
        ],
        "finalConfig": {
          "sku": "Standard_S2",
          "backup_retention_days": 14,
          "encryption_at_rest": true,
          "encryption_in_transit": true,
          "high_availability": false,
          "monitoring": true,
          "app_insights": true,
          "tags": {
            "cost-center": "platform-ops",
            "compliance": "sox-2-compliant",
            "data-classification": "internal"
          }
        }
      }
    }
  ]
}
```

---

## 8. Evolution Path: From Simple to Nuanced

### Phase 1 (MVP): Simplicity

- Teams declare only: `type`
- Everything else comes from platform mappings + policies
- Escape hatch: team overrides for edge cases

**Success metric:** Teams can submit a workload definition in < 5 minutes.

### Phase 2: Classes/Flavors (When Patterns Emerge)

Platform team observes overrides and realizes certain combinations are common:

```yaml
requires:
  - type: database
    class: high-availability    # teams pick from ops-defined presets
  
  - type: cache
    class: high-performance
```

Platform team defines a small set of classes (3–5 per resource type):
- `standard` (cheapest, dev/staging)
- `high-availability` (HA, backup, prod default)
- `high-performance` (larger SKU, for high-traffic apps)

**Benefit:** Teams get more control without learning all the fields. Mappings capture common patterns.

### Phase 3: Advanced Config (For Expert Teams)

Certain teams might need fine-grained control:

```yaml
requires:
  - type: database
    # Base on a class
    class: high-availability
    # But customize specific fields
    config:
      backup_retention_days: 90
      failover_region: westeurope
      read_replicas: 2
```

**Only enable for teams who've proven they understand the implications.**

---

## 9. Implications for B/C → D Transition

### B Phase (Manual Execution)

- Platform generates precise runbook from resolution chain
- Ops executes based on the runbook
- Overrides visible to ops (can approve/reject them)
- Audit trail: what ops actually did vs. what was planned

### C Phase (Structured Work Orders)

- Resolution chain becomes a structured work order
- Ops system validates overrides before approving
- Automated runbook generation
- Less manual interpretation

### D Phase (Full Automation)

- Pulumi Automation API executes the resource graph directly
- Overrides have pre-defined approval rules:
  - dev/staging: auto-approved
  - production: require IT Ops approval before provisioning
- No manual execution; platform handles it

**Key:** The `resolutionChain` and `overrides` structure stays the same from B → D. Only the executor changes.

---

## 10. Questions for Future Design

As the platform evolves, you'll need to answer:

1. **How many resource types do you need?** (database, cache, queue, storage, secrets, cdn, service-bus, app-insights, ...)
2. **Can resource types depend on each other?** (e.g., "this workload needs a database AND a separate read replica")
3. **How do you handle resource naming?** (auto-generated, team-specified, convention-based?)
4. **How do you inject runtime config?** (environment variables, files, connection strings?)
5. **How do teams reference resources in their code?** (e.g., `${resources.database.connection_string}`)
6. **What happens if provisioning fails partway through?** (rollback? manual cleanup?)
7. **Can teams scale resources without re-deploying?** (e.g., change database SKU without redeploying app)

---

## Summary

**The simplicity-for-teams / complexity-for-platform trade-off is intentional and correct.**

- Teams submit minimal definitions
- Platform team maintains mappings (centralized intelligence)
- Global policies enforce compliance and standards
- Full transparency shows teams what they're getting
- Overrides provide escape hatch for urgency
- Feedback loop from overrides drives mapping improvements

This approach scales from MVP (simple, small feature set) to mature platform (fine-grained control, many resource types) without requiring teams to change how they define workloads.
