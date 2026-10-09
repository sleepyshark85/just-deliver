#!/usr/bin/env bash
# Quality gate. Runs before every commit (Claude Code hook) and in CI.
#   tools/verify.sh            format + build (warnings are errors) + unit tests + architecture + secrets
#   tools/verify.sh --azure    also run tests tagged Category=Azure (real resources, free tier only); one run at a time
set -euo pipefail
cd "$(dirname "$0")/.."

SLN=just-deliver.slnx
FILTER="Category!=Azure"

step() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
fail() { printf '\033[31mverify failed:\033[0m %s\n' "$*" >&2; exit 1; }

if [[ "${1:-}" == "--azure" ]]; then
  FILTER=""
  # Azure allows one free-tier Cosmos account per subscription, so two Azure runs at once would collide. The lock is held by this
  # script until it exits; the dotnet commands below close the descriptor so a lingering build server cannot keep it.
  exec 9>"${XDG_RUNTIME_DIR:-/tmp}/just-deliver-azure.lock"
  flock -n 9 || fail "another Azure test run is in progress (lock ${XDG_RUNTIME_DIR:-/tmp}/just-deliver-azure.lock); wait for it to finish"
  # The Azure tests refuse to run without this, so a plain 'dotnet test' cannot reach Azure by accident.
  export JD_AZURE_TESTS=1
fi

step "Format"
dotnet format "$SLN" --verify-no-changes -v q 9>&- || fail "run 'dotnet format $SLN' and commit the result"

step "Build"
dotnet build "$SLN" -nologo -v q 9>&-

step "Tests (${FILTER:-all})"
dotnet test "$SLN" --no-build -nologo -v q ${FILTER:+--filter "$FILTER"} 9>&-

step "Architecture boundaries"
# Inner projects hold domain, resolution and orchestration logic: they must not depend on Pulumi, Azure SDKs or the backend providers.
inner=(src/jd.core/jd.core.csproj)
[[ -f src/jd.resolver/jd.resolver.csproj ]] && inner+=(src/jd.resolver/jd.resolver.csproj)
[[ -f src/jd.orchestrator/jd.orchestrator.csproj ]] && inner+=(src/jd.orchestrator/jd.orchestrator.csproj)
for p in "${inner[@]}"; do
  if grep -E -q 'Include="(Pulumi|Azure)[^"]*"|backend-providers' "$p"; then
    fail "$p must not reference Pulumi, Azure SDKs or backend providers (see docs/engineering/standards.md)"
  fi
done
echo "ok"

step "Secrets"
if git ls-files | grep -E -q '(^|/)[^/]*\.env$|(^|/)\.pulumi/'; then
  fail "tracked .env or .pulumi state files found"
fi
echo "ok"
