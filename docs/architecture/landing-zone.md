# Landing Zone Design

> Status: design reference — not yet reviewed as decisions. Proposed decisions live in [decisions/](../decisions/).

The landing zone is everything beneath the workload (placement, identity, network, security/compliance
baseline, observability, cost controls); it ends where app code, app auth, workload monitoring, data
and app CI/CD begin. Test: "would every workload here need this?" Yes = landing zone, no = workload ([workload-definition.md](workload-definition.md)).

Related: [network.md](network.md), [provisioning.md](provisioning.md), [resolver.md](resolver.md), [../plans/phase-0-bootstrap.md](../plans/phase-0-bootstrap.md), [../open-questions.md](../open-questions.md).

## 1. Boundary and ownership

**Ownership (A3/B10/F41; wins over anything below that conflicts)** — [0008](../decisions/0008-enforcement-in-landing-zone.md), [0009](../decisions/0009-rbac-split-team-subscription.md):

| Layer | Owner | Team access |
|---|---|---|
| Landing zone (Azure Policy, RBAC, quota, network allocation, peering, routing, DNS) | Platform team | Read-only |
| Platform resource group inside the team subscription | Platform | Reader |
| Team resource groups / environments | Team | Contributor + RBAC Administrator with ABAC condition allowlisting assignable data-plane roles |

- Dividing line: a team writes anything whose blast radius stops at its environment; the platform
  owns anything whose blast radius crosses it. Address space, peering, routing, DNS cross it
  (overlapping CIDRs break peering for everyone); subnets and NSGs inside a team's own allocation do not.
- Enforcement lives in the landing zone (Policy Deny for dangerous shapes, RBAC, quota, network);
  the platform is the paved road, not the fence. Merge-function-only checks are defeated by portal
  access, so platform validation is UX, not control. The exact split is open (F41).
- Contributor rather than Owner: its NotActions remove policy and role-assignment writes in one move.
- Accepted consequences: resource locks are an accident guard in team envs (team can remove them) and
  a real control in stage/prod; a team can revoke the platform's access to its own subscription
  (platform detects and surfaces it, does not prevent it); no standing data-plane access in
  stage/prod, so prod DB debugging needs break-glass (H48).

**Boundary unit.** Subscription is the recommended landing-zone boundary (resource group = weak,
logical grouping only; management group = governs many landing zones). Separate landing zones when
workloads differ in compliance, risk/blast radius, network topology, operational ownership, or
lifecycle (always separate prod from non-prod). Anti-patterns listed in the source: one LZ per team,
one LZ for everything, per-application, per-tech-stack. "Start broad, split later" — easier to
divide one zone than merge two. Still open: what an environment physically is (subscription vs
resource group, B5), and what must be identical across tiers (A4; candidate: identical *policy set*,
scale/SKU/replicas/data may differ).

