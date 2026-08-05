# Just-Deliver: Missing Features & Design Gaps

This document catalogs features and design decisions that are not yet specified in just-deliver but will likely be needed for a complete platform.

## 1. Container Runtime Configuration

**What's missing:**
- Health check timing parameters (initialDelaySeconds, timeoutSeconds, failureThreshold)
- Image pull credentials for private container registries
- Security context (capabilities, SELinux, run-as-user, privileged mode)
- Restart policies and replica count specification
- Clear distinction between resource requests vs. limits

**Why it matters:**
- Health checks without timing are useless (containers fail to stabilize)
- Private registries are common in enterprise environments
- Security context is non-negotiable for compliance
- Replica count determines availability and cost

**Design decision needed:**
- Adopt SCORE subset for container layer, or define custom schema?
- Where do security policies come from—team definition or global IT Ops policy?

---

## 2. Multi-Environment Promotion

**What's missing:**
- How a release moves through dev → staging → production
- Whether approvals/gates differ per environment
- In-place promotion (promote existing release) vs. re-submit-per-environment
- Rollback strategy and trigger (manual decision vs. automatic threshold)
- How to handle environment-specific configuration changes

**Why it matters:**
- Release pipeline is core to just-deliver's value prop
- Current docs describe approval gates but not environment flow
- Rollback is critical for production safety

**Design decision needed:**
- Is the same release artifact promoted, or does each env get its own?
- Can ops rollback unilaterally, or does it require approval?
- Who triggers promotion to next environment?

---

## 3. Secrets Lifecycle & Declaration

**What's missing:**
- How teams explicitly declare which secrets a workload needs
- Whether secret requirements are implicit (inferred from resource type) or explicit
- In B phase: how secret *values* are collected and stored (form entry vs. import)
- Secret rotation, versioning, and access control
- How apps access secrets (env var vs. file mount vs. direct Key Vault read)

**Why it matters:**
- Secrets are mandatory for any real workload
- Without explicit declaration, ops doesn't know what to fill in
- In D phase, apps need a way to fetch secrets at runtime

**Design decision needed:**
- One secret per resource, or arbitrary team-defined secrets?
- Is secret storage (Key Vault) transparent to apps, or does app code know about it?

---

## 4. Configuration Management

**What's missing:**
- How teams specify environment-specific configuration (DB connection strings, API endpoints, feature flags)
- Whether configuration is stored in Key Vault, app config service, or platform DB
- Whether config changes trigger redeployment
- Config versioning and change history

**Why it matters:**
- Env-specific config is common (dev/staging/prod endpoints differ)
- Current design talks about "infra requirements" but not app configuration
- Config drift is a major operational problem

**Design decision needed:**
- Is config a first-class resource type (like database), or implicit in workload?
- Can teams update config post-deployment without redeploying?

---

## 5. Networking & Service Exposure

**What's missing:**
- How workloads expose themselves to traffic (Ingress, Load Balancer, internal-only)
- Routing rules and hostname/path mapping
- Service-to-service authentication (mTLS, service mesh, API keys)
- DNS naming convention and discovery
- Network policies (which services can talk to which)

**Why it matters:**
- Traffic routing is fundamental to multi-service deployments
- Service-to-service auth is a security requirement
- Network policies are required for compliance (least privilege)

**Design decision needed:**
- Is networking part of team definition, or IT Ops global policy?
- Does just-deliver orchestrate service mesh, or assume it exists?

---

## 6. Observability & Monitoring

**What's missing:**
- How teams declare what metrics/logs their workload produces
- Custom metric definitions and dashboards
- Alerting rules and escalation paths
- How App Insights (or other observability tool) integrates with provisioning
- Debugging/troubleshooting support in platform UI

**Why it matters:**
- Ops can't support what they can't see
- App Insights is provisioned but relationship to workload is unclear
- Observability is part of "fully audited" promise

**Design decision needed:**
- Are observability configs part of workload definition, or inferred from resource types?
- Who owns alerting thresholds—dev team or ops?

---

## 7. Workload Manifest Schema

**What's missing:**
- A single, clear "team submits THIS" document format
- Whether it's YAML, JSON, or multiple files
- Version and schema evolution strategy
- Validation error messages and recovery path

**Why it matters:**
- Current design describes intent (team layer + IT Ops layer merge), but not the actual form teams use
- Without a clear schema, API can't validate early
- Version evolution affects backwards compatibility

**Design decision needed:**
- Single YAML file per workload, or multiple files?
- Is SCORE used as-is, wrapped, or replaced entirely?
- How does platform validate and give feedback to teams?

---

## 8. Audit & Compliance

**What's missing:**
- What events are logged and retained
- How long logs are kept
- Access control to sensitive audit records (who can view secret change history?)
- Compliance reporting (SOC 2, regulatory requirements)
- Change tracking for all infra changes, not just deployments

**Why it matters:**
- "Fully audited" is a core platform promise
- Regulatory requirements often mandate audit trail retention

**Design decision needed:**
- Is audit in platform DB, or delegated to external system?
- What's the retention policy?

---

## 9. Approval Gate & Rejection Workflow

**What's missing:**
- Detailed approval gate logic (unanimous vs. majority, conditional rules)
- What happens when a release is rejected
- Can it be re-submitted without changes, or must issues be resolved?
- Notification and escalation for pending approvals
- Time limits for approval decisions

**Why it matters:**
- Approvals are your pain-point fix in B/C
- Rejected releases need a clear path forward
- Platform needs to prevent approval deadlock

**Design decision needed:**
- Can approvers comment on rejections?
- Is there a re-review time limit?

---

## 10. In-Place Updates & Canary Deployments

**What's missing:**
- Whether deployments are blue-green, rolling, or canary
- How to control blast radius (which environments/replicas get updated first)
- Traffic shifting strategy (immediate cutover vs. gradual ramp)
- Automated rollback on error threshold

**Why it matters:**
- Production safety depends on deployment strategy
- Gradual rollout is industry best practice

**Design decision needed:**
- Is strategy per-environment, per-workload, or global IT Ops policy?
- Can teams request canary for experimental features?

---

## Priority for MVP (B/C)

Needed immediately:
1. **Workload manifest schema** — teams need to know what to submit
2. **Secrets declaration & lifecycle** — operations can't proceed without knowing what secrets to fill in
3. **Multi-environment promotion** — core to release flow
4. **Approval gate logic** — differentiates B/C from current manual process

Nice-to-have for MVP:
5. Container runtime config (can start simple, add over time)
6. Configuration management (can start env-var-only)

Can defer to D/future:
- Networking (assume static infrastructure for now)
- Observability (add dashboards later)
- Canary/gradual rollout (start with blue-green)
- In-place updates (orchestration refinement)
