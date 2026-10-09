#!/usr/bin/env bash
# Build the sample app image and push it to GHCR as ghcr.io/<owner>/just-deliver-sample-app:<git short sha>,
# then print the image reference and digest. Uses the existing `gh` login (needs the write:packages scope);
# the token goes to docker over stdin and is never printed. The owner is the logged-in GitHub user.
set -euo pipefail
cd "$(dirname "$0")/../.."

owner=$(gh api user --jq .login | tr '[:upper:]' '[:lower:]')
image="ghcr.io/${owner}/just-deliver-sample-app"
tag=$(git rev-parse --short HEAD)

gh auth token | docker login ghcr.io --username "$owner" --password-stdin >/dev/null

docker build --platform linux/amd64 -f samples/sample-app/Dockerfile -t "${image}:${tag}" .
docker push "${image}:${tag}" >/dev/null

digest=$(docker inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "${image}:${tag}" | grep "^${image}@" | head -n1)
echo "image:  ${image}:${tag}"
echo "digest: ${digest}"
