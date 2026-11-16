# Conventions

How the team works. The code rules are in [standards.md](standards.md) and [csharp.md](csharp.md), the review's order and
verdict in [review-checklist.md](review-checklist.md), the lead's steps in [CLAUDE.md](../../CLAUDE.md#lead-workflow), and
[status.md](../plans/status.md) is the single record of where we are.

## Roles

- **The lead** (the main session) turns a slice into a brief, dispatches the developer, has the branch reviewed, runs the Azure
  gate and merges. The agents and their models are in [CLAUDE.md](../../CLAUDE.md#team).
- **The developer** implements a slice, or review findings that need design work. **The fixer** takes findings that are all
  small, with an obvious fix, a few files each. The lead chooses per round by the size of the findings, not by habit.
- **The reviewer** judges a branch against its brief and never edits.

## Briefs

A brief comes from [mvp.md](../plans/mvp.md) or the phase's design note, and points rather than copies: the phase and phase
goal it serves ([roadmap](../plans/roadmap.md#rules-for-work)), its goal and acceptance criteria, the files in scope, what is
out of scope, the named sections that apply (`release-record.md §5–7`, `standards §3`, never "read the docs"), the security
boundaries as **required tests**, and a production-line cap. **The cap is the only size measure:** a slice past it needs the
lead's agreement. Rules are not copied into a brief; they live once, in their owner.

## Reviews

- **The reviewer is independent.** It gets the brief and the branch name, never the developer's reasoning or report.
- **One reviewer per slice, kept for all its rounds.** Rounds 2 and 3 go to the same reviewer with the new head and nothing else;
  it reviews what changed since the head it last judged plus anything that change could break. A fresh reviewer every round
  re-reads everything and finds new things each time; one carrying many slices approves too easily. A strict re-review (the
  lead doubts an approval) gets a new reviewer and counts as one of the rounds.
- **Find everything in round 1** (the reviewer's procedure: [reviewer.md](../../.claude/agents/reviewer.md)); each late finding
  costs a round. A later round may raise a blocking finding an earlier round missed, once, marked as **late**; it counts within
  the rounds.
- **"It works" or "it was accepted before" is never a reason to approve** code the reviewer would not write.
- **At most three rounds.** A round-3 verdict that still blocks stops the slice: the lead marks it `blocked` in status.md and
  takes it to the user with the options. Nobody starts a fourth round on their own.
- **The lead arbitrates** a disputed finding using the standards.

## Reports and context

- **Reports are short:** about ten lines in the developer's format ([developer.md](../../.claude/agents/developer.md), *When
  done*); detail goes in the commit message body. A decision, a deviation or a surprise always goes in `Needs the lead`,
  whatever the length.
- **Context is cost.** Every step re-sends an agent's whole context, and the lead's is the most expensive. The context-size hook
  (`.claude/hooks/context-size.py`, `--report` lists every agent) warns an agent at 150k tokens to plan its finish and at 300k to
  hand back; it tells the lead to update status.md and ask the user to `/compact`. **Resume an agent** (`SendMessage`) for the
  same slice only while it is under 150k; otherwise dispatch a fresh one with the findings. The lead never reads whole diffs or
  logs: `grep`, `head`, or an agent's short report.
- **Claims are reproduced, not trusted.** A constraint an agent reports ("the SDK cannot", "this mutant is equivalent", "Azure
  rejects that") becomes a rule, a design input or an exclusion only once it is shown: a failing command, a test, a spike.

## Azure gate

Only the lead runs `tools/verify.sh --azure`, one run at a time; agents never run Azure tests or create Azure resources. The
suite is one end-to-end scenario (~1.5 h), so it runs only when **what reaches Azure changes** (the user's choice, 2026-10-18):

- **What reaches Azure** is (1) the deployment artifact: every Pulumi package jd hands the backend for `env up`, `deploy` and
  `release deploy` — stack, config entries and secret flags, and the program's SHA-256 — snapshotted by
  `DeployedConfigSnapshotTests` in `src/jd.cli.tests/golden/deployed-config.json`; and (2) the code that calls Azure directly,
  whose effect no snapshot shows: `src/jd.azure`, `src/backend-providers`, `src/jd.orchestrator/ReleaseFlow.cs`, `Qualifier.cs`,
  `src/jd.cli/HttpRevisionProbe.cs`. An edit to the Azure tests alone (`*AzureTests.cs`, `DeployerIdentityTests.cs`) does not
  trigger a gate: the next gate runs them, at the latest the phase's exit gate.
- **No gate** when `git diff origin/main -- src/jd.cli.tests/golden/deployed-config.json` is empty and none of the code in (2)
  changed. A slice that regenerates the snapshot says why in its brief; the lead checks the diff is exactly the intended change.
  A refactor's proof is the unchanged snapshot.
- **Gate** when the snapshot or the code in (2) changes, and **once at the end of each phase** as its exit evidence. The lead
  records "Azure gate: run (reason)" or "none, artifact unchanged" in status.md.
- **When:** after the developer's hand-back, before review round 1, so a failure only Azure can show costs a developer round,
  not a blocked slice. Later rounds re-run it only if they change the snapshot or the code in (2); otherwise the first run
  stands for the approved head. A gate failure goes back through the developer like a review finding.
- **It does not block the next slice:** while a gate runs, the next slice may be developed on a branch from the gated one
  (rebased onto `main` after the merge). Merges stay in order, and only one slice is in review at a time.

## Escalation

Escalate, never improvise. An agent stops and reports to the lead with the specific question when a brief is ambiguous,
conflicts with an ADR or a design note, or cannot be done without hard-coding something, and when a guardrail is in the way, a
decision is needed or credentials are missing. The lead involves the user only when blocked: an ADR or architecture conflict, a
needed decision, a guardrail or free-tier limit in the way, missing credentials, or three review rounds without approval.
