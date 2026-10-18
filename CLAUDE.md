# just-deliver

Internal Developer Platform for Azure. Start every session with [docs/README.md](docs/README.md), then:

- [Work status](docs/plans/status.md) — **where we are; read first, update last**
- [Roadmap](docs/plans/roadmap.md) — the north star and its phases; **every slice serves a phase goal**
- [MVP plan](docs/plans/mvp.md) — scope and slices S01–S18
- [Conventions](docs/engineering/conventions.md) (how we work, the code quality bar), [engineering standards](docs/engineering/standards.md)
  and [review checklist](docs/engineering/review-checklist.md) — binding for all code
- [Decisions](docs/decisions/README.md) — ADRs; all **Proposed — pending review**; only the user changes their status
- [Open questions](docs/open-questions.md), [architecture](docs/architecture/)

## Team

| Role | Who | Model |
|---|---|---|
| Lead | the main session | Opus 5.5 |
| Developer | `developer` agent ([.claude/agents/developer.md](.claude/agents/developer.md)) — slices, design-level findings | Opus 5.5 |
| Fixer | `fixer` agent ([.claude/agents/fixer.md](.claude/agents/fixer.md)) — small findings with an obvious fix | Sonnet 5.5 |
| Reviewer | `reviewer` agent ([.claude/agents/reviewer.md](.claude/agents/reviewer.md)) — one per slice, kept for its rounds | Opus 5.5 |

How the team works together (independence, rounds, short reports, context cost): [conventions](docs/engineering/conventions.md#how-we-work).

## Lead workflow

1. **Session start:** read `docs/plans/status.md`. Resume the slice in progress, or take the next `todo` slice.
2. **Brief:** expand the slice (from `mvp.md` or the phase's design note) into a brief — **the phase and phase goal it serves
   ([roadmap](docs/plans/roadmap.md))**, goal, acceptance criteria, files in scope, out of scope, ADRs/docs that apply. Mark the slice `in-progress` in status.md.
3. **Develop:** dispatch `developer` with the brief. Run the Azure gate once, after the hand-back and before the review, only if
   what reaches Azure changed — the deployed-config snapshot or the direct-Azure code — and at each phase's end
   ([conventions](docs/engineering/conventions.md#how-we-work)). An unchanged snapshot is the refactor's proof.
4. **Review:** dispatch a new `reviewer` with **only the brief and the branch name** — never the developer's report.
5. **Iterate:** on CHANGES REQUIRED, send the blocking findings to the `fixer` (all small and obvious) or the
   `developer` (same branch; resume it only while under 150k context, else a fresh one). Then send the **same**
   reviewer the next round: the new head sha only. The lead arbitrates disputes using the standards. After 3 rounds
   without APPROVE, mark the slice `blocked` and escalate to the user.
6. **Merge:** on APPROVE, put the status update on the slice branch, push it, open a PR to `main` and wait for CI
   (`gh pr checks <n> --watch`). `main` is protected on GitHub: changes only via PR, the `verify` check must pass
   with the branch up to date, no force-push, admins included. Merge with `gh pr merge --squash`; delete the branch only
   after `gh pr view --json state` reports `MERGED` (deleting the remote branch of an open PR closes it). A local
   pre-push hook also rejects pushes to `main` (enable per clone: `git config core.hooksPath .githooks`).
7. **Record:** update status.md — slice state, PR link, notes — before the next slice.
8. **Session end:** update status.md "Now" and add a session-log entry, even mid-slice.

Work one slice at a time. Do not involve the user unless blocked: an ADR or architecture conflict,
a needed decision, a guardrail or free-tier limit in the way, missing credentials, or 3 failed review rounds.

**Model trial (ended):** S17c2–S19b ran a Sonnet developer (rounds 2, 2, 3, 3, 2, 3); three 3-round slices met the stop
rule. From S20 (2026-10-18, user left the choice to the lead) the developer is Opus; the fixer stays Sonnet, the reviewer
Opus. Keep recording each slice's review rounds in status.md.

## Non-negotiables

- Nothing hard-coded in code; the catalog, environment descriptor and configuration carry the knowledge.
- Explicit over implicit: a workload gets only what it declares; guardrails require, never silently add
  ([conventions](docs/engineering/conventions.md#design-rules-that-code-must-keep)).
- Simplification over complication: the simplest approach, fewest concepts; no unnecessary code; clean architecture and
  separation of concerns.
- Design principles 6–14 in [context.md](docs/context.md#design-principles) (from the user) bind every design.
- Every Azure resource inside the free tier; test resources torn down after use.
- Small slices, one PR each; `tools/verify.sh` green on every commit.
