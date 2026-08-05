# Azure Multi-Region Network Architecture Summary

## Overview

This document summarizes the network architecture designed for a multi-region, multi-team Azure deployment spanning **EU, US, Japan, and China**.

---

## 1. Network Topology

### Chosen Pattern: Hub-and-Spoke with Azure Virtual WAN

The recommended architecture uses a **Hub-and-Spoke topology** per region, connected globally via **Azure Virtual WAN**.

```
Azure Virtual WAN (Global)
├── EU Hub (West Europe)
│   ├── Spoke: Team A – Dev/Test
│   ├── Spoke: Team B – Dev/Test
│   └── Spoke: Shared – Prod
├── US Hub (East US)
│   ├── Spoke: Team A – Dev/Test
│   └── Spoke: Shared – Prod
├── Japan Hub (Japan East)
│   └── ...
└── China Hub (China East 2)  ← Separate sovereign cloud (21Vianet)
    └── ...
```

---

## 2. Architecture Components

### 2.1 Azure Virtual WAN (Global Backbone)
- Routes traffic between all regional hubs automatically
- Maintains a global routing table — no manual route configuration between regions
- Provides built-in redundancy with multiple underlying paths
- Manages VPN and ExpressRoute connections from on-premises offices
- Centralizes monitoring of all inter-region traffic

### 2.2 Regional Hub VNet (Per Region)
A central VNet per region hosting all shared infrastructure.

| Component | Responsibility |
|---|---|
| **Azure Firewall** | Inspects all traffic between spokes; enforces cross-team isolation; blocks unauthorized outbound internet |
| **VPN / ExpressRoute Gateway** | Connects on-premises offices into Azure; ExpressRoute for production, VPN for dev/test |
| **Azure Bastion** | Secure RDP/SSH to VMs via the portal; no public IPs required |
| **DNS Private Resolver** | Consistent private name resolution across all spokes and regions |
| **Log Analytics / Network Watcher** | Centralized logging, flow logs, connectivity diagnostics |

### 2.3 Spoke VNets (Per Team × Per Environment)
Isolated virtual networks — one per team per environment.

| Spoke | Purpose |
|---|---|
| `spoke-teamA-dev` | Team A development environment |
| `spoke-teamA-test` | Team A integration/QA testing |
| `spoke-teamB-dev` | Team B development environment |
| `spoke-teamB-test` | Team B integration/QA testing |
| `spoke-shared-prod` | Production — all teams' components integrated |

- Connected to the regional hub via **VNet Peering**
- No direct spoke-to-spoke connectivity by default
- All cross-spoke traffic routes through the hub firewall

### 2.4 VNet Peering (Hub ↔ Spoke)
- Direct, low-latency link between spoke and hub
- Traffic stays on Microsoft's private backbone
- Configured with **gateway transit** so spokes use the hub's VPN/ExpressRoute gateway

### 2.5 Network Security Groups (NSGs)
Stateful packet filters at the subnet level within each spoke.

| Rule Example | Action |
|---|---|
| Allow port 443 inbound from hub | Permit HTTPS from shared services |
| Deny all inbound from other spokes | Prevent lateral movement |
| Allow port 5432 within spoke only | Database accessible only internally |
| Deny all outbound to internet | Force traffic through the hub firewall |

### 2.6 Azure Front Door (Global Traffic Entry)
- Routes end users to the nearest healthy region
- Automatic failover if a region goes down
- TLS termination and WAF (Web Application Firewall) at the edge
- Caches static content globally

### 2.7 China Region (Special Handling)
China is a **sovereign cloud** (operated by 21Vianet) and cannot be natively connected to global Azure Virtual WAN.

- Requires a **separate Azure China account** (`portal.azure.cn`)
- Connected to global network via **site-to-site VPN** (manual configuration)
- Data sovereignty laws — data cannot leave China automatically
- Some Azure services are unavailable or behave differently
- Must comply with **MLPS 2.0** (Chinese cybersecurity law) for production workloads

---

## 3. IP Address Plan

Non-overlapping CIDR blocks allocated per region:

