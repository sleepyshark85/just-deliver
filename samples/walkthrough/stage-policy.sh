#!/usr/bin/env bash
# Writes a copy of the stage descriptor with another approval policy (scenarios 7 and 8); the substrate values stay as they are.
#   samples/walkthrough/stage-policy.sh <stage.env.yaml> <out.env.yaml> <required> <separateFromDeployer: true|false> <approver object id>...
# The approval block is the last block of a descriptor `jd env up` writes; qualifiedOn is kept.
set -euo pipefail

if [[ $# -lt 5 ]]; then
  echo "usage: $0 <stage.env.yaml> <out.env.yaml> <required> <true|false> <approver object id>..." >&2
  exit 2
fi

in=$1 out=$2 required=$3 separate=$4
shift 4
qualified_on=$(sed -n 's/^  qualifiedOn: *//p' "$in")
if [[ -z $qualified_on ]]; then
  echo "$in has no approval block" >&2
  exit 1
fi

{
  sed '/^approval:/,$d' "$in"
  echo "approval:"
  echo "  qualifiedOn: $qualified_on"
  echo "  required: $required"
  echo "  approvers:"
  for approver in "$@"; do echo "  - $approver"; done
  echo "  separateFromDeployer: $separate"
} > "$out"
echo "$out"
