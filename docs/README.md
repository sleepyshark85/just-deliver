# just-deliver docs

Internal Developer Platform for Azure: teams declare what their workload needs, the platform provisions and deploys it, IT Ops
moves from executor to gatekeeper. Each doc owns one thing; a fact lives in its owner and other docs link to it.

## Why and where

- [context.md](context.md) — the problem, stakeholders, goals and design principles
- [plans/roadmap.md](plans/roadmap.md) — the north star and the phases toward it
- [open-questions.md](open-questions.md) — what is still undecided
- [use-cases/viedoc-daybreak.md](use-cases/viedoc-daybreak.md) — a regulated customer's release process and how jd fits it

## How it works today

- [architecture/model.md](architecture/model.md) — the model: workloads, requirements, components, shared resources, environments, policies, grants, the resolved graph
- [architecture/documents.md](architecture/documents.md) — documents, schemas and `x-jd` keywords, the binder, errors, the literal guard
- [architecture/provisioning.md](architecture/provisioning.md) — the orchestrator, the release flow, Pulumi, state
- [architecture/release.md](architecture/release.md) — the release manifest and release set
- [architecture/release-record.md](architecture/release-record.md) — the release record, IQ/OQ qualification
- [architecture/approvals.md](architecture/approvals.md) — approval policy, the gate, "qualified" and "approved"
- [architecture/application.md](architecture/application.md) — use cases, the thin CLI rules R1–R3, `Connections`

## Using jd

- [usage.md](usage.md) — every command: arguments, output, refusals, exit codes; what jd can do today
- [walkthrough.md](walkthrough.md) — hands-on scenarios with the kit in `samples/walkthrough`
- [tools/sandbox](../tools/sandbox/README.md) — preparing an Azure subscription with free-tier guardrails

## Decisions

- [decisions/](decisions/README.md) — the ADRs and their status

## Engineering

- [engineering/conventions.md](engineering/conventions.md) — how the team works
- [engineering/standards.md](engineering/standards.md) — every binding code rule, incl. tests and design invariants
- [engineering/csharp.md](engineering/csharp.md) — C# idioms
- [engineering/review-checklist.md](engineering/review-checklist.md) — what every review checks, and the verdict format

## Plans

- [plans/status.md](plans/status.md) — where the work stands (read first each session)
- [plans/mvp.md](plans/mvp.md) — the MVP's scope and success criteria (done)
- [plans/e2e-evidence.md](plans/e2e-evidence.md) — which tests prove each MVP success criterion
- [architecture/multi-cloud.md](architecture/multi-cloud.md) — phase 1.7's design note: the platform in the model, GCP (to build)
