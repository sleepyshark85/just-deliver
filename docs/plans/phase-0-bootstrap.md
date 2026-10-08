# Phase 0 — Manual Bootstrap Runbook

> Status: design reference — not yet reviewed as decisions. Proposed decisions live in [decisions/](../decisions/).

One-time manual steps before any automation can run (the tooling that would script them does not
exist yet). Estimated under 2 hours for one operator. Afterwards all provisioning is Pulumi via the
IDP. Related: [../architecture/landing-zone.md](../architecture/landing-zone.md),
[../architecture/network.md](../architecture/network.md), [mvp.md](mvp.md); platform bootstrap and
platform privilege are open questions D25 and D19 in [../open-questions.md](../open-questions.md).

## Pre-conditions

- Azure account with billing access (EA or Pay-As-You-Go).
- A verified domain you own (e.g. `litmos.com`) and access to its DNS registrar for TXT records.
- Azure CLI installed (`az --version`).

## Step 1 — Create the Entra ID tenant (nothing exists before it)

1. portal.azure.com → **Microsoft Entra ID** → **Manage tenants** → **Create** → **Workforce tenant**.
2. Organization name `<your org name>`; initial domain `<yourorg>.onmicrosoft.com` (temporary);
   region closest to primary users.
3. You are now the first Global Administrator. Record the **Tenant ID** (Entra ID → Overview).

## Step 2 — Add and verify the custom domain (needs registrar access)

1. Entra ID → **Custom domain names** → **Add custom domain** → e.g. `litmos.com`.
2. Add the displayed TXT record at the registrar; wait for propagation (typically 5–15 min).
3. Click **Verify**; set it as **Primary domain**.
4. Repeat for additional domains needed per landing zone (e.g. `team-a.litmos.com`).

## Step 3 — Link the billing account (portal-only, requires accepting terms)

1. **Cost Management + Billing** → confirm the billing account is associated with the tenant.
2. EA: have the Microsoft account representative associate the enrollment with the tenant.
3. Pay-As-You-Go: **Billing account → Payment methods** → add a payment method.

Without this, no subscriptions can be created.

## Step 4 — Management group hierarchy

1. Portal → **Management Groups** → **Start using management groups** (first-time activation).
2. Grant yourself Management Group Contributor at root:
   ```
   az role assignment create \
     --role "Management Group Contributor" \
     --assignee <your-user-object-id> \
     --scope /providers/Microsoft.Management/managementGroups/<tenant-id>
   ```
3. Create the hierarchy:
   ```
   Root (Tenant Root Group)
   └── <Org> (e.g. "litmos")
       ├── Platform        connectivity, identity, management subscriptions
       ├── Production      production workload subscriptions
       ├── NonProduction   dev/test workload subscriptions
       └── Sandbox         experimental, isolated, auto-expiring
   ```
   For each group (create `<Org>` first, then children with `--parent "<Org>"`):
   ```
   az account management-group create \
     --name "<group-name>" \
     --display-name "<Display Name>" \
     --parent "<parent-group-name>"
   ```
   Group `--name` values are permanent IDs; display names can change later.

## Step 5 — Platform subscriptions (billing permissions not yet delegated)

| Subscription | Purpose |
|---|---|
| `sub-platform-connectivity` | Virtual WAN, hub VNets, firewall, DNS, VPN/ExpressRoute gateways |
| `sub-platform-identity` | Entra ID domain controllers, PIM configuration, auxiliary identity resources |
| `sub-platform-management` | Log Analytics, Automation accounts, Pulumi state backend |

For each: Portal → **Subscriptions** → **Add** → type matching the billing agreement → name as
above → move under Platform:
```
az account management-group subscription add \
  --name "Platform" \
  --subscription "<subscription-id>"
```

## Step 6 — Pulumi state backend (the one thing Pulumi cannot bootstrap)

In `sub-platform-management`:
```
az group create \
  --name "rg-platform-pulumi-state" \
  --location "westeurope" \
  --subscription "<sub-platform-management-id>"

# name: globally unique, 3–24 lowercase alphanumeric
az storage account create \
  --name "stpulumistate<suffix>" \
  --resource-group "rg-platform-pulumi-state" \
  --location "westeurope" \
  --sku "Standard_LRS" \
  --allow-blob-public-access false \
  --min-tls-version "TLS1_2" \
  --subscription "<sub-platform-management-id>"

az storage container create \
  --name "pulumi-state" \
  --account-name "stpulumistate<suffix>"

# versioning + soft delete protect against state corruption
az storage account blob-service-properties update \
  --account-name "stpulumistate<suffix>" \
  --resource-group "rg-platform-pulumi-state" \
  --enable-versioning true \
  --enable-delete-retention true \
  --delete-retention-days 30
```
IDP configuration: `PULUMI_BACKEND_URL=azblob://pulumi-state` (plus the storage account name).

## Step 7 — IDP service principal (must be created by a Global Administrator)

```
az ad sp create-for-rbac \
  --name "sp-just-deliver-idp" \
  --role "Contributor" \
  --scopes "/providers/Microsoft.Management/managementGroups/<org-management-group>"
```
Store `appId`, `password`, `tenant` immediately — the password cannot be retrieved again.

Additional roles for managing subscriptions and policies:
```
az role assignment create \
  --role "Owner" \
  --assignee "<appId>" \
  --scope "/providers/Microsoft.Management/managementGroups/<org-management-group>"

az role assignment create \
  --role "Management Group Contributor" \
  --assignee "<appId>" \
  --scope "/providers/Microsoft.Management/managementGroups/<root-tenant-group>"
```
Note: Owner at the org group subsumes the initial Contributor; the breadth of this identity is
open question D19. Subscription creation via API also needs billing-scope permissions (B5).

Store credentials in a bootstrap Key Vault in `sub-platform-management`:
```
az keyvault create \
  --name "kv-platform-bootstrap" \
  --resource-group "rg-platform-pulumi-state" \
  --location "westeurope"

az keyvault secret set --vault-name "kv-platform-bootstrap" --name "idp-sp-client-id"     --value "<appId>"
az keyvault secret set --vault-name "kv-platform-bootstrap" --name "idp-sp-client-secret" --value "<password>"
az keyvault secret set --vault-name "kv-platform-bootstrap" --name "idp-sp-tenant-id"     --value "<tenant>"
```

## Step 8 — Validate

```bash
az account list --output table                                   # tenant + billing linkage
az account management-group list --output table                  # hierarchy
az account management-group show --name "Platform" --expand --recurse   # platform subs placed
az storage container list --account-name "stpulumistate<suffix>" --output table  # state backend
az login --service-principal --username "<appId>" --password "<password>" --tenant "<tenant>"
```
All passing means Phase 1 can start.

## Outputs to record

| Output | Source |
|---|---|
| Tenant ID | Entra ID → Overview |
| Root management group ID | equals Tenant ID |
| Org management group name | Management Groups |
| `sub-platform-connectivity` / `-identity` / `-management` IDs | Subscriptions list |
| Pulumi state storage account name | Azure Storage |
| Pulumi state container | `pulumi-state` |
| IDP SP `appId`, `tenantId` | App registrations |
| IDP SP `clientSecret` | Key Vault `kv-platform-bootstrap` |

## What follows (Pulumi via the IDP)

- Phase 1 — Governance and observability: Azure Policies, RBAC roles, Entra groups, Log Analytics, budgets.
- Phase 2 — Network backbone: Virtual WAN, regional hubs, Azure Firewall, Private DNS zones, Front Door.
- Phase 3 — Per-team landing zones: subscriptions, spoke VNets, NSGs, Key Vaults, managed identities, workload resources.
