# 0005. Build B/C first, design for D

- **Status:** Proposed — pending review
- **Origin:** investigation notes (platform options; decisions and approach)

## Context
Three options were considered (details in [context.md](../context.md#options-considered)):
- **B:** a release lifecycle tracker with enforced, SSO-linked digital approval gates. IT Ops still does the work by hand.
- **C:** Dev self-serves up to the handoff, and the platform generates structured async work orders for IT Ops. Still limited by IT Ops capacity.
- **D (end goal):** full automation; IT Ops approves and triggers, and secrets are injected automatically. Biggest investment; needs IT Ops buy-in and significant change management.

Going straight to D carries organisational risk. Stopping at B/C leaves IT Ops as the bottleneck.

## Decision
Build B/C first and shape it so that moving to D is a smooth transition, not a rebuild. Five design principles apply:

1. **Separate intent from execution.** Tasks carry an `executor` field (manual, later automated); the payload shape stays the same.
2. **Structured data over free text.** Infra requirements and secrets are typed records, not narrative documents.
3. **Secrets in a vault from day one** (Azure Key Vault), never in the platform DB. In D, apps read them directly from the vault.
4. **Pluggable deployment actions.** A deployment record has a `trigger` field: `manual_instruction` now, `api_call` later.
5. **Never let work happen outside the platform.** Shadow processes break the audit trail and block automation.

Transition stages:
```
B/C        -> Platform tracks intent, Ops executes manually
B/C+       -> Platform generates precise runbooks, less meeting time
D-partial  -> Automate low-risk tasks first (staging deploys, secret injection)
D          -> Full automation, Ops handles approvals and exceptions only
```

## Consequences
- The data model (tasks, deployment records, secret references) has to work for both manual and automated executors from the start.
- Approvals, the audit trail and secrets handling are delivered early, before any automation.
- IT Ops' role changes gradually (executor → runbook follower → approver/exception handler), which lowers the change-management risk.
- The provisioning work ([0002](0002-pulumi-automation-api.md), [0003](0003-pulumi-yaml-template-library.md)) already targets D-style automation. How that work fits with the B/C-first ordering should be confirmed in [plans/mvp.md](../plans/mvp.md).

## Related
- [context.md](../context.md), [0001 custom build](0001-custom-build-api-and-clients.md), [0004 two-layer definition model](0004-two-layer-definition-model.md)
- [plans/mvp.md](../plans/mvp.md), [open-questions.md](../open-questions.md)
