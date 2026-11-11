#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
pilot=tools/invariant-pilots
jar=${DAIKON_JAR:?Set DAIKON_JAR to daikon-5.9.0/daikon.jar}
dotnet tool install dotnet-stryker --version 4.13.0 --tool-path "$pilot/.tools" || test -x "$pilot/.tools/dotnet-stryker"
dotnet tool install Microsoft.Coyote.CLI --version 1.7.11 --tool-path "$pilot/.tools" || test -x "$pilot/.tools/coyote"
dotnet build "$pilot" > "$pilot/results/build.log" 2>&1
dotnet "$pilot/bin/Debug/net10.0/Pilots.dll" > "$pilot/results/fscheck.log" 2>&1
dotnet "$pilot/bin/Debug/net10.0/Pilots.dll" trace
java -cp "$jar" daikon.Daikon "$pilot/results/training.dtrace" -o "$pilot/results/training.inv.gz" > "$pilot/results/daikon-training.log" 2>&1
java -cp "$jar" daikon.tools.InvariantChecker --conf --filter --verbose "$pilot/results/training.inv.gz" "$pilot/results/heldout.dtrace" > "$pilot/results/daikon-heldout-visible.log" 2>&1
DOTNET_ROLL_FORWARD=Major "$pilot/.tools/coyote" rewrite "$pilot/bin/Debug/net10.0/Pilots.dll" > "$pilot/results/coyote-rewrite.log" 2>&1
DOTNET_ROLL_FORWARD=Major "$pilot/.tools/coyote" rewrite "$pilot/bin/Debug/net10.0/Bifrost.dll" > "$pilot/results/coyote-rewrite-production.log" 2>&1
dotnet "$pilot/bin/Debug/net10.0/Pilots.dll" coyote > "$pilot/results/coyote-lifecycle-array.log" 2>&1
dotnet "$pilot/bin/Debug/net10.0/Pilots.dll" coyote negative > "$pilot/results/coyote-negative.log" 2>&1
(cd src/Bifrost.Tests.Scheduling && ../../tools/invariant-pilots/.tools/dotnet-stryker --test-runner mtp --project Bifrost.Scheduling.Core.csproj --mutate '**/IntervalCadence.cs' --concurrency 1 --reporter json --reporter progress) > "$pilot/results/stryker-mtp-full.log" 2>&1
