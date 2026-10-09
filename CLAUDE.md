# just-deliver

Internal Developer Platform for Azure. Start every session with [docs/README.md](docs/README.md), then:

- [Work status](docs/plans/status.md) — **where we are; read first, update last**
- [MVP plan](docs/plans/mvp.md) — scope and slices S01–S18
- [Engineering standards](docs/engineering/standards.md) and [review checklist](docs/engineering/review-checklist.md) — binding for all code
- [Decisions](docs/decisions/README.md) — ADRs; all **Proposed — pending review**; only the user changes their status
- [Open questions](docs/open-questions.md), [architecture](docs/architecture/)

## Team

| Role | Who | Model |
|---|---|---|
| Lead | the main session | Opus 5.5 |
| Developer | `developer` agent ([.claude/agents/developer.md](.claude/agents/developer.md)) | Sonnet 5.5 |
| Reviewer | `reviewer` agent ([.claude/agents/reviewer.md](.claude/agents/reviewer.md)) | Opus 5.5 — a different model from the developer, by design |

## Lead workflow

1. **Session start:** read `docs/plans/status.md`. Resume the slice in progress, or take the next `todo` slice.
2. **Brief:** expand the slice from `mvp.md` into a brief — goal, acceptance criteria, files in scope,
   out of scope, ADRs/docs that apply. Mark the slice `in-progress` in status.md.
3. **Develop:** dispatch `developer` with the brief.
4. **Review:** dispatch `reviewer` with **only the brief and the branch name** — never the developer's report.
5. **Iterate:** on CHANGES REQUIRED, send the blocking findings to the developer (same branch). The lead
   arbitrates disputes using the standards. After 3 rounds without APPROVE, mark the slice `blocked`
   and escalate to the user.
6. **Merge:** on APPROVE with `tools/verify.sh` green, push the branch, open a PR to `main` (CI must pass),
   merge, delete the branch.
7. **Record:** update status.md — slice state, PR link, notes — before the next slice.
8. **Session end:** update status.md "Now" and add a session-log entry, even mid-slice.

Work one slice at a time. Do not involve the user unless blocked: an ADR or architecture conflict,
a needed decision, a guardrail or free-tier limit in the way, missing credentials, or 3 failed review rounds.

**Model trial:** the developer/reviewer pairing (Sonnet/Opus) is on trial. If PR quality is poor — repeated
blocking design findings, over-engineering, or slices needing 3 rounds — stop and ask the user to choose
different models. Record each slice's review rounds in status.md so the trial can be judged.

## Non-negotiables

- Nothing hard-coded in code; the catalog, environment descriptor and configuration carry the knowledge.
- Simplest approach; no unnecessary code; clean architecture and separation of concerns.
- Every Azure resource inside the free tier; test resources torn down after use.
- Small slices, one PR each; `tools/verify.sh` green on every commit.
