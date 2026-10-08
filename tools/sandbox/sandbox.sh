#!/usr/bin/env bash
# Prepare an Azure subscription as a free-tier sandbox for the just-deliver MVP team.
#
#   sandbox.sh check   --subscription <id>                  read-only status report
#   sandbox.sh apply   --subscription <id> --email <addr>   create/update everything (idempotent)
#   sandbox.sh destroy --subscription <id> --yes            remove what apply created
#
# Run as a user who is Owner on the subscription and can create app registrations.
# See README.md for what is created and why.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ---- defaults -------------------------------------------------------------
REGION="southeastasia"
BUDGET_AMOUNT="5"
EMAIL=""
SUBSCRIPTION=""
SP_NAME=""
SECRET_DAYS="30"
ENV_FILE=""
LAW_DAILY_CAP_GB="0.15"
ROTATE_SECRET="false"
ASSUME_YES="false"
# Roles the sandbox identity may assign to managed identities (built-in role GUIDs).
# Monitoring Metrics Publisher: App Insights ingestion with Entra auth.
GRANTABLE_ROLES="3913510d-42f4-4e42-8a64-420c390055eb"

PROVIDERS=(Microsoft.App Microsoft.OperationalInsights Microsoft.Insights Microsoft.DocumentDB Microsoft.ManagedIdentity)

PREFIX="jd-sandbox"
INITIATIVE="${PREFIX}-guardrails"
ASSIGNMENT="${PREFIX}-guardrails"
BUDGET_NAME="${PREFIX}-budget"
CUSTOM_POLICIES=(cosmos-free-tier aca-consumption-only law-daily-cap)

ROLE_CONTRIBUTOR="b24988ac-6180-42a0-ab88-20f7382dd24c"
ROLE_RBAC_ADMIN="f58310d9-a9f6-439a-9e8d-f62e7b41a168"
POLICY_ALLOWED_LOCATIONS="e56962a6-4747-49cd-b67b-bf8b01975c4c"
POLICY_ALLOWED_RG_LOCATIONS="e765b5de-1225-4ba3-bd56-1ac6695af988"
POLICY_NOT_ALLOWED_TYPES="6c112d4e-5bc7-47ae-a041-ea2d9dccd749"

# ---- helpers --------------------------------------------------------------
log()  { printf '\033[1m==>\033[0m %s\n' "$*"; }
ok()   { printf '  \033[32mok\033[0m      %s\n' "$*"; }
miss() { printf '  \033[33mmissing\033[0m %s\n' "$*"; }
die()  { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }

usage() { sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; cat <<EOF

Options:
  --subscription <id>     target subscription (required)
  --email <addr>          budget alert recipient (required for apply)
  --region <name>         only allowed region            (default: $REGION)
  --budget <usd>          monthly budget amount          (default: $BUDGET_AMOUNT)
  --sp-name <name>        app registration display name  (default: $PREFIX-<sub prefix>)
  --secret-days <n>       client secret lifetime         (default: $SECRET_DAYS)
  --env-file <path>       credentials output file        (default: ~/.just-deliver/<sub>.env)
  --law-daily-cap <gb>    max Log Analytics daily cap    (default: $LAW_DAILY_CAP_GB)
  --grantable-roles <ids> comma-separated role GUIDs the sandbox identity may assign
  --rotate-secret         issue a new client secret even if the env file exists
  --yes                   confirm destroy
EOF
}

