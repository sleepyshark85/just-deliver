# Landing Zone MVP: Minimal Azure Foundation

## Goal
Establish minimal Azure foundation to enable the just-deliver platform to deploy workloads. Focus on essentials only — no networking complexity yet.

## Scope

### Core Resources
- Resource groups (organization and logging)
- Key Vault for secrets storage
- Log Analytics workspace for centralized logging
- Storage account for Pulumi state
- Service Principal for platform authentication

That's it. No VNets, subnets, NSGs, or networking setup.

## Tech Stack

- **Infrastructure as Code:** Pulumi (.NET)
- **Cloud Provider:** Azure
- **State Management:** Azure Blob Storage (Pulumi backend)
- **Region:** East US
- **Naming Convention:** `{environment}-{resource-type}-{name}` (e.g., `dev-kv-just-deliver`)

## Key Decisions

- **Provisioning:** Pulumi (.NET) — IaC, version-controlled, reproducible
- **Secrets Management:** Azure Key Vault for app secrets and connection strings
- **Pulumi State:** Local state files (git-ignored, not in repo)
- **Authentication:** Service Principal for Pulumi automation (not personal credentials)
- **Monitoring:** Log Analytics for centralized logging
- **Networking:** Defer to Phase 2 — workloads deploy without VNet constraints for MVP
- **RBAC:** Service Principal has Contributor role on subscription (simplify for MVP, restrict later)

## Core Resources to Provision

### 1. Resource Groups
- `rg-org` — Shared organization resources
- `rg-workloads` — Application workloads (created here by platform)
- `rg-monitoring` — Monitoring infrastructure

### 2. Secrets Management
- **Key Vault:** `kv-just-deliver` (store app secrets, connection strings)

### 3. Monitoring
- **Log Analytics Workspace:** `law-just-deliver` (centralized logging)

### 4. Authentication
- **Service Principal:** `sp-just-deliver-platform` (Pulumi automation, Contributor role)

## Out of Scope (Phase 2+)

- Networking (VNets, subnets, NSGs)
- Network isolation or traffic controls
- Private endpoints or secure connectivity
- DDoS protection or WAF
- Advanced RBAC or custom roles
- Azure Policy enforcement
- Multi-region deployment
- Disaster recovery or backup strategies

## Success Criteria

1. **Pulumi project provisions without errors**
   - All resources created in Azure
   - State stored in Blob Storage

2. **Service Principal has correct permissions**
   - Can create resources in all resource groups
   - Can read/write to Key Vault
   - Can write logs to Log Analytics

3. **Key Vault and logging work**
   - Secrets can be stored and retrieved
   - Log Analytics receives logs from resources

4. **Pulumi state is manageable**
   - State files stored locally (.gitignore prevents accidental commit)
   - Idempotent (can run again without errors)

5. **Documentation complete**
   - How to access Key Vault
   - How to view logs in Log Analytics
   - Service Principal credentials stored securely
   - How to destroy/recreate landing zone

## Implementation Steps

1. Create Pulumi project for landing zone
2. Provision resource groups
3. Create Key Vault
4. Create Log Analytics workspace
5. Create Service Principal with Contributor role
6. Configure .gitignore for local state files
7. Test: destroy and recreate to verify idempotency

## Files & Repository Structure

```
/pulumi
  /landing-zone
    Pulumi.yaml
    Pulumi.dev.yaml
    __main__.cs (or similar for .NET)
    .gitignore (exclude state files, credentials)
```

## Notes

- Landing zone is one-time setup, reused by all platform tests
- Separate Pulumi project from the platform CLI code
- Service principal credentials stored securely (not in git)
- Minimal scope = fast to provision, easy to destroy and recreate
