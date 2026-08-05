# Implementation Plan: just-deliver MVP

## Overview

This plan sequences all work needed to deliver the just-deliver IDP MVP. It's organized into two phases:
- **Phase 0 (1-2 weeks):** Landing Zone setup
- **Phase 1 (3-4 weeks):** Platform implementation

Total estimate: 4-6 weeks solo.

## Architectural Approach

**Everything is driven from code via Pulumi Automation API:**
- CLI tool programmatically invokes Pulumi infrastructure project
- No manual `pulumi` CLI commands
- No shell scripts or external automation
- Infrastructure provisioning is orchestrated entirely from C# code
- Workload definition (YAML) → Pulumi Automation API → Resource provisioning → Secret injection → App deployment

This is the idiomatic pattern for IDP platforms using Pulumi.

---

## Phase 0: Landing Zone MVP (1-2 weeks)

### Goal
Establish minimal Azure foundation to enable workload provisioning.

### Prerequisites
- Azure subscription created and accessible
- Azure CLI installed and authenticated (`az login`)
- Pulumi CLI installed
- .NET 10 SDK installed

### Tasks

#### Task 0.1: Set up Pulumi landing zone project
- Create directory: `pulumi/landing-zone/`
- Initialize Pulumi project: `pulumi new azure-csharp`
- Configure stack: `Pulumi.dev.yaml`
- Install Azure SDK NuGet packages
- **Deliverable:** Pulumi project skeleton, ready for code

#### Task 0.2: Create resource groups
- Provision 3 resource groups:
  - `rg-org` — Shared services
  - `rg-workloads` — Application workloads
  - `rg-monitoring` — Monitoring/logging
- **Deliverable:** 3 resource groups in Azure

#### Task 0.3: Create Key Vault
- Provision Azure Key Vault: `kv-just-deliver`
- Enable soft delete and purge protection
- Grant Service Principal access (via RBAC)
- **Deliverable:** Key Vault accessible and ready for secrets

#### Task 0.4: Create Log Analytics workspace
- Provision Log Analytics workspace: `law-just-deliver`
- Configure retention policy
- **Deliverable:** Log Analytics workspace ready to receive logs

#### Task 0.5: Create Service Principal
- Create Service Principal: `sp-just-deliver-platform`
- Assign Contributor role on subscription
- Store credentials securely (locally, not in git)
- **Deliverable:** Service Principal with credentials documented

#### Task 0.6: Configure Pulumi for CLI usage
- Export Service Principal credentials to environment variables
- Document how to set up Pulumi authentication
- Test that Pulumi can create resources
- **Deliverable:** Pulumi can provision resources via CLI

#### Task 0.7: Test landing zone
- Run `pulumi up` — verify all resources created
- Run `pulumi destroy` — verify all resources deleted
- Run `pulumi up` again — verify idempotency
- **Deliverable:** Landing zone is reproducible and works

#### Task 0.8: Document landing zone
- Document how to provision the landing zone
- Document Service Principal setup
- Document how to access Key Vault
- Document how to view Log Analytics logs
- **Deliverable:** `LANDING_ZONE_SETUP.md` (runbook)

---

## Phase 1: Platform MVP (3-4 weeks)

### Goal
Build CLI tool that deploys workloads end-to-end (YAML definition → provisioned + deployed app).

### Prerequisites
- Landing zone provisioned (Phase 0 complete)
- Service Principal credentials available
- Azure subscription with landing zone resources

### Tasks

#### Task 1.1: Create sample .NET app
- Create simple .NET Web API project
- Add `/health` endpoint
- Add `/api/data` endpoint (queries PostgreSQL database)
- Add database connection logic (Entity Framework or raw SQL)
- Create Dockerfile for containerization
- **Deliverable:** `.NET app with database integration, Docker-ready`

#### Task 1.2: Build and push container image
- Build Docker image locally
- Push to Azure Container Registry (ACR)
  - Create ACR: `acrjustdeliver`
  - Store image: `acrjustdeliver.azurecr.io/sample-app:v1`
- Document image location and how to update it
- **Deliverable:** Container image available in ACR

