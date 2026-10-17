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
  if grep -E -q 'Include="(Pulumi|Azure)[^"]*"|backend-providers|jd\.azure' "$p"; then
    fail "$p must not reference Pulumi, Azure SDKs or backend providers (see docs/engineering/standards.md)"
  fi
done
# The Azure adapter sits next to the core: an allowlist over what MSBuild evaluates (so a Condition or a multi-line element cannot slip a
# reference past a text match). It implements ports from jd.core and uses Azure SDKs, nothing else. A failed evaluation fails the check.
# Checked by hand when written, each variant added to src/jd.azure/jd.azure.csproj and rejected: a ProjectReference to jd.resolver (also
# with Condition="true" before Include), a PackageReference to Pulumi.Automation (also with Condition="true"), to Newtonsoft.Json and to
# Microsoft.Azure.Cosmos, a PackageReference whose Include is on the line after the element name, and a Reference with a HintPath (any
# Reference item is refused), a FrameworkReference to Microsoft.AspNetCore.App, a <Target>, an <Import> (also with Project= on the next line), a <UsingTask>, an <Sdk Name=…/> and a
# src/jd.azure/Directory.Build.props.
# Targets, imports, SDKs, tasks and a nearer Directory.Build file could add references after evaluation, so the project may hold none of them.
if [[ -f src/jd.azure/jd.azure.csproj ]]; then
  if grep -q -E '<(Target|Import|UsingTask|Sdk)([[:space:]>/]|$)' src/jd.azure/jd.azure.csproj; then
    fail "src/jd.azure/jd.azure.csproj must not declare a Target, Import, UsingTask or Sdk element (see docs/engineering/standards.md)"
  fi
  extra=$(find src/jd.azure -path '*/bin' -prune -o -path '*/obj' -prune -o -name 'Directory.Build.*' -print)
  [[ -z "$extra" ]] || fail "src/jd.azure must not hold a Directory.Build file: ${extra//$'\n'/; } (see docs/engineering/standards.md)"
  # Evaluated in this shell, not in a subshell, so a failed evaluation stops the script.
  evaluate() { dotnet msbuild src/jd.azure/jd.azure.csproj -nologo "-getItem:$1" 9>&-; }
  projects=$(evaluate ProjectReference) || fail "could not evaluate src/jd.azure/jd.azure.csproj"
  packages=$(evaluate PackageReference) || fail "could not evaluate src/jd.azure/jd.azure.csproj"
  references=$(evaluate Reference) || fail "could not evaluate src/jd.azure/jd.azure.csproj"
  frameworks=$(evaluate FrameworkReference) || fail "could not evaluate src/jd.azure/jd.azure.csproj"
  identities() { { grep -o '"Identity": "[^"]*"' <<<"$1" || true; } | sed 's/"Identity": "\(.*\)"/\1/'; }
  outside=$({ identities "$projects" | grep -v -x '\.\./jd\.core/jd\.core\.csproj'; identities "$packages" | grep -v -E '^Azure\.'; identities "$references"
    identities "$frameworks" | grep -v -x 'Microsoft\.NETCore\.App'; } || true)
  [[ -z "$outside" ]] || fail "src/jd.azure/jd.azure.csproj may reference only jd.core (ProjectReference), Azure.* packages (PackageReference) and the .NET runtime (FrameworkReference), not: ${outside//$'\n'/; } (see docs/engineering/standards.md)"
fi
echo "ok"

step "Secrets"
if git ls-files | grep -E -q '(^|/)[^/]*\.env$|(^|/)\.pulumi/'; then
  fail "tracked .env or .pulumi state files found"
fi
echo "ok"
