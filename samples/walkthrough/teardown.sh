#!/usr/bin/env bash
# Deletes what the walkthrough created, in the order the Azure test uses: first the diagnostic settings of every group's resources
# (a group delete leaves them behind, and the next env up would collide with them), then the environment groups in parallel (their
# Container Apps environments log to the shared workspace), then the shared group; then lists what is left (nothing, when all is gone).
#   samples/walkthrough/teardown.sh <dev.env.yaml> <stage.env.yaml>
# The groups are read from the descriptors: values.env-group.name of each, and values.shared-group.name (the shared layer's).
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <environment descriptor>..." >&2
  exit 2
fi

shared=$(sed -n '/^  shared-group:$/,/^  [^ ]/s/^    name: *//p' "$1")
groups=()
for descriptor in "$@"; do
  groups+=("$(sed -n '/^  env-group:$/,/^  [^ ]/s/^    name: *//p' "$descriptor")")
done

for group in "$shared" "${groups[@]}"; do
  if [[ -z $group ]]; then
    echo "a descriptor has no env-group or shared-group name; nothing deleted" >&2
    exit 1
  fi
done

"$(dirname "$0")/../../tools/azure/delete-diagnostic-settings.sh" "${groups[@]}" "$shared"
for group in "${groups[@]}"; do az group delete --name "$group" --yes --no-wait; echo "deleting $group"; done
for group in "${groups[@]}"; do az group wait --name "$group" --deleted --timeout 3600; done
echo "deleting $shared"
az group delete --name "$shared" --yes
az group list --query "[?tags.project=='just-deliver-mvp'].name" -o tsv
