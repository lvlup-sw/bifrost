#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
pilot=tools/invariant-pilots
target=src/Bifrost.Tests.Scheduling/SchedulerReplayTests.cs
cp "$target" "$pilot/SchedulerReplayTests.backup"
trap 'cp "$pilot/SchedulerReplayTests.backup" "$target"; rm "$pilot/SchedulerReplayTests.backup"' EXIT
python3 - <<'PY'
p='src/Bifrost.Tests.Scheduling/SchedulerReplayTests.cs'
s=open(p).read()
assert 'IsEqualTo(101);' in s
open(p,'w').write(s.replace('IsEqualTo(101);','IsEqualTo(-999);',1))
PY
set +e
(cd src/Bifrost.Tests.Scheduling && ../../tools/invariant-pilots/.tools/dotnet-stryker --test-runner mtp --project Bifrost.Scheduling.Core.csproj --mutate '**/IntervalCadence.cs' --concurrency 1 --reporter progress) > "$pilot/results/stryker-assertion-negative.log" 2>&1
result=$?
set -e
printf 'Stryker negative-control exit=%s\n' "$result"
test "$result" -ne 0
rg -q SavedProductionCounterexample "$pilot/results/stryker-assertion-negative.log"
