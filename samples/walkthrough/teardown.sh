#!/usr/bin/env bash
# Deletes what the walkthrough created, in the order the Azure test uses: the environment groups in parallel (their Container Apps
# environments log to the shared workspace), then the shared group; then lists what is left (nothing, when all is gone).
#   samples/walkthrough/teardown.sh <dev.env.yaml> <stage.env.yaml>
# The groups are read from the descriptors: values.resourceGroup of each, and values.shared.resourceGroup (the shared layer's).
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <environment descriptor>..." >&2
  exit 2
fi

shared=$(sed -n 's/^    resourceGroup: *//p' "$1")
groups=()
for descriptor in "$@"; do
  groups+=("$(sed -n 's/^  resourceGroup: *//p' "$descriptor")")
done

for group in "${groups[@]}"; do az group delete --name "$group" --yes --no-wait; echo "deleting $group"; done
for group in "${groups[@]}"; do az group wait --name "$group" --deleted --timeout 3600; done
echo "deleting $shared"
az group delete --name "$shared" --yes
az group list --query "[?tags.project=='just-deliver-mvp'].name" -o tsv
