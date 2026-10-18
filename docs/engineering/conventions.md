# just-deliver conventions

How we work on just-deliver and the bar every change is held to. [standards.md](standards.md) holds the concrete rules
(nothing hard-coded, the layer table, the free tier, git); this page says how work flows, what *good* means, and how the
review applies it. The reviewer walks [review-checklist.md](review-checklist.md) with this page's
[code quality bar](#code-quality-bar). Where this page and standards.md seem to disagree, standards.md wins for what a
rule says and this page for how it is applied; raise the conflict so one of them is fixed.

## How we work

- **One slice at a time, one branch, one PR.** The lead (the main session) expands a slice of [mvp.md](../plans/mvp.md)
  into a brief, dispatches the developer, has the branch reviewed, runs the Azure gate and merges. The workflow steps are
  in [CLAUDE.md](../../CLAUDE.md); [status.md](../plans/status.md) is the single record of where we are.
- **Who codes and reviews** (`.claude/agents/`):

  | Agent | Model | Used for |
  |---|---|---|
  | `developer` | Sonnet | a slice; review findings that need design work |
  | `fixer` | Sonnet | review findings that are all small, with an obvious fix, a few files each |
  | `reviewer` | Opus | judging a branch against its brief; never edits |

  The lead chooses developer or fixer per round by the size of the findings, not by habit.
- **The reviewer is independent.** It gets the brief and the branch, never the developer's reasoning or report.
  **One reviewer per slice, kept for all its rounds**: round 2 and 3 are sent to the same reviewer with the new head
  and nothing else, and it reviews what changed since the head it last judged plus anything that change could break. A
  fresh reviewer every round re-reads everything and finds new things each time; one carrying many slices approves too
  easily. A strict re-review (the lead doubts an approval) gets a new reviewer and counts as one of the three rounds.
- **At most three rounds.** A round-3 verdict that still blocks stops the slice: the lead marks it `blocked` in
  status.md and takes it to the user with the options. Nobody starts a fourth round on their own.
- **Briefs point, they don't copy.** A brief names the slice, its acceptance criteria, the files in scope, what is out
  of scope, the named sections that apply (`release-record.md §5–7`, `standards §3`, never "read the docs"), the
  security boundaries as **required tests**, and a production-line cap. Rules live here and in standards.md, once.
- **Reports are short.** Agents report in about ten lines (the developer's *When done* format): head sha, verify
  result, line count, `Needs the lead`, `Left open`. Detail goes in the commit message body. The lead reads every
  report and its context is the most expensive one in the session; a decision, a deviation or a surprise always goes in
  `Needs the lead`, whatever the length.
- **Context is cost.** Every step re-sends an agent's whole context. The context-size hook
  (`.claude/hooks/context-size.py`, `--report` lists every agent) warns an agent at 150k tokens to plan its finish and
  at 300k to hand back; it tells the lead to update status.md and ask the user to `/compact`. Resume an agent
  (`SendMessage`) for the same slice only while it is under 150k; otherwise dispatch a fresh one with the findings.
  The lead never reads whole diffs or logs: `grep`, `head`, or an agent's short report.
- **Azure is the lead's.** Only the lead runs `tools/verify.sh --azure`, one run at a time; agents never run Azure
  tests or create resources. A gate failure goes back through the developer like a review finding. The suite is one
  end-to-end scenario (~2 h since S18a), so it runs **once per slice, and only for an Azure-facing slice** (user's
  choice, 2026-10-17):
  - **Azure-facing** means the slice changes an Azure test (`*AzureTests.cs`, `DeployerIdentityTests.cs`),
    `src/jd.azure`, `src/backend-providers`, `catalog/`, `samples/environments`, `samples/sample-app`, or what
    `env up`, `deploy` or `release deploy` sends to Azure. Any other slice merges on the offline `verify.sh` (CI runs it
    on every PR). The lead decides and records "Azure gate: run" or "none, not Azure-facing" in status.md.
  - **When it runs:** after the developer's hand-back, before review round 1, so a failure only Azure can show costs a
    developer round, not a blocked slice. Later rounds re-run it only if they touch Azure-facing code; otherwise the
    first run stands for the approved head.
  - **It does not block the next slice:** while a gate runs, the next slice may be developed on a branch from the
    gated one (rebased onto `main` after the merge). Merges stay in order, and only one slice is in review at a time.
- **Claims are reproduced, not trusted.** A constraint an agent reports ("the SDK cannot", "this mutant is equivalent",
  "Azure rejects that") becomes a rule, a design input or an exclusion only once it is shown — a failing command, a
  test, a spike. The S16c suffix redesign started from a lead spike, not from a report.
- **Escalate, never improvise.** A brief that conflicts with an ADR or a design note, a guardrail in the way, a needed
  decision, missing credentials: stop and report. ADR status changes only by the user.

## Code quality bar

**The bar is good code, not working code.** Code below it never passes review, however correct it is and whatever
the checks say. The reviewer applies it to every changed line as its next maintainer would read it; everything in
the table is **blocking**.

| Area | Blocking |
|---|---|
| Data, not code | Anything standards §1 forbids: a resource name, region, SKU, role id, template or output name, URL or Azure behaviour in C#; a branch on a resource type, template or workload name. Knowledge goes in the catalog (templates, mappings, policies), the environment descriptor or configuration, and the code reads it by the names the data gives |
| Names and comments | A name that doesn't say what a thing is or does. A comment that restates the code, or code so unclear it needs one; a comment saying *why* (an Azure rule, a design-note section, a residual risk) is welcome |
| Structure | Duplication; a method doing more than one thing; deep nesting; a long parameter list (a record or a smaller seam instead); a helper, option or table entry that does nothing yet; an analyzer warning suppressed to let one of these through |
| Literals | A string the code depends on spelled inline instead of named once: an environment variable, a CLI command or option, an output or record key written or read in more than one place, an exit code. A one-off message stays inline. Tests spell what they expect themselves, never through the constants of the code under test |
| API and contracts | A public type or member that could be internal or private; a public one without a doc comment saying what it is for; a port (interface) with one implementation and no test seam or second adapter; a port defined away from its consumer's layer (standards §3) |
| Errors | An exception that reaches the user raw (an SDK type, a stack trace, `Retry failed after 4 tries`); catching too broadly where one type is meant; swallowing an error or losing its cause; an error handled twice (logged and rethrown); a bug turned into a silent default; a message a team member cannot act on. Expected outcomes (a probe that fails, IQ that fails, drift) are results with a reason, not exceptions |
| Secrets and data | A secret in code, a fixture, a log, an error, the console or a release record; a value the code did not need copied into output "for context" (`NodeReport.Changes`, outputs); external input (definitions, set files, labels, Azure replies) used before it is validated at the boundary; a path built from input without rejecting `..` and separators |
| Azure and cost | Anything standards §6 forbids; a resource without a teardown path; a call without a timeout or cancellation; a retry without a bound; anything that could bill outside the free tier — blocking even when the policy guardrail would stop it |
| Resources and async | A stream, file, process or client not released on every path; blocking on async (`.Result`, `.Wait()`); a `CancellationToken` not passed through; a sleep or a real clock where a `TimeProvider` or an injected delay belongs |
| Persisted formats | A change to a persisted or shared format (catalog and mapping schemas, release-record schema, golden files, set files, Pulumi stack names) without its schema, its goldens, its docs and the readers of older files changed in the same PR, or a note why old files stay valid |
| Dependencies | A new package without a line in the PR saying why the BCL or an existing one is not enough; a package outside its adapter project (verify.sh's boundary checks); a large library for a small problem |
| Size and scope | Code the slice does not need; a refactor or reformatting bundled in; a slice past its production-line cap without the lead's agreement |
| Tests | See [Tests](#tests): a test that would still pass if the behaviour it names broke, a branch no test notices, an error test that does not pin which error, logic that decides what a test asserts |

How it is applied:

- **"It works" or "it was accepted before" is never a reason to approve** code the reviewer would not write. A later
  round may raise a blocking finding an earlier round missed, once, marked as late; it counts within the three rounds.
- **Find everything in round 1.** The reviewer walks every section of the checklist and every new branch in round 1,
  rather than stopping at the first few findings; late findings cost a round each.
- **A finding names a rule, a file and line, and a fix.** "Could be cleaner" without a rule is a suggestion.
  Suggestions never block; anything in the table above is never a suggestion.
- **A rule turned off needs a reason the reviewer agrees with,** on the line that turns it off. "To make the check
  pass" is not one.

## Tests

- **Tests are the developer's check on its own work.** Each required test in a brief names a boundary (a secret that
  must not appear, a call that must not happen, an order that must hold); the test proves the boundary, not only the
  happy path around it.
- **Every test can fail.** For every branch, guard and error path a change adds, the developer breaks it on purpose
  (invert the condition, drop the call, return the happy value), sees a test fail and restores it. A branch no test
  notices is blocking. The reviewer mutates where in doubt; a surviving mutation in changed code is blocking unless
  the PR shows why it is equivalent.
- **Fakes only at boundaries:** the backend (`IBackEndProvider`), Azure (identity, live revisions), the HTTP probe,
  the clock and delays, the file system where a temp directory is impractical. Everything inside — resolver,
  orchestrator, release flow, record builder — runs for real.
- **Error tests pin the error:** its type and the part of the message that is the contract (the variable it names,
  the node, `pinned …, live … on revision …`), and what did *not* happen (no backend call, no file written).
- **Offline and fast.** Unit tests never touch the network or Azure, never sleep, and use a `TimeProvider` and
  injected delays. A flaky or slow test is a bug to fix, never a retry.
- **Golden files change only on purpose,** regenerated by a deliberate step and reviewed in the diff like code; a
  test run never rewrites them.
- **Azure tests** (`Category=Azure`) prove what offline tests cannot — Azure's behaviour — and assert the exact line
  that shows it (the image-check text, the traffic weights), not only an exit code. They leave nothing behind.

## Design rules that code must keep

These hold across slices; a change to one changes its design note or ADR first.

- **Explicit over implicit** (the user's principle, 2026-10-18). What a workload gets, it asks for: every resource, grant and
  setting comes from something written down — the workload's `requires`, a component's `uses`, an environment's offers. No
  silent additions: a guardrail policy *requires* (and fails a workload that lacks it), it does not add behind the
  team's back. Implicit, platform-owned resources (resource groups, the Container Apps environment, the shared Cosmos
  account) are a short named list, and workloads are never granted on them. A grant targets only what the workload's
  own requirements created. Prefer exact lookups to matching rules. A design that adds an implicit resource, grant,
  default or match needs the user's go-ahead.
- **Simplification over complication** (the user's principle). Remove a concept before adding one; prefer an exact
  lookup to a matching rule, one mechanism to two, deleting code to adding it. A design note names the simpler
  alternative it rejected and why.
- **Cohesion, contracts and ownership** ([context.md](../context.md#design-principles) 8–11): one component in one
  place; consumers bind to contracts; a workload definition is the same in every environment; one owner per resource.
- **Data drives the platform.** Templates, mappings, policies and the environment descriptor carry every resource
  fact; the code knows formats and names *from the data* (a mapping's `release:` block names the outputs the release
  flow reads).
- **Ports in `jd.core`, adapters at the edge.** Pulumi only in `jd.bp.pulumi`, Azure SDKs only in `jd.azure`, wiring
  only in `jd.cli` (standards §3; `verify.sh` enforces it).
- **A bad revision never gets traffic.** Dark deploy, grants, probe, then shift; a failed step stops the walk and
  leaves traffic where it was ([provisioning.md](../architecture/provisioning.md)).
- **Evidence is written once.** Release records and `set.yaml` are created with `CreateNew` and never edited; a re-run
  adds a record ([release-record.md](../architecture/release-record.md), ADR 0007).
- **Records are built from an allowlist,** never by serializing a report; no secret can reach them by construction.
- **What runs is observed live.** IQ reads traffic and images from Azure, never from state or the record; a deploy
  never hides drift from `qualify`.
- **The identity is the platform's, not the data's.** Who deployed comes from the credential's token, never from a
  definition or set file.
