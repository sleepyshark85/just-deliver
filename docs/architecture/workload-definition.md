# Workload Definition

> Status: design reference — not yet reviewed as decisions. Proposed decisions live in [decisions/](../decisions/).

What a team declares, and what it deliberately does not. Schema: [`workload.schema.json`](../../schemas/workload.schema.json);
sample: [`workload.yaml`](../../samples/provisioner/workload.yaml); resolution: [provisioning.md](provisioning.md),
[resolver.md](resolver.md); model: [0004](../decisions/0004-two-layer-definition-model.md).

## Principles

| Principle | Meaning | Why |
|---|---|---|
| Simple definitions for teams | Teams declare *what*, not *how* | Lower friction and cognitive load; teams focus on business logic |
| Platform team maintains mappings | "How to provision correctly" lives in one place | Consistency, centralised expertise, evolves without touching team files |
| Transparency over abstraction | Every resource shows its full resolution chain | Debugging, audit, learning, accountability |
| Escape hatch for urgency | Whitelisted, reasoned, non-persistent overrides | Teams are never blocked; overrides feed mapping improvements |

The definition is authored and stored wherever the team chooses (own repo recommended) and **uploaded** to
the platform per deployment; the upload is the snapshot, identified by definition SHA plus image tag
([0007](../decisions/0007-definition-upload-snapshot.md), [0006](../decisions/0006-infra-deployed-with-app.md)).
The same definition deploys to every environment; the platform adjusts sizing per environment.

## Current schema (`just-deliver/v1`)

```yaml
apiVersion: just-deliver/v1
kind: Workload
metadata:
  name: just-deliver-sample-app       # ^[a-z][a-z0-9-]{1,38}[a-z0-9]$ (3-40 chars, DNS-safe)
  team: platform-team
container:                            # SCORE-compatible subset
  image: myregistry.azurecr.io/just-deliver-sample-app:1.0.0
  variables:
    LOG_LEVEL: info
    COSMOS_ENDPOINT: ${resource.cosmos-sql.endpoint}   # endpoint only; app uses its managed identity
  ports:
    - port: 8080
      protocol: TCP
requires:
  - type: cosmos-sql
```

| Field | Required | Notes |
|---|---|---|
| `apiVersion` | yes | enum `just-deliver/v1` |
| `kind` | yes | enum `Workload` |
| `metadata.name` | yes | Feeds generated resource names, so it must survive prefixes/suffixes (C14) |
| `metadata.team` | yes | Drives approval routing and cost attribution |
| `container.image` | yes | Fully qualified, including tag |
| `container.variables` | no | String map; may contain `${resource.<id>.<output>}` references |
| `container.ports[]` | no | `port` 1-65535 (required), `protocol` `TCP`/`UDP` |
| `requires[].type` | yes | Lowercase kebab-case, `^[a-z][a-z0-9-]{1,14}[a-z0-9]$`. Whether the type exists is the resolver's job (catalog), not the schema's |
| `requires[].id` | no | Same pattern as `type`; effective id defaults to `type` |
| `requires[].class` | no | Same pattern; defaults to `standard` |

Overrides (`overrides`, `override_reason`) are out of MVP scope and rejected by the schema.

`additionalProperties: false` at every level; top-level required: `apiVersion`, `kind`, `metadata`, `container`. Validation is implemented in `src/jd.definitionvalidator` (YamlDotNet with unquoted-scalar type inference so
`port: 8080` stays an integer, then NJsonSchema). Rules JSON Schema cannot express run afterwards in code
(`WorkloadRules`), only on a document that passed the schema: effective ids are unique within the workload,
and every `${resource.<id>.<output>}` in `container.variables` names a declared effective id.

### Requirements: `id` and `class` (C12, C13)

```yaml
requires:
  - type: cosmos-sql               # id defaults to "cosmos-sql", environment's shared server
  - type: cosmos-sql
    id: reporting                  # second database, same shared server
  - type: cosmos-sql
    id: ledger
    class: dedicated               # its own server: a different substrate tier
```

- **`id`** ([0011](../decisions/0011-requirement-id.md)): optional, defaults to the type name. Unique within the
  workload, validated pre-upload by the validator (two id-less requirements of one type collide and fail
  with a "set distinct ids" message). Lowercase kebab-case, 3-16 characters (`^[a-z][a-z0-9-]{1,14}[a-z0-9]$`);
  the tighter ~12-character alphanumeric budget for generated names (storage accounts cap at 24 lowercase
  alphanumerics) is still open under C14. `id` is an **identity, not a label**: renaming
  `db` to `primary` is delete + create of an empty database; `protect` (C11) and the destructive-diff
  gate (C16) make this fail safe, but it must be documented.
