# MVP Phase 1: Workload Definition & Auto-Provisioning

## Goal
Prove the core IDP capability: a developer defines their workload in YAML and the platform provisions + deploys it from scratch in a single command.

## Scope

### Developer Experience
- Write a workload definition in YAML (app name, owning team, environment, container image, and the resource types the app needs)
- Run one CLI command: `platform deploy workload.yaml`
- App is provisioned and live on Azure with all dependencies (database, App Insights)
- Secrets and connection strings automatically configured so the app can use them

### Platform Responsibilities
- Parse workload YAML definition and translate to Pulumi configuration
- Generate and execute Pulumi infrastructure code to provision Azure resources
- Deploy containerized app to provisioned infrastructure
- Inject secrets and connection strings (database credentials, connection strings, etc.)
- CLI-based for now; architecture designed to move logic server-side later

## Prerequisites
- Existing landing zone on Azure (network, resource groups, basic security setup)
- Developer has Azure credentials and Pulumi state access

## Tech Stack
- **Workload Definition:** YAML
- **Infrastructure Provisioning:** Pulumi with Automation API (invoked from C# code)
- **Cloud Provider:** Azure
- **Sample App:** .NET 10 console/web app
- **CLI Implementation:** .NET 10 (using Pulumi.Automation NuGet package)
- **Pattern:** Everything orchestrated in code, no manual Pulumi CLI commands

---

## High-Level Design

### System Components

1. **CLI Tool** — Developer-facing entry point
   - Reads and validates workload.yaml
   - Invokes Platform Engine

2. **Platform Engine** — Core orchestration logic
   - Parses workload definition
   - Generates Pulumi infrastructure code
   - Calls Pulumi Automation API to provision resources
   - Coordinates deployment

3. **Pulumi Automation API Layer** — Infrastructure provisioning (invoked from CLI code)
   - Takes workload definition
   - Uses Pulumi Automation API to invoke Pulumi infrastructure project programmatically
   - Executes Pulumi program to create Azure resources (App Service, PostgreSQL, App Insights)
   - Captures resource outputs (connection strings, IDs, etc.)
   - Manages local state files

4. **Deployment Engine** — Application deployment
   - Pulls container image (or builds if needed)
   - Deploys to provisioned App Service
   - Sets environment variables

5. **Secret Injector** — Configuration management
   - Retrieves secrets from source (local env, Key Vault)
   - Injects into running app (environment variables, config files)

### Data Flow

```
Developer runs: platform deploy workload.yaml
         ↓
[CLI Tool (C#)]
├─ Read and validate workload.yaml
├─ Parse YAML into workload definition
├─ Invoke Pulumi Automation API with workload config
│
└─ [Pulumi Infrastructure Project]
   ├─ Create App Service Plan + App Service
   ├─ Create PostgreSQL database + server
   ├─ Create Application Insights
   └─ Return outputs (connection strings, resource IDs)
         ↓
[CLI Tool (C#)]
├─ Capture Pulumi outputs
├─ Pull container image from ACR
├─ Deploy container to App Service
├─ Inject secrets as environment variables
└─ Verify app is healthy
         ↓
Developer has live app with working database
```

**Note:** All orchestration happens in C# code via Pulumi Automation API. No manual Pulumi CLI commands.

---

## Workload Definition Schema

Canonical schema: [`schemas/workload.schema.json`](../schemas/workload.schema.json). Design rationale: [Resource Provisioning & Workload Definition Strategy](investigation/resource-provisioning-strategy.md). The MVP implements a subset of that schema — it does not define a separate shape.

Example `workload.yaml`:

```yaml
apiVersion: just-deliver/v1
kind: Workload

metadata:
  name: my-app
  team: platform-team
  environment: dev

container:
  image: myregistry.azurecr.io/dotnet-sample:1.0.0
  variables:
    LOG_LEVEL: info
  ports:
    - port: 8080

requires:
  - type: database
```

### Workload Definition Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `apiVersion` | string | Yes | `just-deliver/v1` |
| `kind` | string | Yes | `Workload` |
| `metadata.name` | string | Yes | App name, used as a resource prefix. DNS-safe, max 40 chars. |
| `metadata.team` | string | Yes | Owning team |
| `metadata.environment` | string | Yes | `dev` in MVP; `staging`/`production` in later phases |
| `container.image` | string | Yes | Fully qualified image reference including tag |
| `container.variables` | map | No | Environment variables. May reference resource outputs with `${resources.<type>.<output>}` |
| `container.ports` | array | No | Ports the container listens on |
| `requires[].type` | string | Yes | Resource type the app needs. `database` in MVP. |

### What Teams Do Not Specify

Deliberately absent from the definition, per the two-layer model:

| Concern | Where it comes from |
|---------|--------------------|
| Database engine, version, SKU | Platform mapping for `database` |
| App Service tier / compute size | Platform mapping for the container runtime |
| Application Insights and Log Analytics | `enforce-monitoring` global policy — attached to every workload, never requested |
| Encryption, tags, network rules | Global IT Ops policies |
| Approvals and sign-off | Release process, defined outside the workload |

Teams declare *what*, not *how*. If a team hits a case the mapping does not cover, they use the `overrides` escape hatch (which requires an `override_reason`), not a new workload field.

---

## Out of Scope (Phase 2+)

- Multiple environments (staging, prod) — only dev in MVP
- Multiple resource types — `database` only; no mappings yet for cache, queue, storage, secrets, or cdn
- Multiple database engines — the `database` mapping resolves to a single engine
- Team overrides — the schema accepts them; the MVP does not yet apply the whitelist or approval gates
- Advanced networking (VPNs, private endpoints, custom routing)
- Approval workflows or governance policies
- GUI/dashboard — CLI only
- Monitoring dashboards or alerts
- Auto-scaling or cost optimization
- Rollback capabilities
- Team-level quotas or cost tracking
- Secret rotation or lifecycle management

---

## Key Decisions

- **Language:** .NET 10 (CLI, Pulumi code)
- **Infrastructure as Code:** Pulumi Automation API
- **Definition Format:** YAML
- **CLI Execution:** Developer laptop; uses `az login` for Azure credentials
- **Pulumi State:** Local state file in `.pulumi/` directory (gitignored)
- **Secret Storage & Injection:** Environment variables; Pulumi sets them on App Service during deployment. No Key Vault in MVP.
- **Container Image:** Pre-built and stored in Azure Container Registry; Pulumi pulls at deployment time
- **Sample App:** Realistic — .NET Web API with `/health` endpoint + database query endpoint to validate end-to-end connectivity
- **Error Handling:** Fail fast on errors; no auto-rollback. Manual cleanup documented for failed deployments.

---

## Success Criteria

1. **Developer can deploy in one command:**
   - Write `workload.yaml`
   - Run `platform deploy workload.yaml`
   - App is live and accessible (no manual steps)

2. **Full provisioning from scratch:**
   - App Service created and running
   - PostgreSQL database provisioned and accessible
   - App Insights monitoring configured
   - All connection strings auto-injected

3. **Time to deployment:** < 10 minutes from `platform deploy` to live app

4. **Proof of concept:**
   - Demonstrates that infrastructure-as-code driven by developer definitions works
   - Proves the core IDP model: teams define, platform provisions

5. **Code quality:**
   - Pulumi code is idempotent (can run again without errors)
   - CLI is documented and user-friendly