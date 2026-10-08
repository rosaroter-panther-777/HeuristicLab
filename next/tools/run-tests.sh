#!/usr/bin/env bash
# Run the next/ test suite. Usage: next/tools/run-tests.sh [quick|daily|all] [extra dotnet test args]
#   quick (default): everything except the long-running Run.Daily category
#   daily:           only Run.Daily (full algorithm runs, takes long)
#   all:             everything
# On Linux the tests in next/tests/known-failures-linux.txt are excluded (see reasons there).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mode="${1:-quick}"; shift || true

case "$mode" in
  quick) filter="TestCategory!=Run.Daily" ;;
  daily) filter="TestCategory=Run.Daily" ;;
  all)   filter="" ;;
  *) echo "unknown mode '$mode' (quick|daily|all)" >&2; exit 2 ;;
esac

if [[ "$(uname -s)" != MINGW* && "$(uname -s)" != MSYS* ]]; then
  while read -r name; do
    filter="${filter:+$filter&}FullyQualifiedName!=$name"
  done < <(sed -e 's/#.*//' -e 's/[[:space:]]*$//' "$here/../tests/known-failures-linux.txt" | grep -v '^$')
fi

exec dotnet test "$here/../tests/HeuristicLab.Tests/HeuristicLab.Tests.csproj" ${filter:+--filter "$filter"} "$@"