- **`class`**: how isolated / which operational shape. "Dedicated" means its own server (noisy neighbour,
  compliance boundary, different engine version), not a separate database — every workload already gets
  its own database on the shared server. Open (C12): which classes exist per type; whether
  `class: dedicated` in production needs approval given cost.
- Identity of a requirement is `(workload, id, type)`, never its position in the list (reorder must not
  destroy resources).

### Resource references

`${resource.<id>.<output>}`, with `id` defaulting to `<type>`, so a requirement without an id is referenced
as `${resource.<type>.<output>}`. Substituted at deployment time; some outputs (principal ids,
endpoints) only exist after earlier steps run, so references resolve in phases — see [resolver.md](resolver.md).

## What teams do not specify

| Concern | Comes from |
|---|---|
| Engine, version, SKU/size, replicas, scaling | Platform mapping (per environment) |
| Cache eviction, queue partitioning, backup retention | Platform mapping |
| Container runtime and compute size | Platform: Container Apps by default ([0013](../decisions/0013-container-apps-runtime.md)) |
| Health checks, resource limits, scaling rules | Global policy |
| Encryption, tags, network rules, security policy | Global IT Ops policies; hard guardrails in the landing zone ([0008](../decisions/0008-enforcement-in-landing-zone.md)) |
| Monitoring (Log Analytics, App Insights) | `enforce-monitoring` policy — attached to every workload, never requested |
| Identity and grants | Platform: system-assigned identity per app and job ([0012](../decisions/0012-system-assigned-identity.md)) |
| Resource names | Generated from workload name + id + type (C14 open) |
| Approvals / sign-off | Release process, defined separately so sign-off rules change without teams editing definitions |

## Overrides (escape hatch)

Use when a team hits an edge case the mapping does not cover (e.g. needs Redis 6 for Streams, mapping
defaults to 5), needs an urgent customisation that would otherwise wait for a new mapping class, or is
experimenting in dev/staging.

```yaml
requires:
  - type: cache
    overrides:
      sku: Premium_P1
      persistence: enabled
      eviction_policy: volatile-lru
    override_reason: |
      Q4 traffic spike; Basic insufficient at peak. Platform team: please add a 'high-performance' class.
```

