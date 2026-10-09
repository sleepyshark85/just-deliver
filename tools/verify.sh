#!/usr/bin/env bash
# Quality gate. Runs before every commit (Claude Code hook) and in CI.
#   tools/verify.sh            format + build (warnings are errors) + unit tests + architecture + secrets
#   tools/verify.sh --azure    also run tests tagged Category=Azure (real resources, free tier only)
set -euo pipefail
cd "$(dirname "$0")/.."

SLN=just-deliver.slnx
FILTER="Category!=Azure"
[[ "${1:-}" == "--azure" ]] && FILTER=""

step() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
fail() { printf '\033[31mverify failed:\033[0m %s\n' "$*" >&2; exit 1; }

step "Format"
dotnet format "$SLN" --verify-no-changes -v q || fail "run 'dotnet format $SLN' and commit the result"

step "Build"
dotnet build "$SLN" -nologo -v q

step "Tests (${FILTER:-all})"
dotnet test "$SLN" --no-build -nologo -v q ${FILTER:+--filter "$FILTER"}

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