parse_args() {
  CMD="${1:-}"; [[ -n "$CMD" ]] || { usage; exit 1; }
  shift
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --subscription)    SUBSCRIPTION="$2"; shift 2 ;;
      --email)           EMAIL="$2"; shift 2 ;;
      --region)          REGION="$2"; shift 2 ;;
      --budget)          BUDGET_AMOUNT="$2"; shift 2 ;;
      --sp-name)         SP_NAME="$2"; shift 2 ;;
      --secret-days)     SECRET_DAYS="$2"; shift 2 ;;
      --env-file)        ENV_FILE="$2"; shift 2 ;;
      --law-daily-cap)   LAW_DAILY_CAP_GB="$2"; shift 2 ;;
      --grantable-roles) GRANTABLE_ROLES="$2"; shift 2 ;;
      --rotate-secret)   ROTATE_SECRET="true"; shift ;;
      --yes)             ASSUME_YES="true"; shift ;;
      -h|--help)         usage; exit 0 ;;
      *) die "unknown option: $1" ;;
    esac
  done
  [[ -n "$SUBSCRIPTION" ]] || die "--subscription is required"
  SCOPE="/subscriptions/$SUBSCRIPTION"
  SP_NAME="${SP_NAME:-$PREFIX-${SUBSCRIPTION:0:8}}"
  ENV_FILE="${ENV_FILE:-$HOME/.just-deliver/$SUBSCRIPTION.env}"
}

preflight() {
  command -v az >/dev/null || die "az CLI not found"
  command -v python3 >/dev/null || die "python3 not found"
  az account show >/dev/null 2>&1 || die "not logged in: run 'az login'"
  az account set --subscription "$SUBSCRIPTION" || die "cannot select subscription $SUBSCRIPTION"
  TENANT_ID="$(az account show --query tenantId -o tsv)"
  SUB_NAME="$(az account show --query name -o tsv)"
}

app_id()  { az ad app list --display-name "$SP_NAME" --query "[0].appId" -o tsv 2>/dev/null; }
sp_oid()  { az ad sp show --id "$1" --query id -o tsv 2>/dev/null || true; }

# ABAC condition: the identity may only create/delete assignments of GRANTABLE_ROLES.
rbac_condition() {
  local guids="${GRANTABLE_ROLES//,/, }"
  printf "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {%s})) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {%s}))" "$guids" "$guids"
}

role_assignment_ids() { # <principal oid> <role guid>
  az role assignment list --assignee "$1" --scope "$SCOPE" \
    --query "[?ends_with(roleDefinitionId, '$2')].id" -o tsv 2>/dev/null
}

initiative_definitions() {
  python3 -I - "$SCOPE" "$POLICY_ALLOWED_LOCATIONS" "$POLICY_ALLOWED_RG_LOCATIONS" "$POLICY_NOT_ALLOWED_TYPES" "$PREFIX" "${CUSTOM_POLICIES[@]}" <<'PY'
import json, sys
scope, loc, rgloc, types, prefix, *custom = sys.argv[1:]
builtin = lambda g: f"/providers/Microsoft.Authorization/policyDefinitions/{g}"
defs = [
  {"policyDefinitionReferenceId": "allowedLocations", "policyDefinitionId": builtin(loc),
   "parameters": {"listOfAllowedLocations": {"value": "[parameters('allowedLocations')]"}}},
  {"policyDefinitionReferenceId": "allowedResourceGroupLocations", "policyDefinitionId": builtin(rgloc),
   "parameters": {"listOfAllowedLocations": {"value": "[parameters('allowedLocations')]"}}},
  {"policyDefinitionReferenceId": "notAllowedResourceTypes", "policyDefinitionId": builtin(types),
   "parameters": {"listOfResourceTypesNotAllowed": {"value": "[parameters('notAllowedResourceTypes')]"}}},
]
for name in custom:
  ref = {"policyDefinitionReferenceId": name,
         "policyDefinitionId": f"{scope}/providers/Microsoft.Authorization/policyDefinitions/{prefix}-{name}"}
  if name == "law-daily-cap":
    ref["parameters"] = {"maxDailyQuotaGb": {"value": "[parameters('maxDailyQuotaGb')]"}}
  defs.append(ref)
print(json.dumps(defs))
PY
}

INITIATIVE_PARAMS='{
  "allowedLocations":        {"type": "Array", "metadata": {"displayName": "Allowed locations"}},
  "notAllowedResourceTypes": {"type": "Array", "metadata": {"displayName": "Not allowed resource types"}},
  "maxDailyQuotaGb":         {"type": "Float", "metadata": {"displayName": "Max Log Analytics daily cap (GB)"}}
}'

