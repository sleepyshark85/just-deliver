#!/usr/bin/env bash
# Deletes every diagnostic setting on every resource in the given resource groups; run it before deleting a group. Deleting a group
# does not delete the diagnostic settings of its resources, and a resource created again under the same id (names come from
# naming.yaml) then collides with its old setting. A resource type that has no diagnostic settings answers ResourceTypeNotSupported
# and is skipped; any other failure stops the script.
#   tools/azure/delete-diagnostic-settings.sh <resource group>...
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <resource group>..." >&2
  exit 2
fi

error=$(mktemp)
trap 'rm -f "$error"' EXIT

for group in "$@"; do
  ids=$(az resource list --resource-group "$group" --query "[].id" -o tsv)
  # One per line: a setting name may hold a space.
  while IFS= read -r id; do
    [[ -n $id ]] || continue
    if ! names=$(az monitor diagnostic-settings list --resource "$id" --query "[].name" -o tsv 2>"$error"); then
      grep -q ResourceTypeNotSupported "$error" || { cat "$error" >&2; exit 1; }
      continue
    fi
    while IFS= read -r name; do
      [[ -n $name ]] || continue
      az monitor diagnostic-settings delete --resource "$id" --name "$name"
      echo "deleted diagnostic setting $name of $id"
    done <<<"$names"
  done <<<"$ids"
done
