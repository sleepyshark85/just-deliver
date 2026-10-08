# Review checklist

Used by the reviewer on every PR, and by the developer before asking for review. Each item maps to
[standards.md](standards.md). Review the **diff and the files it touches**, against the **slice brief**.

## Verdict format

```
VERDICT: APPROVE | CHANGES REQUIRED

BLOCKING
- [rule] path/to/file.cs:42 — what is wrong — why it matters — what would fix it

SUGGESTIONS
- [rule] path/to/file.cs:10 — ...

CHECKED
- one line per section below: ok / n/a / see finding
```

Any blocking finding means CHANGES REQUIRED. Findings cite a file and line and the rule broken;
"could be cleaner" without a rule is a suggestion, never blocking.

## 1. Correctness — blocking
- Does it do what the slice brief asks, including edge cases and failure paths?
- Are errors surfaced with actionable messages; nothing swallowed?
- Do tests actually exercise the behaviour (not just execute it)? Would they fail if the code broke?

## 2. Nothing hard-coded — blocking
- Any resource name, region, SKU, role GUID, template name, environment name, URL or id in C#?
- Any branching on resource type or workload name?
- Any knowledge that belongs in the catalog, environment descriptor or configuration?

## 3. Simplicity and no unnecessary code — blocking when clear
- Is there code the slice does not need: speculative abstractions, unused options, interfaces with a
  single implementation and no test seam, dead or commented-out code?
- Is there a simpler approach that meets the brief? Name it.
- New dependency or project without a stated reason?

## 4. Architecture and separation of concerns — blocking
- Dependency direction per standards §3; inner layers free of Pulumi/Azure/CLI.
- One responsibility per type; side effects isolated behind adapters; logic unit-testable.
- Consistent with the ADRs and architecture docs. Divergence must be escalated, not merged.

## 5. Code quality — blocking for clear violations
- Naming, nullability, async/cancellation, error handling per standards §4.
- Matches surrounding style; comments explain why.

## 6. Tests — blocking
- Behaviour change without tests? Missing golden test for resolver behaviour?
- Azure tests tagged `Category=Azure`; unit tests offline and deterministic.

## 7. Free tier and Azure safety — blocking
- Every resource the change can create fits the free-tier rules (standards §6): Cosmos RU/s budget,
  Container Apps consumption + `minReplicas: 0`, Log Analytics daily cap, nothing paid.
- Resource groups tagged; teardown path exists; region from configuration.
- No changes to policies, role definitions, app registrations or sandbox setup.

## 8. Repository hygiene
- Slice-sized PR; conventional commits; no secrets; docs updated with behaviour.