#### Task 1.3: Create Pulumi infrastructure project (invoked by Automation API)
- Create directory: `pulumi/workload/`
- Initialize Pulumi project in C# (will be invoked by CLI via Automation API)
- Create `Pulumi.yaml` with stack configuration
- Organize code by component:
  - `__main__.cs` — Entry point, main stack definition
  - `app_service.cs` — App Service provisioning
  - `database.cs` — PostgreSQL provisioning
  - `app_insights.cs` — App Insights provisioning
  - `config.cs` — Read workload definition from Pulumi config
  - `outputs.cs` — Export resource IDs, connection strings, etc.
- Ensure project exports outputs that CLI can capture via Automation API
- **Deliverable:** Pulumi project structure ready to be invoked programmatically

#### Task 1.4: Implement App Service provisioning (Pulumi)
- Code to create App Service Plan
- Code to create App Service
- Configure environment variables (will be populated dynamically)
- **Deliverable:** Pulumi can provision App Service

#### Task 1.5: Implement PostgreSQL provisioning (Pulumi)
- Code to create PostgreSQL Server
- Code to create PostgreSQL Database
- Configure firewall rules (allow from App Service)
- Generate connection string dynamically
- **Deliverable:** Pulumi can provision PostgreSQL database

#### Task 1.6: Implement App Insights provisioning (Pulumi)
- Code to create Application Insights instance
- Link to App Service
- **Deliverable:** Pulumi can provision App Insights

#### Task 1.7: Implement secret injection (Pulumi)
- Populate connection string as App Service environment variable
- Set database username/password as environment variables
- Store generated secrets in Key Vault
- **Deliverable:** App can access database via env vars

#### Task 1.8: Create CLI project (.NET)
- Create .NET console app project: `just-deliver-cli`
- Add NuGet package: `Pulumi.Automation`
- Add argument parsing (workload YAML file path)
- Add file I/O and YAML parsing
- Project structure:
  - `Program.cs` — Entry point, orchestration flow
  - `YamlParser.cs` — Parse and validate workload.yaml
  - `PulumiOrchestrator.cs` — Use Pulumi Automation API to invoke infrastructure project
  - `Deployment.cs` — Deploy container and inject secrets
  - `Models.cs` — WorkloadDefinition, ProvisioningResult classes
- **Deliverable:** CLI skeleton with Pulumi Automation API support

#### Task 1.9: Implement YAML parsing
- Parse workload.yaml schema:
  - `metadata.name`
  - `spec.appTemplate`
  - `spec.database.type`
  - `spec.monitoring.appInsights`
  - `spec.resources.compute`
- Validate required fields
- Return structured workload definition
- **Deliverable:** YAML parser with validation