| Rule | Detail |
|---|---|
| Whitelist | Platform defines overridable fields per type, e.g. `database: [backup_retention_days, connection_timeout]`, `cache: [sku, eviction_policy, persistence]`, `queue: [max_message_size, default_ttl]`. Protects encryption, compliance tags etc. |
| Reason | `override_reason` mandatory (to be enforced when overrides return; the MVP schema rejects overrides altogether) |
| Approval | dev: no; staging: no; production: yes (ops must approve deviations). Since the platform diffs against the previous definition, a new override is a sensitive-field change routed to the gate (0007) |
| Precedence | Overrides must **not** beat compliance policies — the old algorithm applied them after policies; see [provisioning.md](provisioning.md#precedence) |
| Non-persistence | Not carried to the next deployment unless re-specified; prevents hidden drift. A permanent need belongs in the mapping |
| Logging | `override_reason`, resource type, field, requested value, environment, deployment id, timestamp |
| Feedback loop | Weekly review, e.g. `cache.sku=Premium_P1` x5 → add `high-performance` class; `database.backup_retention_days=90` x3 → `compliance` class; one-off experiments → monitor |

Current MVP state: overrides are out of MVP scope and the schema rejects them.

## Evolution path

| Stage | Team declares | Notes |
|---|---|---|
| 1 MVP | `type` (+ `id`) | Everything else from mappings + policies; overrides for edge cases. Metric: a definition in < 5 minutes |
| 2 Classes | `type` + `class` | 3-5 ops-defined presets per type, emerging from override patterns, e.g. `standard` (cheap, dev/staging), `high-availability` (prod default), `high-performance` |
| 3 Advanced | `class` + `config` (e.g. `backup_retention_days: 90`, `failover_region: westeurope`, `read_replicas: 2`) | Only for teams that have proven they understand the implications |

**Type naming rule (C52):** the type names the *interface the code binds to*; the class names the
*operational shape the code cannot see*. `type: database` breaks the moment it resolves to an engine the
team's driver does not expect, so expect `type: postgres` / `cosmos`, `class: dedicated` — abstract SKU, HA,
backup and size, never the wire protocol.

## Pending schema changes (H50)

Decided; items 1 and 2 are applied to `workload.schema.json`, the rest are not yet:

1. ~~Remove `metadata.environment`~~ (A1, A2) — done (S01). The same argument, weaker, applies to `name`
   and `team` (registration identity that can drift if duplicated in the file).
2. ~~Add `id` and `class`~~ to `requires` items (C12, C13) — done (S01).
3. **Add a `hooks` block** (E28, E33; [0014](../decisions/0014-hook-points.md), [0015](../decisions/0015-database-migrations.md)):
   `preDeploy`, `verify`, `postDeploy`, each with `command`, optional `image` (defaults to the workload
   image for `preDeploy`), `timeout` (default 10 minutes), optional tier restriction, and `destructive` on
   `preDeploy`.
4. Possibly tighten the `metadata.name` pattern — C14's naming budget does not currently close.

Also implied but not on the H50 list: a `runtime` enum defaulting to Container Apps (E27); renaming types
per the C52 rule. Still open (H50): migration across `apiVersion`s, how many versions are supported at
once, and validation runnable **before upload** (CLI/CI step) with self-service error messages.

## SCORE evaluation (condensed)

[SCORE](https://github.com/score-spec/spec) is a platform-agnostic container workload spec (JSON Schema
2020-12): image, command/args, variables, ports, liveness/readiness probes, CPU/memory, files/volumes, and a
lightweight `resources` list with implementation-specific `params`. Strengths: simple, opinionated,
multi-runtime, RFC1123 naming validation.

| Concern | SCORE | just-deliver |
|---|---|---|
| Question answered | "How do I run this container?" | "What is provisioned, who approves, how does it deploy?" |
| Infrastructure | Reference list only | Full resource graph from team + IT Ops layers |
| Release workflow / approvals | None, stateless | Release lifecycle, gates, audit trail |
| Secrets | Out of scope | Key Vault from day one; platform-injected |
| Policy layering | Flat, per-workload | Global policies merged into team declarations |
| Executor | None | `executor` field for the B/C → D transition ([0005](../decisions/0005-build-bc-first-design-for-d.md)) |

SCORE's own gaps: no security context, no image-pull credentials, no probe timing (initialDelaySeconds/timeoutSeconds/failureThreshold), no restart/scaling policy, resource-id pattern allows consecutive hyphens, deprecated files/volumes array syntax still accepted.

**Recommendation:** not the primary model. Keep `container` as a SCORE-compatible subset (as the schema does)
and wrap it with `requires`, policy and release concerns, which remain ours.

## Feature gaps

From the original gap analysis; MVP priority as originally assessed.

| Gap | Missing | Priority | Open questions |
|---|---|---|---|
| Container runtime config | Probe timing, image-pull credentials, security context, restart/replicas, requests vs limits; security from team or policy? | Nice-to-have | E27 settles runtime; ACR pull bootstrap under D17 |
| Multi-environment promotion | Same artifact or per env, who promotes, per-env gates, rollback trigger, env-specific config | Needed | E34, E32, E35 |
| Secrets declaration & lifecycle | Explicit vs inferred secrets, B-phase value collection, rotation, access (env/file/direct KV) | Needed | D22, D23, D24 |
| Configuration management | Env-specific config, storage (KV / app config / DB), redeploy on change, versioning | Nice-to-have (env vars first) | E34 |
| Networking & exposure | Ingress, routing/hostnames, service-to-service auth, DNS, network policies; team or policy? mesh? | Defer | F41, [network.md](network.md) |
| Observability | Declared metrics/logs, dashboards, alerting ownership, UI troubleshooting | Defer | G44, B8 |
| Manifest schema | One clear format, versioning, validation feedback | Needed | H50 (schema exists) |
| Audit & compliance | Events, retention, access to sensitive records, SOC 2 reporting, all infra changes | — | F39 |
| Approval & rejection | Unanimous/majority, rejection path, resubmission, notifications, time limits, comments | Needed | F37, F38, F40 |
| Deployment strategy | Blue-green/rolling/canary, blast radius, traffic shifting, auto-rollback | Defer | E35, E32 |
