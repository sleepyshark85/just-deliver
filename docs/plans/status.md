# Work status

> Where the work stands. **Read first every session; update last** (lead only). Phases: [roadmap.md](roadmap.md).

## Now

- **Phase 1.5 done** (2026-11-16, Azure exit gate green). Next: the phase 2 design note ([roadmap](roadmap.md)) for the user.
- **In progress:** S27a–S27d, the docs restructure (user, 2026-11-17), in parallel; S27a merges last.
- **Waiting for the user:** ADR 0019 says "approved by the user" but is Proposed; the new ADR 0020 (stack per graph node,
  supersedes 0003 decision 3); ADR 0011's id rule rewritten to the schema's (see its note).
- **Sandbox secret expires 2026-12-14** (renew: `tools/sandbox/sandbox.sh apply … --rotate-secret`, the user as Owner).
- **Team:** developer Opus from S20 (after a Sonnet trial S17c2–S19b), fixer Sonnet, reviewer Opus.

## Slices

States: `todo` · `in-progress` · `in-review` · `changes-requested` · `done` · `blocked`. Rounds = review rounds.

| ID | Slice | State | PR | Rounds | Note |
|---|---|---|---|---|---|
| S00 | Repo baseline and team setup | done | `main` | | Strict build, verify gate, CI, agents |
| S01 | Workload schema v2 | done | #2 | 1 | `id` pattern widened against ADR 0011 |
| S02 | Catalog formats and loader | done | #3 | 2 | |
| S03 | Environment descriptor | done | #4 | 2 | NJsonSchema ignores `propertyNames`; enforced in code |
| S04 | Expression evaluator | done | #5 | 2 | Golden hash and guid values |
| S04a | Catalog loader hardening | done | #6 | 2 | |
| S05 | Matching and expansion | done | #9 | 1 | |
| S06 | Policies and provenance | done | #10 | 2 | |
| S07 | Graph builder | done | #11 | 2 | Stack name per node (ADR 0020) |
| S08 | CLI skeleton | done | #12 | 2 | M1 complete |
| S09 | Template library | done | #13 | 2 | Cosmos account capped at 1,000 RU/s |
| S10 | Provider fixes | done | #14 | 2 | Real offline Pulumi tests in the gate |
| S11 | Orchestrator (infra nodes) | done | #20 | 2 | First Azure slice |
| S12 | Substrate from data | done | #22 | 2 | `jd env up` |
| S12a | `database` type (user decision) | done | #23 | 1 | The catalog picks the engine |
| S13 | Release set (offline) | done | #18 | 2 | |
| S13b | Deploy a release set (infra) | done | #24 | 2 | Azure run guard added |
| S14 | Sample app | done | #16 | 2 | Public image on GHCR |
| S15a | `container-app` template | done | #25 | 1 | |
| S15b | Runtime mapping and the `runtime` node | done | #26 | 1 | |
| S15c | Variables and `runtime.*` policies | done | #28 | 5 | Escalated after 3; design narrowed by the user |
| S16a | Revision and traffic inputs | done | #29 | 1 | |
| S16b1 | Release data for the deploy steps | done | #30 | 1 | |
| S16b2 | Deploy steps and HTTP probe | done | #32 | 3 | Shift always after a passed probe |
| S16c | Revert-safe suffix, E2E Azure test | done | #33 | 1 | |
| S17a | Release record design, deployer identity | done | #34 | 2 | |
| S17b | Release record, OQ, timings | done | #35 | 2 | |
| S17c1 | IQ and `jd release qualify` | done | #40 | 3 | Blocked after 3; finished by the lead |
| S17c2 | IQ inside `jd release deploy` | done | #41 | 2 | First Sonnet-trial slice |
| S18 | Second environment and approvals (design) | done | #42 | 3 | |
| S18a | Approval policy, gate, `stage` | done | #43 | 2 | |
| S18b | Qualification reader | done | #44 | 3 | |
| S18c | `jd approve` | done | #46 | 3 | |
| S19a | Approval reader, promotion check | done | #47 | 2 | |
| S19b | `jd promote` | done | #48 | 3 | |
| S20 | E2E run and docs | done | #49 | 1 | MVP complete; Opus developer from here |
| S20w | Manual-test walkthrough and kit | done | #50 | 2 | |
| S21 | Catalog by component (design) | done | #55 | 3 | Phase 1 |
| S21a | Environment types replace tier | done | #56 | 2 | Deployed-config snapshot is the refactor proof |
| S21b | Components and runtimes | done | #57 | 3 | |
| S21c | Monitoring as a requirement; policies require | done | #58 | 2 | Upgrade gate green |
| S21d | Shared resources, `uses`, load checks | done | #60 | 3 | |
| S21e | Environments from offers | done | #61 | 2 | |
| S21f | Workloads through `uses`; grant target rule | done | #62 | 2 | Phase 1 done |
| S23 | Nothing set on the machine | done | #64 | 2 | Local state, record store, region in definition |
| S22 | Container Apps logs without the shared key | done | #67 | 3 | Diagnostic settings deleted before group deletes |
| S22a | Logs table type explicit; unread exports removed | todo | | | Needs the Azure gate |
| S24 | The code knows no file structure (design) | done | #66 | 3 | Phase 1.5 |
| S24a1 | One loader | done | #68 | 3 | |
| S25 | Thin CLI over an application layer (design) | done | #69 | 3 | |
| S25a | Connections | done | #73 | 3 | |
| S26 | C# conventions conformance | done | #75 | 2 | |
| S25b1 | Frame, validate | done | #76 | 3 | |
| S25b2 | Workload commands | done | #77 | 3 | |
| S25c | Env up | done | #78 | 2 | |
| S25d | Release create and show | done | #79 | 2 | |
| S25e1 | Release run start | done | #80 | 3 | |
| S25f1 | Approve | done | #81 | 2 | |
| S24a2 | Meta-schema and index | done | #82 | 2 | |
| S25e2 | Release deploy and qualify | done | #83 | 3 | |
| S24a2b | Literal guard | done | #84 | 3 | |
| S24a3 | Names | done | #85 | 2 | |
| S25f2 | Promote | done | #86 | 2 | |
| S24b1 | Binder and parts | done | #88 | 2 | |
| S24b1b | Catalog-level roles | done | #89 | 2 | |
| S24b2 | Reads and scopes | done | #90 | 2 | |
| S24b3 | Grants, contracts, completeness | done | #91 | 2 | |
| S24b4 | Catalog collections | done | #92 | 2 | `CatalogParser` deleted |
| S24c1 | Environment | done | #93 | 2 | |
| S24c2 | Workload | done | #94 | 2 | |
| S24c3 | Expansion model | done | #95 | 2 | |
| S24c4 | Expansion locations | done | #96 | 2 | |
| S24d | Policies | done | #97 | 3 | |
| S25e1b | Template library path form | done | #98 | 2 | |
| S24e | Rollout data | done | #99 | 2 | Azure gate green |
| S25g | Close | done | #100 | 2 | R1–R3 with empty allowlists |
| S24f | YAML jd writes | done | #101 | 1 | |
| S24f2 | Release set and manifest reads | done | #102 | 2 | |
| S24g | Docs and standards | done | #103 | 2 | Phase 1.5 done; ADR 0019 |
| S27a | Docs: entry, framing, plans, decisions | in-progress | | | |
| S27b | Docs: agent entry and engineering | in-progress | | | |
| S27c | Docs: model, documents, provisioning | in-progress | | | |
| S27d | Docs: release, approvals, application, usage | in-progress | | | |

