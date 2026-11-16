#!/usr/bin/env bash
# Cross-platform shared libraries, Avalonia headless UI, CLI and actual MCP protocol.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
source "$root/tools/env.sh"
cd "$root"
mkdir -p build/out/tests
results=$(mktemp -d "$root/build/out/tests/run-XXXXXXXX")
export MEILI_RENDER_DIR="$results/render"
mkdir -p "$MEILI_RENDER_DIR"
failed=0
for name in Core Data Analytics Shared Mac Automation; do
  if dotnet test "tests/Meili.$name.Tests/Meili.$name.Tests.csproj" -m:3 --filter 'Category!=Live' --logger "trx;LogFileName=$name.trx" --results-directory "$results" -v minimal > "$results/$name.log" 2>&1; then
    tail -n 4 "$results/$name.log"
  else
    tail -n 45 "$results/$name.log"
    failed=1
  fi
done
if dotnet build tests/Meili.Automation.Host/Meili.Automation.Host.csproj -c Release -m:3 -v minimal > "$results/cli-build.log" 2>&1; then
  if ! python3 -m unittest discover -s tests/automation -p "test_*.py" -v > "$results/coverage-guard.log" 2>&1; then failed=1; fi
  cat "$results/coverage-guard.log"
  if ! python3 tests/automation/check_capability_coverage.py --report "$results/capability-coverage.json" > "$results/capability-coverage.log" 2>&1; then failed=1; fi
  cat "$results/capability-coverage.log"
  if ! python3 tests/automation/cli_mcp_smoke.py --dll src/Meili.Cli/bin/Release/net10.0/easysellput.dll > "$results/cli-mcp.log" 2>&1; then failed=1; fi
  cat "$results/cli-mcp.log"
else
  cat "$results/cli-build.log"
  failed=1
fi
printf 'Results: %s\n' "$results"
exit "$failed"