# ---- check ----------------------------------------------------------------
cmd_check() {
  log "Subscription $SUB_NAME ($SUBSCRIPTION)"
  local offer; offer="$(az rest --method get --url "https://management.azure.com$SCOPE?api-version=2022-12-01" \
    --query "[subscriptionPolicies.quotaId, subscriptionPolicies.spendingLimit]" -o tsv | tr '\n' ' ')"
  echo "  offer/spending limit: $offer"

  log "Resource providers"
  for p in "${PROVIDERS[@]}"; do
    local s; s="$(az provider show -n "$p" --query registrationState -o tsv)"
    [[ "$s" == "Registered" ]] && ok "$p" || miss "$p ($s)"
  done

  log "Guardrails"
  for name in "${CUSTOM_POLICIES[@]}"; do
    az policy definition show -n "$PREFIX-$name" >/dev/null 2>&1 && ok "definition $PREFIX-$name" || miss "definition $PREFIX-$name"
  done
  az policy set-definition show -n "$INITIATIVE" >/dev/null 2>&1 && ok "initiative $INITIATIVE" || miss "initiative $INITIATIVE"
  local a; a="$(az policy assignment show -n "$ASSIGNMENT" --scope "$SCOPE" \
    --query "join(', ', parameters.allowedLocations.value)" -o tsv 2>/dev/null || true)"
  [[ -n "$a" ]] && ok "assignment $ASSIGNMENT (allowed: $a)" || miss "assignment $ASSIGNMENT"

  log "Budget"
  local b; b="$(az rest --method get --url "https://management.azure.com$SCOPE/providers/Microsoft.Consumption/budgets/$BUDGET_NAME?api-version=2023-05-01" \
    --query "[properties.amount, properties.notifications.actual100.contactEmails[0]]" -o tsv 2>/dev/null | tr '\n' ' ' || true)"
  [[ -n "$b" ]] && ok "$BUDGET_NAME: $b" || miss "$BUDGET_NAME"

  log "Sandbox identity"
  local id; id="$(app_id)"
  if [[ -z "$id" ]]; then miss "app registration $SP_NAME"; else
    ok "app registration $SP_NAME ($id)"
    local exp; exp="$(az ad app credential list --id "$id" --query "[].endDateTime" -o tsv | sort | tail -1)"
    echo "  secret expires: ${exp:-none}"
    local oid; oid="$(sp_oid "$id")"
    [[ -n "$oid" ]] && ok "service principal" || miss "service principal"
    if [[ -n "$oid" ]]; then
      [[ -n "$(role_assignment_ids "$oid" "$ROLE_CONTRIBUTOR")" ]] && ok "Contributor" || miss "Contributor"
      [[ -n "$(role_assignment_ids "$oid" "$ROLE_RBAC_ADMIN")" ]] && ok "RBAC Administrator (conditional)" || miss "RBAC Administrator (conditional)"
    fi
  fi
  [[ -f "$ENV_FILE" ]] && ok "env file $ENV_FILE" || miss "env file $ENV_FILE"
}