```
10.0.0.0/8         (entire organization)
├── 10.1.0.0/16    EU
│   ├── 10.1.0.0/24    Hub
│   ├── 10.1.1.0/24    Spoke – Team A Dev
│   ├── 10.1.2.0/24    Spoke – Team B Dev
│   └── 10.1.10.0/24   Spoke – Prod
├── 10.2.0.0/16    US
├── 10.3.0.0/16    Japan
└── 10.4.0.0/16    China
```

---

## 4. Subscription & Governance Model

### Management Hierarchy

```
Root Management Group
└── Org Management Group
    ├── Production
    │   └── Shared Prod Subscription
    └── NonProduction
        ├── Team A Subscription
        ├── Team B Subscription
        └── ...
```

### Key Governance Tools

| Tool | Purpose |
|---|---|
| **Management Groups** | Apply Azure Policy across all subscriptions in one place |
| **Subscriptions (per team)** | Hard boundary for billing, quotas, and RBAC isolation |
| **Azure Policy** | Enforce guardrails — no public IPs in prod, required tags, approved regions only |
| **RBAC** | Team engineers = Owner on own subscription only; network team = Network Contributor on hub VNets; security team = Reader + Security Admin across all |

---

## 5. OSI Model Mapped to Azure Services

| Layer | Name | Azure Services |
|---|---|---|
| 7 | Application | App Gateway, Azure Front Door, API Management |
| 6 | Presentation | TLS termination at Load Balancer / App Gateway |
| 5 | Session | Entra ID token sessions, API session management |
| 4 | Transport | Azure Load Balancer, NSG port rules, Firewall DNAT |
| 3 | Network | VNet routing (UDR), VPN Gateway, ExpressRoute, VNet peering |
| 2 | Data Link | VNet switching fabric, ExpressRoute private peering, VXLAN overlays |
| 1 | Physical | Microsoft data center cabling, ExpressRoute physical circuits |

In this architecture:
- **L3** is where most of the work happens — VNet routing, peering, UDRs, BGP
- **L4** governs NSG rules and load balancing between team environments
- **L7** is where Azure Front Door routes traffic globally to the right regional endpoint
- **L1/L2** is Microsoft's responsibility, except where ExpressRoute is used

---

## 6. Traffic Flow Diagram

```
[User Globally]
      ↓
[Azure Front Door] ← WAF, TLS termination, global failover
      ↓
[Regional Hub VNet]
  ├─ Azure Firewall     ← inspects all traffic
  ├─ Azure Bastion      ← engineer VM access
  ├─ DNS Resolver       ← private name resolution
  └─ VNet Peering
        ├─ spoke-teamA-dev
        ├─ spoke-teamA-test
        ├─ spoke-teamB-dev
        └─ spoke-shared-prod
              ↕ (Azure Virtual WAN)
[US Hub] ──── [Japan Hub]
                    ↕ (Site-to-Site VPN)
              [China Hub] ← sovereign cloud
```

---

## 7. Recommended Next Steps

1. **Finalize subscription model** — confirm one subscription per team
2. **Lock in CIDR ranges** — allocate all blocks upfront; changing later is painful
3. **Deploy Azure Virtual WAN** — use Standard tier for multi-hub support
4. **Use Azure Landing Zone accelerators** — Microsoft provides Bicep/Terraform templates implementing this pattern
5. **Handle China separately** — engage Microsoft/21Vianet early for compliance and connectivity planning
6. **Deploy Terraform / Bicep templates** — infrastructure-as-code for repeatable, governed deployments
7. **Define Azure Policy rules** — codify what teams can and cannot do in their subscriptions

---

## 8. Azure Services Reference

| Service | Role in Architecture |
|---|---|
| Azure Virtual WAN | Global hub-to-hub routing backbone |
| Azure Firewall | Centralized traffic inspection and policy enforcement |
| VPN / ExpressRoute Gateway | On-premises and China connectivity |
| Azure Bastion | Secure VM access without public IPs |
| DNS Private Resolver | Private DNS across all VNets |
| Log Analytics Workspace | Centralized network and resource logging |
| Azure Front Door | Global load balancing, WAF, CDN |
| NSGs | Subnet-level packet filtering within spokes |
| VNet Peering | Hub-spoke connectivity |
| Management Groups | Policy governance hierarchy |
| Azure Policy | Automated guardrails across subscriptions |
| Microsoft Entra ID | Identity and RBAC across all subscriptions |
