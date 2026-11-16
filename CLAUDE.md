# just-deliver

Internal Developer Platform for Azure. Start every session with [docs/README.md](docs/README.md) (the index of every doc), then:

- [Work status](docs/plans/status.md) — **where we are; read first, update last**
- [Roadmap](docs/plans/roadmap.md) — the north star and its phases; **every slice serves a phase goal**
- [Conventions](docs/engineering/conventions.md) (how the team works), [engineering standards](docs/engineering/standards.md)
  (every code rule), [C# conventions](docs/engineering/csharp.md) (C# idioms) and [review checklist](docs/engineering/review-checklist.md)
  (the review's order and verdict) — binding for all work
- [Decisions](docs/decisions/README.md) — the ADRs and their status; only the user changes a status
- [Open questions](docs/open-questions.md), [architecture](docs/architecture/), [MVP plan](docs/plans/mvp.md) (done)

## Team

| Role | Who | Model |
|---|---|---|
| Lead | the main session | Opus 5.5 |
| Developer | `developer` agent ([.claude/agents/developer.md](.claude/agents/developer.md)) — slices, design-level findings | Opus 5.5 |
| Fixer | `fixer` agent ([.claude/agents/fixer.md](.claude/agents/fixer.md)) — small findings with an obvious fix | Sonnet 5.5 |
| Reviewer | `reviewer` agent ([.claude/agents/reviewer.md](.claude/agents/reviewer.md)) — one per slice, kept for its rounds | Opus 5.5 |

How the team works together: [conventions](docs/engineering/conventions.md).

## Lead workflow

1. **Session start:** read `docs/plans/status.md`. Resume the slice in progress, or take the next `todo` slice.
2. **Brief:** expand the slice (from `mvp.md` or the phase's design note) into a brief
   ([conventions](docs/engineering/conventions.md#briefs)). Mark the slice `in-progress` in status.md.
3. **Develop:** dispatch `developer` with the brief. Run the Azure gate after the hand-back if its rule says so
   ([gate](docs/engineering/conventions.md#azure-gate)).
4. **Review:** dispatch a new `reviewer` with only the brief and the branch name
   ([reviews](docs/engineering/conventions.md#reviews)).
5. **Iterate:** on CHANGES REQUIRED, send the blocking findings to the `fixer` or the `developer` (same branch; resume or fresh:
   [context](docs/engineering/conventions.md#reports-and-context)), then send the same reviewer the new head sha. Arbitrate
   disputes; after three rounds without APPROVE, mark the slice `blocked` and escalate ([reviews](docs/engineering/conventions.md#reviews)).
6. **Merge:** on APPROVE, put the status update on the slice branch, push it, open a PR to `main` and wait for CI
   (`gh pr checks <n> --watch`). `main` is protected on GitHub: changes only via PR, the `verify` check must pass
   with the branch up to date, no force-push, admins included. Merge with `gh pr merge --squash`; delete the branch only
   after `gh pr view --json state` reports `MERGED` (deleting the remote branch of an open PR closes it). A local
   pre-push hook also rejects pushes to `main` (enable per clone: `git config core.hooksPath .githooks`).
7. **Record:** update status.md — slice state, review rounds, PR link, notes — before the next slice.
8. **Session end:** update status.md "Now" and add a session-log entry, even mid-slice.

Work one slice at a time. Do not involve the user unless blocked ([escalation](docs/engineering/conventions.md#escalation)).

## Non-negotiables

- Nothing hard-coded in code ([standards §1](docs/engineering/standards.md#1-nothing-hard-coded)).
- Explicit over implicit: a workload gets only what it declares; guardrails require, never silently add
  ([standards §4](docs/engineering/standards.md#4-design-invariants)).
- Simplification over complication: the simplest approach, fewest concepts, no unnecessary code; clean architecture and
  separation of concerns
  ([standards §2–3](docs/engineering/standards.md#2-simplest-thing-that-works)).
- Design principles 6–14 in [context.md](docs/context.md#design-principles) (from the user) bind every design.
- Every Azure resource inside the free tier; test resources torn down after use ([standards §6](docs/engineering/standards.md#6-azure-and-the-free-tier)).
- Small slices, one PR each; `tools/verify.sh` green on every commit ([standards §7](docs/engineering/standards.md#7-git-and-prs)).