#### Task 1.10: Implement Pulumi Automation API integration
- Reference Pulumi.Automation NuGet package in CLI project
- Create automation client that references the Pulumi workload project
- Implement `DeployWorkload()` method in CLI:
  - Create or select Stack programmatically
  - Pass workload definition as Pulumi config (name, appTemplate, database type, etc.)
  - Call `UpAsync()` to trigger provisioning (no shell commands, all C# code)
  - Capture structured output (resource IDs, connection strings, secrets)
  - Handle errors and return results to caller
- No manual `pulumi` CLI invocation — everything in C# code
- **Deliverable:** CLI uses Pulumi Automation API to programmatically provision resources

#### Task 1.11: Implement app deployment
- Pull container image from ACR
- Deploy to App Service via Azure SDK
- Wait for deployment to complete
- **Deliverable:** CLI can deploy container to App Service

#### Task 1.12: Implement secret injection into app
- Retrieve generated secrets from Pulumi output
- Set as App Service environment variables
- Restart app to pick up new config
- **Deliverable:** App receives secrets and can connect to database

#### Task 1.13: Create sample workload.yaml
- Example file showing schema
- Reference configuration for sample .NET app
- Document all fields
- **Deliverable:** `workload-example.yaml`

#### Task 1.14: End-to-end test
- Write workload.yaml with sample app config
- Run `just-deliver deploy workload.yaml`
- Verify:
  - App Service created and running
  - PostgreSQL database created and accessible
  - App Insights monitoring configured
  - App deployed and receiving traffic
  - `/health` endpoint responds
  - `/api/data` endpoint queries database successfully
- **Deliverable:** Complete end-to-end workflow functional

#### Task 1.15: Error handling and recovery
- Add error handling for provisioning failures
- Log errors clearly
- Document manual cleanup steps
- Add retry logic for transient failures
- **Deliverable:** CLI handles failures gracefully

#### Task 1.16: Documentation
- Document CLI usage: `platform deploy workload.yaml`
- Document workload.yaml schema
- Document how to update/modify deployed workload
- Document how to destroy resources
- Create troubleshooting guide
- **Deliverable:** `PLATFORM_USAGE.md` (user guide)

#### Task 1.17: Final testing and validation
- Test with multiple workload definitions
- Test destroy and recreate
- Verify idempotency
- Measure deployment time
- **Deliverable:** MVP meets success criteria

---

## Task Dependencies & Critical Path

```
Phase 0: Landing Zone
├─ 0.1: Pulumi project setup
├─ 0.2: Resource groups
├─ 0.3: Key Vault
├─ 0.4: Log Analytics
├─ 0.5: Service Principal
├─ 0.6: Pulumi authentication
├─ 0.7: Test landing zone ← GATE: Must succeed before Phase 1
└─ 0.8: Documentation

Phase 1: Platform
├─ 1.1: Sample .NET app
├─ 1.2: Container image
├─ 1.3: Pulumi workload project setup
├─ 1.4: App Service provisioning
├─ 1.5: PostgreSQL provisioning ← Depends on: 1.3
├─ 1.6: App Insights provisioning ← Depends on: 1.3
├─ 1.7: Secret injection ← Depends on: 1.4, 1.5
├─ 1.8: CLI project
├─ 1.9: YAML parsing ← Depends on: 1.8
├─ 1.10: Pulumi Automation API ← Depends on: 1.9, 1.3, 1.4, 1.5, 1.6, 1.7
├─ 1.11: App deployment ← Depends on: 1.2, 1.10
├─ 1.12: Secret injection into app ← Depends on: 1.7, 1.11
├─ 1.13: Sample workload.yaml
├─ 1.14: End-to-end test ← Depends on: 1.13, 1.12 ← GATE: MVP success
├─ 1.15: Error handling ← Depends on: 1.14
├─ 1.16: Documentation
└─ 1.17: Final validation
```

### Critical Path (Fastest possible delivery)
1. 0.1-0.7: Landing zone (1 week)
2. 1.1-1.2: Sample app + container (3-4 days)
3. 1.3-1.7: Pulumi infrastructure (4-5 days)
4. 1.8-1.12: CLI tool (4-5 days)
5. 1.13-1.14: Integration test (2-3 days)
6. 1.15-1.17: Polish and docs (2-3 days)

**Total: ~4-6 weeks solo**

---

## Success Criteria Checklist

- [ ] Landing zone provisions and destroys cleanly
- [ ] CLI accepts workload.yaml and validates schema
- [ ] Pulumi provisions App Service, PostgreSQL, App Insights
- [ ] Container image deployed to App Service
- [ ] App connects to database and queries succeed
- [ ] App Insights monitoring shows traffic
- [ ] End-to-end deploy completes in < 10 minutes
- [ ] Deployment is repeatable (idempotent)
- [ ] Documentation is complete and clear

---

## Rollback Plan

If anything goes wrong mid-phase:

**Landing Zone (Phase 0):**
- Run `pulumi destroy` in landing zone directory
- Start over with task 0.1 (minimal rework)

**Platform (Phase 1):**
- Run `pulumi destroy` in workload directory (cleans up resources)
- Fix code and retry
- If critical issue found, document it and plan Phase 2 fix

---

## Notes for Solo Development

1. **Work in order** — Tasks have dependencies; skipping hurts later
2. **Test frequently** — Don't wait until the end to test integration
3. **Document as you go** — Make notes on what you learn for the final docs
4. **Keep it simple** — MVP scope; don't add features
5. **Commit regularly** — Every completed task gets a git commit
6. **Take breaks** — Infrastructure work can have long wait times; use them for documentation