# ---- apply ----------------------------------------------------------------
cmd_apply() {
  [[ -n "$EMAIL" ]] || die "--email is required for apply"

  log "Registering resource providers"
  for p in "${PROVIDERS[@]}"; do az provider register --namespace "$p" --wait >/dev/null; ok "$p"; done

  log "Policy definitions"
  for name in "${CUSTOM_POLICIES[@]}"; do
    local params=()
    [[ -f "$HERE/policies/$name.params.json" ]] && params=(--params "$HERE/policies/$name.params.json")
    az policy definition create -n "$PREFIX-$name" --display-name "just-deliver sandbox: $name" \
      --mode Indexed --rules "$HERE/policies/$name.json" "${params[@]}" \
      --metadata category=just-deliver-sandbox -o none
    ok "$PREFIX-$name"
  done

  log "Guardrail initiative"
  az policy set-definition create -n "$INITIATIVE" --display-name "just-deliver sandbox guardrails" \
    --definitions "$(initiative_definitions)" --params "$INITIATIVE_PARAMS" \
    --metadata category=just-deliver-sandbox -o none
  ok "$INITIATIVE"

  local assignment_params
  assignment_params="$(python3 -I -c 'import json,sys; print(json.dumps({
    "allowedLocations": {"value": [sys.argv[1]]},
    "notAllowedResourceTypes": {"value": json.load(open(sys.argv[2]))},
    "maxDailyQuotaGb": {"value": float(sys.argv[3])}}))' \
    "$REGION" "$HERE/config/not-allowed-resource-types.json" "$LAW_DAILY_CAP_GB")"
  az policy assignment create -n "$ASSIGNMENT" --display-name "just-deliver sandbox guardrails" \
    --scope "$SCOPE" --policy-set-definition "$INITIATIVE" --params "$assignment_params" -o none
  if [[ -z "$(az policy assignment non-compliance-message list -n "$ASSIGNMENT" --scope "$SCOPE" -o tsv 2>/dev/null)" ]]; then
    az policy assignment non-compliance-message create -n "$ASSIGNMENT" --scope "$SCOPE" -o none \
      --message "Blocked by just-deliver sandbox guardrails (free tier only, region $REGION). See tools/sandbox/README.md."
  fi
  ok "assignment $ASSIGNMENT (region $REGION, LAW cap ${LAW_DAILY_CAP_GB} GB/day)"

  log "Budget"
  local url="https://management.azure.com$SCOPE/providers/Microsoft.Consumption/budgets/$BUDGET_NAME?api-version=2023-05-01"
  local existing; existing="$(az rest --method get --url "$url" -o json 2>/dev/null || true)"
  [[ -n "$existing" ]] || existing="{}"
  local body; body="$(python3 -I - "$BUDGET_AMOUNT" "$EMAIL" "$existing" <<'PY'
import json, sys, datetime
amount, email, existing = float(sys.argv[1]), sys.argv[2], json.loads(sys.argv[3] or "{}")
start = existing.get("properties", {}).get("timePeriod", {}).get("startDate") \
        or datetime.date.today().replace(day=1).isoformat() + "T00:00:00Z"
n = lambda t, kind: {"enabled": True, "operator": "GreaterThanOrEqualTo", "threshold": t,
                     "thresholdType": kind, "contactEmails": [email]}
body = {"properties": {"category": "Cost", "amount": amount, "timeGrain": "Monthly",
        "timePeriod": {"startDate": start},
        "notifications": {"actual50": n(50, "Actual"), "actual100": n(100, "Actual"),
                          "forecast100": n(100, "Forecasted")}}}
