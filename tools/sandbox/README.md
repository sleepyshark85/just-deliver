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
| Policy initiative `jd-sandbox-guardrails` (Deny) | Allowed locations (resources and resource groups) = `--region`; not-allowed resource types from [config/not-allowed-resource-types.json](config/not-allowed-resource-types.json); Cosmos DB accounts must enable free tier (which also caps it at one account); Container Apps environments may use Consumption profiles only; Log Analytics workspaces must set a daily cap between 0.01 and `--law-daily-cap` GB, written as a decimal (Azure Policy compares numbers of the same type only, so an integer cap fails evaluation and is denied) |
| Budget `jd-sandbox-budget` | `--budget` USD/month; email at 50% and 100% actual, 100% forecast. Alerts only — it does not stop spend |
| App registration + service principal | **Contributor** on the subscription, plus **Role Based Access Control Administrator** restricted by an ABAC condition to assigning only `--grantable-roles` (default: Monitoring Metrics Publisher). Neither role can modify policy, so the guardrails hold against the team |
| Credentials file | `~/.just-deliver/<subscription>.env` (mode 600): `ARM_*` for Pulumi, `AZURE_*` for `DefaultAzureCredential`, `JD_REGION`, `PULUMI_CONFIG_PASSPHRASE`. Secret lifetime `--secret-days` (30); renew with `--rotate-secret` |

## Team usage

```bash
source ~/.just-deliver/<subscription>.env
# --password= form: generated secrets may start with "-"
az login --service-principal --username "$ARM_CLIENT_ID" --password="$ARM_CLIENT_SECRET" --tenant "$ARM_TENANT_ID"
```

## Free-tier constraints the team must design for

Verify figures on Azure's pricing pages; grants change.

| Service | Free allowance | Design consequence |
|---|---|---|
| Cosmos DB | 1,000 RU/s + 25 GB on one free-tier account, provisioned throughput only (no serverless) | One shared account; total RU/s across all databases/containers ≤ 1,000 |
| Container Apps (Consumption) | Monthly per-subscription grant (~180k vCPU-s, 360k GiB-s, 2M requests) | `minReplicas: 0`, smallest CPU/memory size; inactive revisions scale to zero |
| Log Analytics / App Insights | ~5 GB ingestion/month, 31-day retention | Daily cap enforced by policy; sample telemetry |
| Container registry | ACR is not free (and denied) | Use a public registry (GHCR / Docker Hub) |

Check Cost Management after the first day of real deployments. The guardrails are a safety net,
not a guarantee: a Pay-As-You-Go subscription has no spending limit.

## Adding a subscription later

Run `check`, then `apply` with the new `--subscription`. Everything is parameterised; nothing in the
policies is subscription-specific. To allow the team to grant another role (e.g. `AcrPull`), re-run
`apply` with `--grantable-roles <guid1>,<guid2>`.
