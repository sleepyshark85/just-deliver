#!/usr/bin/env bash
# Build the sample app image and push it to GHCR as ghcr.io/<owner>/just-deliver-sample-app:<git short sha>,
# then print the image reference and digest. Uses the existing `gh` login (needs the write:packages scope);
# the token goes to docker over stdin and is never printed. The owner is the logged-in GitHub user.
# Refuses a dirty tree, so the sha tag always names exactly what was built.
set -euo pipefail
cd "$(dirname "$0")/../.."

if ! git diff --quiet HEAD || [[ -n "$(git ls-files --others --exclude-standard samples/sample-app)" ]]; then
  echo "publish: uncommitted changes - commit them first so the tag matches the image" >&2
  exit 1
fi

owner=$(gh api user --jq .login | tr '[:upper:]' '[:lower:]')
image="ghcr.io/${owner}/just-deliver-sample-app"
tag=$(git rev-parse --short HEAD)

trap 'docker logout ghcr.io >/dev/null 2>&1 || true' EXIT
gh auth token | docker login ghcr.io --username "$owner" --password-stdin >/dev/null

docker build --platform linux/amd64 --provenance=false -f samples/sample-app/Dockerfile -t "${image}:${tag}" .
push_output=$(docker push "${image}:${tag}")

# The registry's digest, from the push itself ("<tag>: digest: sha256:... size: ...").
digest=$(grep -Eo 'digest: sha256:[0-9a-f]{64}' <<<"$push_output" | head -n1 | cut -d' ' -f2 || true)
if [[ -z "$digest" ]]; then
  echo "publish: docker push did not report a digest" >&2
  exit 1
fi
echo "image:  ${image}:${tag}"
echo "digest: ${image}@${digest}"
