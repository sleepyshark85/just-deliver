#!/usr/bin/env bash
# Delete test resource groups tagged project=just-deliver-mvp, the diagnostic settings of their resources first (a group delete leaves
# them behind).
#   tools/azure/cleanup.sh                  list what would be deleted
#   tools/azure/cleanup.sh --yes            delete (waits for completion)
#   tools/azure/cleanup.sh --yes --all      also delete groups tagged tier=substrate
set -euo pipefail

YES=false; ALL=false
for a in "$@"; do
  case "$a" in
    --yes) YES=true ;;
    --all) ALL=true ;;
    *) echo "unknown option: $a" >&2; exit 1 ;;
  esac
done

query="[?tags.project=='just-deliver-mvp'"
$ALL || query+=" && tags.tier!='substrate'"
query+="].name"

# An assignment, so a failed listing stops the script instead of reading as nothing to clean up.
names=$(az group list --query "$query" -o tsv)
if [[ -z $names ]]; then echo "nothing to clean up"; exit 0; fi
mapfile -t groups <<<"$names"

printf '%s\n' "${groups[@]}"
$YES || { echo "(dry run — re-run with --yes to delete)"; exit 0; }
"$(dirname "$0")/delete-diagnostic-settings.sh" "${groups[@]}"
for g in "${groups[@]}"; do az group delete -n "$g" --yes --no-wait; echo "deleting $g"; done
for g in "${groups[@]}"; do az group wait -n "$g" --deleted; done
echo "done"
