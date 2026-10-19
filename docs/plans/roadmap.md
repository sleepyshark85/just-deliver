# Roadmap: phases toward the north star

> Every piece of work serves a phase goal, and every phase goal serves the north star. A slice that serves no phase goal needs the user's
> go-ahead before it starts. Current phase and slices: [status.md](status.md). Vision as the user stated it: [context.md](../context.md)
> § "North star".

## North star

**A team builds its application by composing components, whether a cloud provider supplies them or another team does. Every component
has a declared contract. The platform resolves, grants, deploys, qualifies and promotes the whole graph safely, so no team needs to know
how a component is built, only its contract.**

An application always runs on compute, so the application and its compute are one unit, a **workload**. A workload is a component like a
database or a service bus. With many workloads, a workload uses another workload the same way it uses a base component.

## The north star as concrete ideas

| # | Idea | What it means in practice | Today (after the MVP) |
|---|---|---|---|
| N1 | **One component shape** | Base components and workloads share one definition shape: a contract, the parts per scope, and what it `uses` | Types, mappings and substrate are separate; a workload is a different file kind |
| N2 | **Contracts, versioned** | Every component publishes named exports, with a version. Consumers bind to the contract, never to the implementation; a breaking change is caught before release | A type's `exports`, unversioned; base components only |
| N3 | **Explicit scopes** | Each part of a component is declared at one scope: shared, environment or workload instance. Shared resources are named and defined once | Implicit; the substrate mappings decide on their own |
| N4 | **Three ways to require** | **Create**: my own instance (a database container). **Bind**: an instance another owner runs (another team's API). **Attach**: my part on someone else's resource (my subscription on a producer's topic) | Create only |
| N5 | **Access by offer** | A provider offers access levels (invoke, publish, read), possibly only to listed consumers. A consumer requests one. A grant exists only where it is offered. This generalises D19 and `grantable` | `grantable` list on the environment; cross-workload references forbidden |
| N6 | **Resolve against what is deployed** | A consumer resolves against the exports a provider actually deployed in that environment, kept in a per-environment registry | Substrate values only, through the environment descriptor |
| N7 | **Graph-aware release and promotion** | Ordering across teams. Promotion needs compatible providers already on the target. Impact analysis: who depends on me, and what a change breaks | Ordering within one release set; promotion of one set |
| N8 | **Lifecycle safety** | A provider with consumers cannot be removed; contracts are deprecated before removal; destructive changes are gated | Records and approvals for one set |
| N9 | **Discoverable and self-serve** | One place lists every component (base and workload) with its contract, offers and owners; teams find and use components without asking | Read the catalog files |
| N10 | **Cheap for the platform team** | A new base component is scaffolded and checked offline in minutes; a standard base catalog is extended, not written from scratch | Hand-written files in five directories |

## Phases

Each phase ends in something usable and leaves the previous phases' guarantees intact (the MVP's success criteria stay green).

| Phase | Goal | Serves | Exit criteria |
|---|---|---|---|
| **0 — MVP** (done, plus S23 "nothing set on the machine" added by the user 2026-10-18; then verified by hand) | Prove the engine on Azure: resolve, deploy dark, probe, shift, qualify, approve, promote | foundation | MVP success criteria 1–9 ([e2e-evidence.md](e2e-evidence.md)); the user's manual run with [walkthrough.md](../walkthrough.md) |
| **1 — Components, base** | The component is the catalog's unit for base components: one definition per component, explicit shared resources, substrate derived from the components an environment offers | N1, N3, N5 (grantable from `uses`) | The same Azure resources for the same inputs (resolved graphs and stack names unchanged), except the one-time move of monitoring into a requirement, which the upgrade gate checks (user, 2026-10-18); the catalog laid out per component; E2E green. Design: S21 ([components.md](../architecture/components.md)) |
| **1.5 — The code knows no file structure** (added by the user 2026-10-19 after the manual MVP run) | The engine knows a closed list of generic concepts; every file's structure and the rules on it are annotated JSON Schemas (data) | N1, N10 | No field literal in production code beyond the note's exceptions (the literal guard); the same Azure resources and written documents for the same inputs; every D19 boundary keeps its tests; E2E green. Design: S24 ([documents.md](../architecture/documents.md)) |
| **2 — Prove the model, platform ergonomics** | A second and third base component prove the shape is general, and the platform team can add one cheaply | N2 (contract version), N9, N10 | A messaging component (Service Bus, free-tier safe) and a secrets component (Key Vault) added as components only, no C# change and no schema change outside the component's own kinds; `jd catalog new`/`check`/`show`; a breaking change to a contract that deployed workloads use is refused |
| **3 — Workloads as providers** | A workload declares a contract and offers, and its deployed exports are recorded per environment | N1 (workloads), N2, N5, N6 | The sample app publishes its URL and an `invoke` offer; the environment registry holds what is deployed; nothing consumes it yet; D19 unchanged |
| **4 — Workloads as consumers** | A workload requires another workload (bind), gets only offered access, and resolves against deployed values; release and promotion respect it | N4 (bind), N5, N6, N7 | The sample worker binds to the sample app (`requires: just-deliver-sample-app`), gets the `invoke` grant only because it is offered, and its promotion is refused until a compatible app is on `stage`; every forbidden path is a test |
| **5 — Lifecycle at scale** | Many workloads stay safe to change | N4 (attach), N7, N8, N9 | Impact analysis (`jd who-uses`); removal blocked while consumers exist; contract deprecation; the attach pattern (topic subscription); a component directory for teams |

Not on this track: the platform as a hosted service (API, UI; ADR 0001), the audit store (F39), the B/C → D transition stages, secrets
injection and multi-subscription landing zones. Each must still name the north-star idea or the organisation goal ([context.md](../context.md)
§ "Goals") it serves. The user decides where they go relative to these phases.

## Rules for work

1. **Every brief names its phase and the phase goal it advances**, with the ideas (N#) it serves. The reviewer checks that the slice stays
   inside that goal; work outside it is a finding.
2. **A phase starts with a design note** that maps its goal to slices and exit criteria. The note is reviewed, and the user decides on its
   open choices.
3. **A phase ends** when its exit criteria are evidenced in status.md, like the MVP's. Then the next phase's design note starts.
4. **No phase makes a later one harder.** A design note states how it keeps the later phases' ideas open, e.g. phase 1 must not make
   bind (N4) or offers (N5) harder.
5. **Changing this roadmap** (a phase's goal, the order, the north star) is the user's decision. The lead may propose changes.