## Open items

Take when touching the code named; remove once done.

- Loader: a scanner failure reads only "Exception during deserialization (line N)" (pinned in `YamlSchemaValidatorTests`); add the inner message.
- Deeply nested YAML (~6,000 levels) overflows the stack in YamlDotNet; a depth guard before parsing would make it a load error.
- `ContainerAppAzureTests`: hard-codes `management.azure.com` (use a resource-id URL as `EnvUpAzureTests` does); builds two stacks;
  repeats ~60 lines of `DeployAzureTests` setup.
- A secret in workload `variables` is plain text in the Container App's `env`: make it a Container App secret with `secretRef`
  (ADR 0013 runtime port); the revision suffix hashes secret values; no test pins that the suffix tells null from empty.
- Accepted phase-1.5 debt: `ResolvedGraph`/`PolicyResult` shapes (`GraphNode` positional); `Orchestrator.Walk`'s `preview: bool`;
  `YamlSchemaValidator` → a parser, `ValidateTokenAsync`/`ValidationResult` deleted (touches `TemplateLibrary`: Azure gate);
  `PolicyApplier.Apply`/`ApplyLayer` length; `EnvironmentParser.LoadAsync(SourceDocument)` repeats `ParseAsync`.
- `Expander`, `PolicyApplier`, `GraphBuilder` are public only for jd.bp.pulumi.tests; test classes are public; `CA1031` is not on.
- `ReleaseFlow`: `StepLog.Dark` assigned inside an expression; `Prepared` is a second top-level type in the file.
- `PreviewFormatter` prints the probe as "GET <path>" without the scheme; `HttpRevisionProbe` accepts any URI scheme.
- `CatalogLoader` is hard-wired to `SchemaIndex.Shipped`; `new Bound(...)` takes 12 positional arguments.
- CI downloads azure-native on every run (cache `~/.pulumi/plugins`); templates do not pin the azure-native version.

## Environment

| Item | Value |
|---|---|
| Subscription | `MVPLandingZone` (`ca89cbcc-e368-4e81-9e80-6686b4d9f3b9`), Pay-As-You-Go, sandbox guardrails applied |
| Credentials | `~/.just-deliver/ca89cbcc-e368-4e81-9e80-6686b4d9f3b9.env`; secret expires 2026-12-14 |
| Region | `southeastasia`, from the environment definitions' `region` |
| Azure gate | `tools/verify.sh --azure`, the lead's only, one run at a time, ~1 h 20 m ([conventions](../engineering/conventions.md#azure-gate)) |
| Live Azure resources | none |

## Session log

Newest first; the last five sessions.

- **2026-11-16 (S24g, phase 1.5 exit)** — S24g merged (2 rounds) after the user's Azure exit gate (jd.cli.tests 341, 1 h 23 m).
  Phase 1.5 done. ADR 0019 (Proposed) awaits the user. Next: the phase 2 design note.
- **2026-11-15 (S24f2)** — S24f2 merged (2 rounds). Next: S24g.
- **2026-11-15 (S24f)** — S24f merged (1 round, split taken: S24f2 for the release set and manifest reads).
- **2026-11-15 (S25g)** — S25g merged (2 rounds); the S25 track is done.
- **2026-11-15 (S24e)** — S24e merged (2 rounds) after the user's Azure gate (jd.cli.tests 339, 1 h 19 m). Lead removed 13 stale
  worktrees and 29 merged branches; `origin/mappings-sample-approach` and `origin/typing-sample-approach` left for the user.