if existing.get("eTag"): body["eTag"] = existing["eTag"]
print(json.dumps(body))
PY
)"
  az rest --method put --url "$url" --body "$body" -o none
  ok "$BUDGET_NAME: \$$BUDGET_AMOUNT/month, alerts at 50%/100% actual and 100% forecast to $EMAIL"

  log "Sandbox identity"
  local id; id="$(app_id)"
  if [[ -z "$id" ]]; then
    id="$(az ad app create --display-name "$SP_NAME" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
    ok "created app registration $SP_NAME ($id)"
  else ok "app registration $SP_NAME ($id)"; fi
  local oid; oid="$(sp_oid "$id")"
  if [[ -z "$oid" ]]; then oid="$(az ad sp create --id "$id" --query id -o tsv)"; ok "created service principal"; fi

  if [[ -z "$(role_assignment_ids "$oid" "$ROLE_CONTRIBUTOR")" ]]; then
    az role assignment create --assignee-object-id "$oid" --assignee-principal-type ServicePrincipal \
      --role "$ROLE_CONTRIBUTOR" --scope "$SCOPE" -o none
  fi
  ok "Contributor on subscription"

  local cond; cond="$(rbac_condition)"
  local current; current="$(az role assignment list --assignee "$oid" --scope "$SCOPE" \
    --query "[?ends_with(roleDefinitionId, '$ROLE_RBAC_ADMIN')].condition | [0]" -o tsv 2>/dev/null || true)"
  if [[ "$current" != "$cond" ]]; then
    for rid in $(role_assignment_ids "$oid" "$ROLE_RBAC_ADMIN"); do az role assignment delete --ids "$rid"; done
    az role assignment create --assignee-object-id "$oid" --assignee-principal-type ServicePrincipal \
      --role "$ROLE_RBAC_ADMIN" --scope "$SCOPE" --condition "$cond" --condition-version 2.0 \
      --description "just-deliver sandbox: may only assign roles $GRANTABLE_ROLES" -o none
  fi
  ok "RBAC Administrator, restricted to roles: $GRANTABLE_ROLES"

  if [[ ! -f "$ENV_FILE" || "$ROTATE_SECRET" == "true" ]] || ! grep -q "ARM_CLIENT_ID=$id" "$ENV_FILE"; then
    local end; end="$(date -u -d "+$SECRET_DAYS days" +%Y-%m-%dT%H:%M:%SZ)"
    local secret; secret="$(az ad app credential reset --id "$id" --end-date "$end" \
      --display-name "$PREFIX" --query password -o tsv 2>/dev/null)"
    mkdir -p "$(dirname "$ENV_FILE")"; chmod 700 "$(dirname "$ENV_FILE")"
    ( umask 077; cat >"$ENV_FILE" <<EOF
# just-deliver sandbox credentials for $SUB_NAME — secret expires $end. Do not commit.
export ARM_TENANT_ID=$TENANT_ID
export ARM_SUBSCRIPTION_ID=$SUBSCRIPTION
export ARM_CLIENT_ID=$id
export ARM_CLIENT_SECRET='$secret'
export AZURE_TENANT_ID=\$ARM_TENANT_ID
export AZURE_SUBSCRIPTION_ID=\$ARM_SUBSCRIPTION_ID
export AZURE_CLIENT_ID=\$ARM_CLIENT_ID
export AZURE_CLIENT_SECRET=\$ARM_CLIENT_SECRET
export JD_REGION=$REGION
export PULUMI_CONFIG_PASSPHRASE=
EOF
    )
    ok "wrote $ENV_FILE (secret expires $end)"
  else
    ok "kept existing credentials in $ENV_FILE (use --rotate-secret to renew)"
  fi

  log "Done. Team usage: source $ENV_FILE && az login --service-principal --username \$ARM_CLIENT_ID --password=\$ARM_CLIENT_SECRET --tenant \$ARM_TENANT_ID"
}

# ---- destroy --------------------------------------------------------------
cmd_destroy() {
  [[ "$ASSUME_YES" == "true" ]] || die "destroy removes guardrails, budget and the sandbox identity; re-run with --yes"
  log "Removing sandbox identity"
  local id; id="$(app_id)"
  if [[ -n "$id" ]]; then
    local oid; oid="$(sp_oid "$id")"
    if [[ -n "$oid" ]]; then
      for rid in $(az role assignment list --assignee "$oid" --all --query "[].id" -o tsv); do az role assignment delete --ids "$rid"; done
    fi
    az ad app delete --id "$id"; ok "deleted $SP_NAME"
  fi
  rm -f "$ENV_FILE"
  log "Removing budget"
  az rest --method delete --url "https://management.azure.com$SCOPE/providers/Microsoft.Consumption/budgets/$BUDGET_NAME?api-version=2023-05-01" -o none 2>/dev/null || true
  log "Removing guardrails"
  az policy assignment delete -n "$ASSIGNMENT" --scope "$SCOPE" 2>/dev/null || true
  az policy set-definition delete -n "$INITIATIVE" 2>/dev/null || true
  for name in "${CUSTOM_POLICIES[@]}"; do az policy definition delete -n "$PREFIX-$name" 2>/dev/null || true; done
  log "Done. Resource providers are left registered; workload resources are not touched."
}

parse_args "$@"
preflight
case "$CMD" in
  check)   cmd_check ;;
  apply)   cmd_apply ;;
  destroy) cmd_destroy ;;
  *) usage; exit 1 ;;
esac
