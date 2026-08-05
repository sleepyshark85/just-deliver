# SCORE Specification Evaluation

## Overview

SCORE (https://github.com/score-spec/spec) is a platform-agnostic workload specification for containerized applications. It defines a JSON Schema (Draft 2020-12) that describes how to run containers with environment variables, health checks, port mappings, and resource requirements.

## What SCORE Addresses

**Container runtime specification:**
- Image reference
- Command/arguments override
- Environment variables
- Port mappings and service exposure
- Health probes (liveness, readiness)
- CPU/memory limits and requests
- Mounted files and volumes
- External resource dependencies (lightweight list only)

**Strengths:**
- Simple, opinionated design reduces configuration verbosity
- Platform-agnostic — targets Kubernetes, Docker Compose, and other runtimes
- Strong validation with RFC1123 naming constraints
- Extensible via resource `params` for implementation-specific provisioning

## Comparison to just-deliver

### Scope Mismatch

| Aspect | SCORE | just-deliver |
|--------|-------|--------------|
| **Focus** | Container runtime specification | Definition-driven infrastructure provisioning + release lifecycle |
| **Layer** | Application deployment only | Team definitions + IT Ops policies → dynamic resource graph |
| **Question answered** | "How do I run this container?" | "What needs to be provisioned? Who approves? How does it deploy?" |

### Key Divergences

**1. Infrastructure provisioning**
- **SCORE:** Minimal `resources` object for external dependency references only
- **just-deliver:** Full infrastructure graph modeling (databases, keyvaults, service bus, application insights, networks, security policies)
- **Gap:** SCORE cannot express the complexity of just-deliver's team-defined + IT Ops-defined resource requirements

**2. Release approval workflow**
- **SCORE:** No built-in workflow or approval concept
- **just-deliver:** Structured release lifecycle with enforced approval gates, stakeholder sign-offs, and audit trail
- **Gap:** SCORE is stateless; just-deliver tracks release state transitions

**3. Secrets management**
- **SCORE:** No native support for secrets or credential handling
- **just-deliver:** Mandates Azure Key Vault from day one; platform injects secrets automatically in D phase
- **Gap:** SCORE assumes secrets are handled outside the specification

**4. Multi-layer policy model**
- **SCORE:** Flat structure; each workload is independent
- **just-deliver:** Two-layer definition model where IT Ops global policies merge with team-specific declarations
- **Gap:** SCORE has no mechanism for platform-wide policy injection

**5. Executor abstraction**
- **SCORE:** Stateless; no concept of who/what executes the specification
- **just-deliver:** `executor` field enables B/C→D transition — same task structure, different executor (manual → automated)
- **Gap:** SCORE cannot model the evolution from manual to automated execution

### Technical Fit Assessment

**SCORE as optional workload format:**
- ✅ Could be nested within a just-deliver workload definition to specify container runtime config
- ✅ Already covers health checks and resource constraints
- ❌ Insufficient for declaring infrastructure needs (no database schemas, vault policies, network topology)
- ❌ No hooks for release approvals or multi-stakeholder workflows
- ❌ No policy composition mechanism

## Potential Integration Path

If adopting SCORE as a component:

```yaml
# just-deliver workload definition
workload:
  name: my-service
  team: platform-team
  
  # Container runtime (SCORE-compatible subset)
  container:
    image: my-registry/my-service:latest
    variables:
      LOG_LEVEL: debug
    ports:
      - port: 8080
        protocol: TCP
    resources:
      limits:
        memory: 512Mi
        cpu: 500m
    livenessProbe:
      httpGet:
        port: 8080
        path: /health
  
  # Infrastructure requirements (just-deliver specific)
  requires:
    - type: azure-sql-database
      class: standard
      metadata:
        annotations:
          backup-retention: "30d"
    - type: azure-keyvault
      id: secrets
    - type: azure-service-bus
      id: events
  
  # Release workflow (just-deliver specific)
  approval:
    required: true
    stakeholders:
      - role: development-lead
      - role: ops-lead
      - role: security-reviewer
```

**Rationale:**
- SCORE handles the container layer cleanly
- just-deliver wraps it with infrastructure, policy, and approval requirements
- Clear separation of concerns: container runtime vs. infrastructure provisioning

## Recommendation

**SCORE is not a fit as your primary workload definition model.** It solves a different problem—container deployment, not infrastructure provisioning or release orchestration.

**However, SCORE could serve as a component:** Adopt SCORE (or a SCORE-compatible subset) as the schema for the container runtime layer within a larger just-deliver workload definition. This keeps container concerns separate and maintains alignment with industry standards.

### What Remains Unaddressed by SCORE

1. **Dynamic resource graph construction** — still your responsibility (Pulumi-driven)
2. **Two-layer policy composition** — still team + IT Ops layers
3. **Release lifecycle and approvals** — still structured in just-deliver
4. **Secrets injection and vault integration** — still Azure Key Vault from day one
5. **Executor abstraction for B/C→D transition** — still your abstraction

## SCORE Evaluation Caveats

**Known SCORE limitations (beyond scope):**
- No security context (capabilities, SELinux)
- No image pull credentials or registry authentication
- Missing health probe timing parameters (initialDelaySeconds, timeoutSeconds, failureThreshold)
- No restart or scaling policies
- Resource ID pattern allows consecutive hyphens, creating ambiguity
- Deprecated array syntax for files/volumes still supported, creating migration friction
