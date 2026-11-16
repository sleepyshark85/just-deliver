# Sandbox subscription setup

Prepares an Azure subscription so a team can build and test the just-deliver MVP on the free tier,
with guardrails the team's own identity cannot remove.

```bash
./sandbox.sh check   --subscription <id>                       # read-only status
./sandbox.sh apply   --subscription <id> --email <alerts@addr>  # idempotent, safe to re-run
./sandbox.sh destroy --subscription <id> --yes                  # remove guardrails, budget, identity
```

Run as a user who is **Owner** on the subscription and can create app registrations. Requires `az`
and `python3`. Options: `./sandbox.sh --help`.

## What `apply` creates

| Item | Detail |
|---|---|
| Resource providers | `Microsoft.App`, `Microsoft.OperationalInsights`, `Microsoft.Insights`, `Microsoft.DocumentDB`, `Microsoft.ManagedIdentity` |
| Policy initiative `jd-sandbox-guardrails` (Deny) | The guardrails, at subscription scope; what they enforce: [below](#what-the-guardrails-enforce) |
| Budget `jd-sandbox-budget` | `--budget` USD/month; email at 50% and 100% actual, 100% forecast. Alerts only — it does not stop spend |
| App registration + service principal | **Contributor** on the subscription, plus **Role Based Access Control Administrator** restricted by an ABAC condition to assigning only `--grantable-roles` (default: Monitoring Metrics Publisher). Neither role can modify policy, so the guardrails hold against the team |
| Credentials file | `~/.just-deliver/<subscription>.env` (mode 600): `ARM_*` for Pulumi, `AZURE_*` for `DefaultAzureCredential` (jd needs nothing else: the region is in the environment definition, state and records live in jd's data directory). Secret lifetime `--secret-days` (30); renew with `--rotate-secret` |

## Team usage

```bash
source ~/.just-deliver/<subscription>.env
# --password= form: generated secrets may start with "-"
az login --service-principal --username "$ARM_CLIENT_ID" --password="$ARM_CLIENT_SECRET" --tenant "$ARM_TENANT_ID"
```

The Azure tests run as this identity; their rules are in [standards §6](../../docs/engineering/standards.md#6-azure-and-the-free-tier).
They need a subscription with no existing free-tier Cosmos account (one is the limit; see standards §6).

## What the guardrails enforce

The free-tier rules a slice must keep are [standards §6](../../docs/engineering/standards.md#6-azure-and-the-free-tier). `apply`
enforces only this much of them (Deny, at subscription scope):

| Service | Enforced by `apply` |
|---|---|
| Cosmos DB | Free tier on, so at most one account per subscription |
| Container Apps | Consumption workload profiles only |
| Log Analytics | Daily cap set, between 0.01 GB and `--law-daily-cap` (default 0.15 GB), written as a decimal (Azure Policy compares numbers of the same type only, so an integer cap fails evaluation and is denied) |
| Container registry, VMs, AKS, SQL, Postgres, Redis, gateways, … | Denied ([not-allowed-resource-types.json](config/not-allowed-resource-types.json)) |
| Region | `--region` only, for resources and resource groups |

RU/s totals, replica counts, CPU/memory sizes and sampling are not enforced here. The guardrails and the budget are a safety net,
not a guarantee: a Pay-As-You-Go subscription has no spending limit; check Cost Management after the first day of real deployments.

## Adding a subscription later

Run `check`, then `apply` with the new `--subscription`. Everything is parameterised; nothing in the
policies is subscription-specific. To allow the team to grant another role (e.g. `AcrPull`), re-run
`apply` with `--grantable-roles <guid1>,<guid2>`.
