#!/usr/bin/env bash
# Claude Code PreToolUse hook: run tools/verify.sh before any `git commit`; block the commit if it fails.
set -uo pipefail

command="$(python3 -I -c 'import json,sys; print(json.load(sys.stdin).get("tool_input", {}).get("command", ""))')"
[[ "$command" =~ (^|[[:space:];&|])git[[:space:]]+commit ]] || exit 0

cd "${CLAUDE_PROJECT_DIR:-$(git rev-parse --show-toplevel)}"
if ! output="$(tools/verify.sh 2>&1)"; then
  printf 'Commit blocked: tools/verify.sh failed. Fix the issues, then commit again.\n\n%s\n' "$(tail -40 <<<"$output")" >&2
  exit 2
fi
exit 0
