# Phase 0 — Manual Bootstrap

This phase covers everything that must be done **by hand, once, before any automation can run**. Nothing here can be scripted because the tooling that would run those scripts does not yet exist. The entire process should take under 2 hours for a single operator.

After Phase 0 is complete, all further provisioning is driven by Pulumi via the IDP.

---

## Pre-conditions

Before starting, you need:

- An Azure account with billing access (EA or Pay-As-You-Go)
- A verified domain you own (e.g. `litmos.com`) — required for custom Entra ID domains
- Access to the domain's DNS registrar to add TXT records
- A local machine with the Azure CLI installed (`az --version`)

---

## Step 1 — Create the Entra ID Tenant

**Why manual:** The tenant is the root identity container. Nothing exists before it.

1. Go to [portal.azure.com](https://portal.azure.com) and sign in with your Microsoft account.
2. Search for **Microsoft Entra ID** → **Manage tenants** → **Create**.
3. Select **Workforce tenant** and fill in:
   - Organization name: `<your org name>`
   - Initial domain: `<yourorg>.onmicrosoft.com` (temporary, will be replaced)
   - Region: choose the region closest to your primary users
4. Complete creation. You are now the first **Global Administrator**.

> Record the **Tenant ID** (visible in Entra ID → Overview). Every downstream resource will reference this.

---

## Step 2 — Add and Verify Your Custom Domain

**Why manual:** DNS verification requires human interaction with your domain registrar.

1. In Entra ID → **Custom domain names** → **Add custom domain**.
2. Enter your primary domain (e.g. `litmos.com`).
3. Azure will display a TXT record to add to your DNS zone.
4. Add the TXT record at your registrar and wait for propagation (typically 5–15 minutes).
5. Click **Verify** in the portal once the record is visible.
6. Set the verified domain as the **Primary domain**.

Repeat for any additional domains needed per landing zone type.

---

## Step 3 — Link the Billing Account

**Why manual:** Billing linkage is portal-only and requires explicit agreement to Microsoft's terms. Subscriptions cannot be created programmatically without this.

1. Navigate to **Cost Management + Billing** in the portal.
2. Confirm your billing account is associated with the tenant created in Step 1.
3. If using an Enterprise Agreement (EA): work with your Microsoft account representative to associate the EA enrollment with the tenant.
4. If using Pay-As-You-Go: add a payment method under **Billing account → Payment methods**.

> Without a linked billing account, you cannot create subscriptions in the next steps.

---

## Step 4 — Create the Management Group Hierarchy

**Why manual:** The root management group exists by default but must be activated and structured before policies or subscriptions can be organized under it.

1. In the portal, search for **Management Groups** → **Start using management groups** (first-time activation).
2. Grant yourself the **Management Group Contributor** role at the root level:
   ```
   az role assignment create \
     --role "Management Group Contributor" \
     --assignee <your-user-object-id> \
     --scope /providers/Microsoft.Management/managementGroups/<tenant-id>
   ```
3. Create the following hierarchy under the root:

   ```
   Root (Tenant Root Group)
   └── <Org> (e.g. "litmos")
       ├── Platform          ← connectivity, identity, management subscriptions
       ├── Production        ← production workload subscriptions
       ├── NonProduction     ← dev/test workload subscriptions
       └── Sandbox           ← experimental, isolated, auto-expiring
   ```

   Create each group via:
   ```
   az account management-group create \
     --name "<group-name>" \
     --display-name "<Display Name>" \
     --parent "<parent-group-name>"
   ```

> The management group names are permanent IDs — choose them carefully. Display names can be changed later.

---

## Step 5 — Create Platform Subscriptions

**Why manual:** Subscription creation from the API requires billing account permissions that are not yet delegated. These three subscriptions form the platform foundation.

Create the following subscriptions and move them under the **Platform** management group:

| Subscription | Purpose |
|---|---|
| `sub-platform-connectivity` | Azure Virtual WAN, hub VNets, firewall, DNS, VPN/ExpressRoute gateways |
| `sub-platform-identity` | Entra ID domain controllers, PIM configuration, auxiliary identity resources |
| `sub-platform-management` | Log Analytics, Automation accounts, Pulumi state backend |

For each:
1. Portal → **Subscriptions** → **Add**.
2. Select subscription type matching your billing agreement.
3. Name it using the convention above.
4. Move it to the **Platform** management group:
   ```
   az account management-group subscription add \
     --name "Platform" \
     --subscription "<subscription-id>"
   ```

---

## Step 6 — Create the Pulumi State Backend

**Why manual:** Pulumi needs a state store before it can manage any resources. This is the only resource in the platform that cannot be bootstrapped by Pulumi itself.

In the `sub-platform-management` subscription:

1. Create a resource group:
   ```
   az group create \
     --name "rg-platform-pulumi-state" \
     --location "westeurope" \
     --subscription "<sub-platform-management-id>"
   ```

2. Create a storage account (name must be globally unique, 3–24 lowercase alphanumeric):
   ```
   az storage account create \
     --name "stpulumistate<suffix>" \
     --resource-group "rg-platform-pulumi-state" \
     --location "westeurope" \
     --sku "Standard_LRS" \
     --allow-blob-public-access false \
     --min-tls-version "TLS1_2" \
     --subscription "<sub-platform-management-id>"
   ```

3. Create a blob container for state:
   ```
   az storage container create \
     --name "pulumi-state" \
     --account-name "stpulumistate<suffix>"
   ```

4. Enable versioning and soft delete on the storage account (protects against accidental state corruption):
   ```
   az storage account blob-service-properties update \
     --account-name "stpulumistate<suffix>" \
     --resource-group "rg-platform-pulumi-state" \
     --enable-versioning true \
     --enable-delete-retention true \
     --delete-retention-days 30
   ```

> Record the **storage account name** and **container name**. These go into the IDP configuration as `PULUMI_BACKEND_URL=azblob://pulumi-state`.

---

## Step 7 — Create the IDP Service Principal

**Why manual:** The IDP needs an Azure identity with permission to create and manage resources. This identity must be created by a Global Administrator before it can be used.

1. Create the service principal:
   ```
   az ad sp create-for-rbac \
     --name "sp-just-deliver-idp" \
     --role "Contributor" \
     --scopes "/providers/Microsoft.Management/managementGroups/<org-management-group>"
   ```

2. The output will contain `appId`, `password`, and `tenant`. Store all three in a secure location immediately — the password cannot be retrieved again.

3. Grant the service principal the additional roles it needs to manage subscriptions and policies:
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

4. Store the credentials in Azure Key Vault (create a bootstrap Key Vault in `sub-platform-management` first):
   ```
   az keyvault create \
     --name "kv-platform-bootstrap" \
     --resource-group "rg-platform-pulumi-state" \
     --location "westeurope"

   az keyvault secret set --vault-name "kv-platform-bootstrap" --name "idp-sp-client-id"     --value "<appId>"
   az keyvault secret set --vault-name "kv-platform-bootstrap" --name "idp-sp-client-secret"  --value "<password>"
   az keyvault secret set --vault-name "kv-platform-bootstrap" --name "idp-sp-tenant-id"      --value "<tenant>"
   ```

---

## Step 8 — Validate the Bootstrap

Before handing off to Phase 1 automation, verify the bootstrap is complete:

```bash
# Confirm tenant and billing linkage
az account list --output table

# Confirm management group hierarchy
az account management-group list --output table

# Confirm platform subscriptions are under the right management group
az account management-group show --name "Platform" --expand --recurse

# Confirm state backend is reachable
az storage container list \
  --account-name "stpulumistate<suffix>" \
  --output table

# Confirm service principal can authenticate
az login --service-principal \
  --username "<appId>" \
  --password "<password>" \
  --tenant "<tenant>"
```

All checks passing means Phase 1 (Pulumi-driven governance and network backbone) can begin.

---

## Outputs of Phase 0

Record these values — they are inputs to the IDP configuration and Phase 1 Pulumi stacks:

| Output | Where to find it |
|---|---|
| Tenant ID | Entra ID → Overview |
| Root Management Group ID | Management Groups (equals Tenant ID) |
| Org Management Group name | Management Groups hierarchy |
| `sub-platform-connectivity` subscription ID | Subscriptions list |
| `sub-platform-identity` subscription ID | Subscriptions list |
| `sub-platform-management` subscription ID | Subscriptions list |
| Pulumi state storage account name | Azure Storage |
| Pulumi state container name | `pulumi-state` |
| IDP service principal `appId` | Azure AD App registrations |
| IDP service principal `tenantId` | Azure AD App registrations |
| IDP service principal `clientSecret` | Key Vault `kv-platform-bootstrap` |

---

## What Comes Next

With Phase 0 complete, the following can be driven entirely by Pulumi via the IDP:

- **Phase 1 — Governance & Observability**: Azure Policies, RBAC roles, Entra ID groups, Log Analytics workspace, cost budgets
- **Phase 2 — Network Backbone**: Azure Virtual WAN, regional hubs, Azure Firewall, Private DNS zones, Azure Front Door
- **Phase 3 — Per-team Landing Zones**: Subscriptions, spoke VNets, NSGs, Key Vaults, Managed Identities, workload resources
