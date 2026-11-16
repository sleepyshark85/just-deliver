# Review checklist

The order of the review and its verdict. The reviewer walks every check over the diff and the files it touches, against the
brief; the developer walks it before handing back. Each check names what blocks; the rule itself is in the linked owner.

## Verdict format

```
VERDICT: APPROVE | CHANGES REQUIRED

BLOCKING
- [rule] path/to/file.cs:42 — what is wrong — why it matters — what would fix it

SUGGESTIONS
- [rule] path/to/file.cs:10 — ...

CHECKED
- one line per check below: ok / n/a / see finding
```

Any blocking finding means CHANGES REQUIRED. A finding names the rule, the file and line, and a fix. Without a rule it is a
suggestion; suggestions never block, and a broken rule is never a suggestion.

## Checks

1. **Brief.** Something the brief asks is missing or wrong, including edge cases and failure paths; a required test is missing
   or does not prove its boundary; work outside the phase goal the brief names, or a choice that makes a later phase's idea
   harder ([roadmap](../plans/roadmap.md#rules-for-work), rules 1 and 4).
2. **Nothing hard-coded.** Anything [standards §1](standards.md#1-nothing-hard-coded) forbids, including a literal the guard
   allows that still hides structure.
3. **Simplicity.** Anything [standards §2](standards.md#2-simplest-thing-that-works) forbids; name the simpler design.
4. **Architecture.** A dependency, port or responsibility against [standards §3](standards.md#3-architecture-and-separation-of-concerns);
   a divergence from an ADR or design note ([standards §3](standards.md#3-architecture-and-separation-of-concerns)).
5. **Design invariants.** A change that breaks one of [standards §4](standards.md#4-design-invariants) or a design principle it
   points to, unless its design note or ADR was changed first ([standards §4](standards.md#4-design-invariants)).
6. **C#.** Anything [csharp.md](csharp.md) forbids, beyond its *aim* numbers without a reason.
7. **Tests.** Anything [standards §5](standards.md#5-tests) forbids; a branch, guard or error path whose mutation survives.
8. **Free tier and Azure safety.** Anything [standards §6](standards.md#6-azure-and-the-free-tier) forbids.
9. **Git, docs and formats.** Anything [standards §7](standards.md#7-git-and-prs) forbids.