**Management groups.** Investigation recommends Platform, Regulated, General Production, Non-Production, Sandbox
(auto-expiring); Phase 0 creates `<Org>` → Platform, Production, NonProduction, Sandbox (no Regulated) — [network.md](network.md#subscription-and-governance-hierarchy).

## 2. Landing zone definition (after schema redesign)

A declarative YAML definition, cut by the redesign to shared infrastructure only: ~8 team fields
(was 40+), 30–60 lines (was 200+), org policy auto-applied with no per-team overrides, at the
accepted cost of higher platform complexity. Teams define only what differs from the standard.

```yaml
apiVersion: "v1"
kind: "LandingZoneDefinition"
metadata:
  name: "lz-team-a"                        # ^lz-[a-z0-9-]+$
  displayName: "Team A"
  owner: { team: "Team A", email: "team-a-leads@litmos.com" }
spec:
  identity:
    domain: "team-a.litmos.com"            # must be verified in Entra ID
    accessGroups:                          # min 1
      - name: "grp-team-a-developers"      # ^grp-[a-z0-9-]+$
        roles: ["Contributor"]             # enum: Owner, Contributor, Reader, UserAccessAdministrator
  governance:
    tags: { CostCenter: "CC-12345", Team: "TeamA", Environment: "development" }
    budget: { monthlyLimitUsd: 1000 }      # optional, 100..1,000,000; default per environment
  subscriptions:                           # min 1
    - name: "sub-team-a-dev"               # ^sub-[a-z0-9-]+$
      displayName: "Team A - Dev"
      environment: "development"           # enum: development, test, production
  networking:
    primaryRegion: "westeurope"
    vnets:                                 # min 1
      - subscriptionName: "sub-team-a-dev"
        name: "vnet-team-a-dev"            # ^vnet-[a-z0-9-]+$
        addressSpace: "10.1.1.0/24"
        subnets:                           # min 1
          - name: "subnet-apps"            # ^subnet-[a-z0-9-]+$
            addressPrefix: "10.1.1.0/25"
```

Optional in the schema: `metadata.created/lastModified` (platform-set), `identity.administrativeUnit`
(auto-named), `tags.DataClassification` (Public/Internal/Confidential/Restricted; extra tags allowed),
`subscriptions[].billingAccountId` (default `default`) / `.parentManagementGroup` (from environment) /
`.description`, `networking.secondaryRegions`, `subnets[].purpose`. Regions: westeurope, eastus, eastus2,
centralus, northeurope, japaneast, australiaeast, uksouth, southeastasia, canadacentral, germanywestcentral.

**Removed by the redesign** (org policy or platform applies instead): `identity.conditionalAccess`,
`governance.compliance`, `security.*`, `observability.*`, `policies.*` (allowed/denied actions,
VM/core limits, regions), `networking.securityPolicies`, `vnets[].flowLogs`, `vnets[].peeringToHub`
(always on), `automation.*` (provisioning method, scheduled tasks, self-healing), `dependencies`
(auto-detected), `resources.*` and `subnets[].serviceEndpoints/privateEndpointEnabled` (workload
concerns, moved to workload definitions). Stated migration: removed fields in old definitions are
ignored — but see contradiction 6 below.

**Auto-applied by org policy (not team-customizable):**

| Area | Applied |
|---|---|
| Identity | Administrative Unit (auto-named), Entra groups, RBAC assignments, org-wide Conditional Access, MFA |
| Governance | Tags CostCenter/Team/Environment/ManagedBy; compliance defaults (SOC2, GDPR); budget alerts 80%/100%; cost allocation via tags |
| Networking | Peering to regional hub, org NSG rules, Azure Firewall rules, Private DNS zones, flow logs, Network Watcher, secondary subnets "for data isolation" |
| Security | AES-256 at rest, TLS 1.2+, all org Azure Policies, Defender for Cloud, threat detection, JIT VM access / PAM, Key Vault with CMK rotation |
| Observability | Central Log Analytics `law-platform-shared`, diagnostics on all resources, Application Insights, standard baselines and alert thresholds |
| Management | Management group by environment, MG-based subscription policies, per-environment quotas |
| Operations | Approval workflow (platform team), self-healing, scheduled maintenance, backup/DR |

## 3. Definition to Azure

| Field | Azure target | Notes |
|---|---|---|
| `metadata.name`, `owner.team`, `owner.email`, `displayName` | Subscription tags `LandingZoneName`, `Team`, `OwnerEmail` (+ `ManagedBy: IDP`); display name | |
| `identity.domain` | Entra ID custom domain | DNS TXT verification (manual, see Phase 0) |
| `identity.administrativeUnit` | Entra Administrative Unit `au-<team>` | scoped identity admins |
| `accessGroups[]` | Entra security group + RBAC assignment at subscription scope | conflicts with A3 RG-scoped model |
| `governance.tags.*` | Resource tags, enforced on all resources via Policy (Append/Modify) | CostCenter/Team drive cost allocation; Environment drives env-specific policy |
| `governance.budget.monthlyLimitUsd` | Cost Management budget at subscription scope | alerts per section 5 |
| `subscriptions[].name/displayName` | Azure subscription | created via API after billing linkage |
| `subscriptions[].environment` | `Environment` tag + management group placement | development/test → NonProduction, production → Production |
| `primaryRegion` / `secondaryRegions` | Default location / cross-region DR replication | redesign: org decides DR |
| `vnets[]` | VNet in the named subscription, peered to `vnet-hub-<region>` (gateway transit, forwarded traffic on) | |
| `subnets[]` | Subnet + NSG `nsg-<subnet>` | default rules: in 443 allow / deny rest; out 443, UDP 53 allow / deny rest |

Example instantiation for one dev subscription: under NonProduction, `sub-team-a-dev` with
`rg-team-a-core` (VNet, subnets, NSGs, peering, Network Watcher, Log Analytics) and
`rg-team-a-security` (Key Vault `kv-team-a-dev` with CMK `team-a-cmk` rotated every 90 days,
private endpoint `pe-keyvault`); policy assignments; Entra domain, AU, groups, Conditional Access;
budget. About 15–20 Azure resources, estimated 15–30 min fully automated. Pulumi state: one stack
per landing-zone subscription (e.g. `lz-team-a-dev`) in the Phase 0 backend; only `sp-just-deliver-idp`
reads/writes it and appears as actor in the Activity Log. Platform ops (if built): Automation account
`aa-platform-idp` in `sub-platform-management`.

## 4. Identity and tenant design

- Single Entra ID tenant. Entra = who (tenant-scoped); RBAC = what/where (subscription/RG-scoped).
- Per-zone isolation inside one tenant: Administrative Units (scoped admins), RBAC scoping,
  directory enumeration restrictions (requires hardening), Conditional Access, multiple verified
  custom domains (need not be subdomains; DNS ownership must be proven). `john@zone-a.com` and
  `john@zone-b.com` are distinct identities.
- Multiple tenants only for regulated/sovereign environments, M&A isolation, MSP-style separation,
  or when a shared Global Admin is unacceptable. China is a separate sovereign cloud anyway
  ([network.md](network.md#china-region)).

## 5. Controls

| Control | Mechanism / values |
|---|---|
| Cost | Mandatory cost tags; budgets with alerts; VM size limits; auto-shutdown of non-prod VMs; resource expiry |
| Security | Policy Deny of non-compliant resources; Defender for Cloud; no public IPs on sensitive resources; JIT via PIM; MFA/Conditional Access |
| Resource limits | Allowed resource types, allowed regions, max VM sizes/counts, subscription quotas |
| Network | Inbound/outbound rules, blocked destinations (prod networks, malicious IPs), private endpoint enforcement, DNS/routing |

Per-management-group values (illustrative, not decided); GDPR retention min 90 days for storage and Log Analytics; tag policy effect Append:

| | NonProduction | Production |
|---|---|---|
| Budget limit per subscription | $10,000/month | $100,000/month |
| VM size limit | up to 8 cores | up to 16 cores |
| Backup retention | 7 days | 30 days |
| Encryption | platform default | customer-managed keys mandatory |

**Policy effects:** Deny (hard requirements), Audit (gradual rollout), Append (tags), Modify (remediate
existing), DeployIfNotExists (diagnostics, auto-tagging). **Inheritance:** MG → subscription → RG → resource; no lower-level override.

## 6. Developer landing zone configuration

The landing zone is the guardrail, not the approval process. Golden rule: recoverable and
contained in the dev zone → allow; could affect prod, cost significantly, or expose data → block.

| Block | Allow |
|---|---|
| Public exposure of sensitive data; connecting to prod; real customer/PII data; backdoors into other zones; disabling audit logging; cost abuse (crypto mining, oversized resources) | Unusual resource types; misconfiguring own env; overspend with alerts; breaking own env; experimental architectures |

- Access (original proposal, superseded by A3/B10): Contributor on subscription, User Access Admin
  scoped to own RG, Key Vault Secrets Officer. Cannot modify subscription policies, access other
  teams' RGs, modify hub/firewall, or reach prod networks.
- Network: dev spoke VNet with pre-configured subnets; outbound internet allowed and logged via hub
  firewall; inbound internet denied by default; production connection hard-blocked.
- Policy: Hard Deny — public IPs on DBs, unencrypted storage, VM sizes above threshold, prod network
  peering. Audit — unused resources, non-standard SKUs, untagged resources. Auto-remediate —
  mandatory tags, diagnostics, Defender on new VMs.
- Cost: budget $X/month; 50% alert to team lead, 80% to platform team, 100% to everyone (no
  auto-stop); VM limit e.g. max 8 cores (prevents GPU abuse); auto-shutdown 20:00, restart 08:00
  (overridable); resource expiry auto-delete after 120 days unless renewed (TTL still open, B6).

## 7. Validation

- Schema: JSON Schema draft-07 at `definitions/landing-zone-definition.json`
  (`$id` `https://just-deliver.litmos.com/schemas/landing-zone-definition.schema.json`);
  annotated template at `definitions/landing-zone-definition.yaml`. `additionalProperties: false` except tags.
- Validates: required fields, types, formats (email, hostname, date-time), naming patterns, CIDR
  pattern, enums (environment, region, role, data classification), `CostCenter` = `CC-[0-9]+`,
  budget 100–1,000,000, min-one constraints. Not covered by schema: subnet-within-VNet, CIDR
  overlap, name uniqueness across teams, quota checks.
- Recommended path: the IDP backend (C#) validates YAML against the schema before any merge or
  provisioning and rejects with path + message, e.g. `POST /api/landing-zones/validate`
  → `{ "valid": false, "errors": [{ "path": "metadata.name", "message": "..." }] }`.
  Same schema runs in CI on PRs (`ajv validate -s <schema> -d 'definitions/lz-*.yaml'`) and in the
  editor via `yaml.schemas` mapping to `definitions/lz-*.yaml`.
- Maintenance: test against valid/invalid fixtures, re-validate existing definitions, bump `$id` to
  `...schema.v2.json` on breaking change, keep a changelog (evolution: H50). Per F41 this is UX; Azure Policy is the control.

## 8. Workflow: definition to infrastructure

1. Definition authored (by team per original design; by platform team under A3) and submitted via IDP API/UI or Git.
2. IDP validates: syntax, schema, naming, quotas, approved regions, duplicate names.
3. Approval: platform team reviews, or auto-approve if compliant (approval model open, F37).
4. IDP merges with org policy layer (two-layer model).
5. Pulumi Automation API job run by a background worker: subscriptions, Entra groups, VNets/NSGs/peering, Key Vault, policies, monitoring, budgets.
6. Requester notified with access details; workloads then deployed via workload definitions.

Updates: IDP diffs definitions and applies only the delta, keeping history for audit; drift is
detected and alerted/remediated. Substrate versioning and upgrade gating: B7.

## 9. Demo setup cost estimate

Minimal demo: 1 Entra tenant (free), 2 custom domains, 2 AUs, 2 subscriptions (one per zone);
per zone a VNet, NSG, Key Vault, Storage Account, Log Analytics.

| Scenario | Monthly |
|---|---|
| No VMs, no custom domains | ~$0–5 |
| Custom domains + Entra ID P1 | ~$26 |
| VMs + custom domains + P1 | ~$40–50 |
| Azure free account credits ($200 for 30 days) | $0 |

Entra licensing: Free $0 (users, groups, AUs, custom domains, basic MFA); P1 ~$6/user (Conditional
Access, SSPR); P2 ~$9/user (PIM, Identity Protection, Access Reviews). Conditional Access as
auto-applied above therefore requires at least P1; JIT approval via PIM requires P2.

## 10. Known contradictions in the source material

1. Authorship: teams submit the definition in the schema/workflow; A3 makes the landing zone
   platform-owned and read-only to teams. Address space is platform-owned under A3 but team-supplied in the schema.
2. RBAC: schema allows `Owner`/`UserAccessAdministrator` at subscription scope and examples give
   devops `Owner`; network design says team engineers are Owner on their subscription. A3/B10:
   Contributor + constrained RBAC Administrator on team RGs, Reader on platform RG.
3. Enforcement: original docs have the IDP "validate against zone policies"; F41 says that is UX only.
4. "One LZ per team" is listed as an anti-pattern, yet `lz-team-a` is one LZ per team (B5).
5. Dev zones looser than prod (section 6, per-MG limits) vs A4 candidate of an identical policy set.
6. Redesign says removed fields are ignored; the schema's `additionalProperties: false` rejects them.
   The schema also still carries `administrativeUnit` and `secondaryRegions` the redesign removed.
7. Budget alerts: 80/100% (redesign) vs 50/80/100% (developer config, original example).
   Budget ceiling: schema 1,000,000 vs NonProduction $10,000 policy.
8. `governance.tags.Environment` is one value per landing zone but a definition can hold dev/test/prod subscriptions.
9. Template says "Pulumi/TypeScript"; the project uses the C# Automation API.
10. Older two-layer text: "all traffic routes through Azure Application Gateway"; network design uses Front Door + hub Firewall.
11. Validation docs reference `landing-zone-definition.schema.json`/`.schema.yaml` and
    `landing-zone-example.yaml`; actual files are `landing-zone-definition.json` and `.yaml`, no example file.
12. A3 points to a read/write matrix in `decisions-and-approach.md` that does not exist there.
