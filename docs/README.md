# just-deliver docs

Internal Developer Platform for Azure: teams declare what their workload needs, the platform
provisions and deploys it, IT Ops moves from executor to gatekeeper.

## Status legend

| Label | Meaning |
|---|---|
| **Proposed** | A conclusion from investigation, written as an ADR, awaiting review. Nothing is accepted yet. |
| **Design reference** | Architecture thinking; useful, not binding. |
| **Open** | An unresolved question, tracked in [open-questions.md](open-questions.md) with a stable ID. |

## Reading order

1. [context.md](context.md) — why the platform exists, stakeholders, B/C → D path
2. [decisions/](decisions/README.md) — proposed decisions (ADRs 0001–0016)
3. [open-questions.md](open-questions.md) — what is still undecided (IDs A1 … H58)
4. Use cases
   - [viedoc-daybreak.md](use-cases/viedoc-daybreak.md) — a real regulated suite's release process, and how the design fits it
5. Architecture
   - [workload-definition.md](architecture/workload-definition.md) — what teams declare
   - [provisioning.md](architecture/provisioning.md) — mappings, policies, Pulumi operation
   - [resolver.md](architecture/resolver.md) — proposed resolver design (C52)
   - [release.md](architecture/release.md) — the release manifest and release set: pinned workloads in deploy order (C53)
   - [usage.md](usage.md) — using the `jd` CLI: commands and exit codes
   - [workload_deployment_flow.html](architecture/workload_deployment_flow.html) — deployment flow diagram ([overview svg](architecture/workload_deployment_flow-overview.svg), [steps svg](architecture/workload_deployment_flow-steps.svg))
   - [landing-zone.md](architecture/landing-zone.md) — landing zone ownership and definition
   - [network.md](architecture/network.md) — hub-and-spoke network design
6. Plans
   - [status.md](plans/status.md) — current work status (read first each session)
   - [mvp.md](plans/mvp.md) — MVP scope and slices
   - [phase-0-bootstrap.md](plans/phase-0-bootstrap.md) — manual Azure bootstrap runbook
7. Engineering
   - [standards.md](engineering/standards.md) — binding rules for all code
   - [review-checklist.md](engineering/review-checklist.md) — what every review checks
   - Sandbox subscription setup: [tools/sandbox](../tools/sandbox/README.md)

## Conventions

- A decision lives in exactly one ADR; other docs link to it rather than restate it.
- Open questions keep their IDs forever; when one settles, it gets an ADR and its entry becomes a pointer.
- Keep docs short. Background reasoning belongs in the ADR's Context, not in a separate essay.
