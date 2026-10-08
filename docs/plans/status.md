# Work status

> Single source of truth for where the MVP stands. **Read this first every session; update it before
> the session ends** (lead only). Plan and slice briefs: [mvp.md](mvp.md).

## Now

- **Next slice:** S01 — Workload schema v2
- **In progress:** —
- **Blocked:** —

## Slices

States: `todo` · `in-progress` · `in-review` · `changes-requested` · `done` · `blocked`

| ID | Slice | State | Branch / PR | Notes |
|---|---|---|---|---|
| S00 | Repo baseline + team setup | done | `main` | Strict build, verify gate, hook, CI, agents, standards, sandbox |
| S01 | Workload schema v2 | todo | | |
| S02 | Catalog formats + loader | todo | | |
| S03 | Environment descriptor | todo | | |
| S04 | Expression evaluator | todo | | |
| S05 | Matching + expansion | todo | | |
| S06 | Policies + provenance | todo | | |
| S07 | Graph builder | todo | | |
| S08 | CLI skeleton | todo | | |
| S09 | Template library | todo | | |
| S10 | Provider fixes | todo | | |
| S11 | Orchestrator (infra nodes) | todo | | |
| S12 | Substrate from data | todo | | |
| S13 | Second workload | todo | | |
| S14 | Sample app | todo | | |
| S15 | Runtime mapping + container-app | todo | | |
| S16 | Deploy steps | todo | | |
| S17 | jd deploy + record | todo | | |
| S18 | E2E run and docs | todo | | |

## Environment

| Item | Value |
|---|---|
| Subscription | `MVPLandingZone` (`ca89cbcc-e368-4e81-9e80-6686b4d9f3b9`), Pay-As-You-Go, guardrails applied |
| Credentials | `~/.just-deliver/ca89cbcc-e368-4e81-9e80-6686b4d9f3b9.env` — secret expires **2026-11-07** (renew: `tools/sandbox/sandbox.sh apply … --rotate-secret`, needs the user as Owner) |
| Region | `southeastasia` (`JD_REGION`) |
| Live Azure resources | none |

## Session log

Newest first. One entry per session: what moved, decisions taken, anything the next session must know.

- **2026-10-08** — Repo baseline and team setup (S00). Docs reorganised; sandbox subscription
  prepared and guardrails verified; strict build, `tools/verify.sh`, commit hook, CI; developer
  (Opus 5.5) and reviewer (Fable 5.1) agents; standards and review checklist; MVP sliced into S01–S18.
