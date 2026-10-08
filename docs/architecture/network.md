# Network Architecture

> Status: design reference — not yet reviewed as decisions. Proposed decisions live in [decisions/](../decisions/).

Multi-region, multi-team Azure network spanning EU, US, Japan and China. Related:
[landing-zone.md](landing-zone.md), [../plans/phase-0-bootstrap.md](../plans/phase-0-bootstrap.md).
Under A3 ([0008](../decisions/0008-enforcement-in-landing-zone.md)) address space, peering, routing
and DNS are platform-owned; subnets and NSGs inside a team's allocation are team-owned.

## Topology: hub-and-spoke per region, joined by Azure Virtual WAN

```
Azure Virtual WAN (Global, Standard tier)
├── EU Hub (West Europe)      spokes: Team A dev/test, Team B dev/test, Shared prod
├── US Hub (East US)          spokes: Team A dev/test, Shared prod
├── Japan Hub (Japan East)    ...
└── China Hub (China East 2)  separate sovereign cloud (21Vianet), site-to-site VPN
```

## Components

| Component | Role |
|---|---|
| Azure Virtual WAN | Global hub-to-hub routing; global routing table (no manual inter-region routes); redundancy; terminates on-prem VPN/ExpressRoute; central monitoring of inter-region traffic |
| Azure Firewall (per hub) | Inspects all spoke-to-spoke traffic; cross-team isolation; blocks unauthorized outbound internet |
| VPN / ExpressRoute Gateway (per hub) | On-prem connectivity: ExpressRoute for production, VPN for dev/test |
| Azure Bastion (per hub) | RDP/SSH to VMs without public IPs |
| DNS Private Resolver (per hub) | Private name resolution across spokes and regions |
| Log Analytics / Network Watcher | Central logging, flow logs, connectivity diagnostics |
| Spoke VNets | One per team per environment |
| VNet peering (hub to spoke) | Private backbone; gateway transit so spokes use the hub gateway; no direct spoke-to-spoke, cross-spoke traffic goes via hub firewall |
| NSGs | Subnet-level stateful filters in spokes |
| Azure Front Door | Global entry: nearest healthy region, automatic regional failover, TLS termination, WAF, static caching |

Spokes: `spoke-teamA-dev`, `spoke-teamA-test`, `spoke-teamB-dev`, `spoke-teamB-test`,
`spoke-shared-prod` (production with all teams' components integrated).

Example NSG rules: allow 443 inbound from hub; deny all inbound from other spokes (no lateral
movement); allow 5432 within spoke only; deny all outbound internet (force via hub firewall).
Note: the developer landing zone config allows outbound internet from dev spokes, logged via the
hub firewall ([landing-zone.md](landing-zone.md#6-developer-landing-zone-configuration)).

Naming used in the landing-zone mapping: `vnet-hub-<region>`, `azfw-<region>`, `vpn-gw-<region>`,
`hub-<region>`, team VNets `vnet-<team>-<env>` peered to `vnet-hub-<region>`.

## IP address plan

Non-overlapping blocks, allocated upfront (changing later is painful):

```
10.0.0.0/8         organization
├── 10.1.0.0/16    EU
│   ├── 10.1.0.0/24    Hub
│   ├── 10.1.1.0/24    Spoke - Team A Dev
│   ├── 10.1.2.0/24    Spoke - Team B Dev
│   └── 10.1.10.0/24   Spoke - Prod
├── 10.2.0.0/16    US
├── 10.3.0.0/16    Japan
└── 10.4.0.0/16    China
```

Inconsistency: the landing-zone mapping investigation used `vnet-hub-westeurope` = 10.0.0.0/24 and
`vnet-hub-eastus` = 10.2.0.0/24 instead of 10.1.0.0/24 for the EU hub. Open constraint (B5): a
Container Apps Environment needs a dedicated subnet of a minimum size per environment type, so the
per-environment allocation must account for it before any space is handed out; a /24 per spoke may
be too small.

## China region

- Sovereign cloud operated by 21Vianet; cannot join global Virtual WAN.
- Separate Azure China account (`portal.azure.cn`); separate tenant in practice.
- Connected to the global network by manually configured site-to-site VPN.
- Data residency: data cannot leave China automatically. Some services missing or different.
- Production workloads must comply with MLPS 2.0.
- `chinaeast2` is not in the landing-zone schema's allowed regions.

## Subscription and governance hierarchy

```
Root Management Group
└── Org Management Group
    ├── Production       └── Shared Prod Subscription
    └── NonProduction    ├── Team A Subscription
                         └── Team B Subscription ...
```

Phase 0 adds `Platform` (connectivity, identity, management subscriptions) and `Sandbox` under the
Org group; connectivity resources (vWAN, hubs, firewall, DNS, gateways) live in
`sub-platform-connectivity`.

| Tool | Use |
|---|---|
| Management groups | Assign Azure Policy once for all subscriptions below |
| Subscription per team | Hard boundary for billing, quota, RBAC |
| Azure Policy | No public IPs in prod, required tags, approved regions only |
| RBAC | Network team = Network Contributor on hub VNets; security team = Reader + Security Admin across all; team engineers = Owner on own subscription only (superseded by A3/B10: Contributor + constrained RBAC Administrator on team RGs) |

## Traffic flow

```
User -> Azure Front Door (WAF, TLS, global failover)
     -> Regional Hub VNet: Azure Firewall (inspect) | Bastion (VM access) | DNS Resolver
     -> VNet peering -> spoke-teamA-dev | spoke-teamA-test | spoke-teamB-dev | spoke-shared-prod
Hubs EU <-> US <-> Japan via Virtual WAN; China hub via site-to-site VPN
```

Most work is at L3 (routing, peering, UDRs, BGP); L4 for NSGs and load balancing; L7 for Front Door.
L1/L2 are Microsoft's except where ExpressRoute is used.

## Next steps (from the investigation)

1. Finalize subscription model — one subscription per team (open, B5).
2. Lock in all CIDR ranges upfront.
3. Deploy Virtual WAN, Standard tier (needed for multi-hub).
4. Use Azure Landing Zone accelerators as reference (Microsoft provides Bicep/Terraform; this
   project provisions with Pulumi, so they are reference only).
5. Handle China separately; engage Microsoft/21Vianet early for compliance and connectivity.
6. Codify Azure Policy rules for what teams can and cannot do (split vs platform: F41).
