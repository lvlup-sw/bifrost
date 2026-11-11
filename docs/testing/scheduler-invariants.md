# Scheduler invariant evidence (WP1 S1/S2)

Baseline: `0fc121384950d964722a8de36d72a26679a428d3`; verified ancestor before edits. SDK 10.0.303, runtime 10.0.11, TUnit 1.2.11, source-generated engine. No external workflow tooling used.

## Contracts and domains

- `src/Bifrost.Scheduling.Core/IntervalCadence.cs`: positive intervals, zero jitter, aligned next occurrence strictly after now.
- `src/Bifrost.Scheduling.Core/MissedFirePolicy.cs` and `src/Bifrost.Scheduling/Internal/MissedFirePolicyApplier.cs`: Coalesce latest missed occurrence; FireAll oldest capped prefix; Skip empty.
- `src/Bifrost.Scheduling.Core/JobFireContext.cs`: dispatched FireTime is the scheduled occurrence.
- `src/Bifrost.Scheduling/TickEngine/ScheduleTickLoop.cs`: startup reconciliation uses durable last-fired state and passes selected occurrences to dispatch.

S1 uses seeds 17, 83, 2026, 128 samples each, positive tick intervals below one day, anchors in years 2/2026/9998 and representable results. Each sample covers null, earlier, equal and future last-fire state (1,536 input pairs). Its independent integer oracle is N+I for null, otherwise L+max(1,floor((N-L)/I)+1)*I. Tests also translate both times by 7,919 ticks and extend L by 31 periods when N-L>=I.

S2 covers 99/100/101/500 missed occurrences, 1/17/minute/hour tick intervals, exact and fractional upper boundaries, and caps 1/7/100. Expected occurrence is L+floor((N-L)/I)*I; FireAll expected entries are L+jI for j=1..min(count,cap). The oracle does not call production scheduling methods. Phase extension adds 211 periods to history; translation shifts 37 days. Null/equal/future/no-due states produce empty results for every policy. A counting cadence verifies FireAll makes exactly seven calls for cap seven. Cron and one-shot regressions exercise generic handling. Startup assertion captures actual JobFireContext.FireTime in the dispatcher.

## Reproduction and negative control

Tests commit `904cb84` against unchanged production baseline:

```sh
dotnet test --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/*IntervalInvariantTests/*'
dotnet test --project src/Bifrost.Tests.Scheduling --no-build -- --treenode-filter '/*/*/*MissedFireStartupTests/Coalesce_FiveHundredPastIntervals_DispatchesLatestOccurrence'
```

First command: 11 tests, 5 failed, 6 passed, 475 ms, exit 2. Second: 1 failed, 502 ms, exit 2. Startup expected 2026-06-13 12:00 UTC but received 05:20 UTC (400 minutes stale). Raw exact failure output is retained in `scheduler-baseline.txt` and `scheduler-startup-baseline.txt`.

The production baseline itself is the semantic cap-before-selection negative control and is killed by both suites. A permanent deterministic negative-control test constructs that faulty capped-prefix result and proves it disagrees with the arithmetic oracle; it does not inject a runtime fault or weaken expected results.

## Candidate and validation

Fix commit `35f0861` is separate from tests. Zero-jitter intervals select the latest due tick in O(1); other supported cadences walk forward with constant retained memory and the non-advancing guard. FireAll retains its bounded enumeration. Additional S1 metamorphisms are in `2dcf359`.

```sh
dotnet test --project src/Bifrost.Tests.Scheduling -- --treenode-filter '/*/*/*IntervalInvariantTests/*'
dotnet test --project src/Bifrost.Tests.Scheduling --no-build -- --treenode-filter '/*/*/*MissedFireStartupTests/*'
dotnet test --project src/Bifrost.Tests.Scheduling --no-build
dotnet src/Bifrost.Tests.Scheduling/bin/Debug/net10.0/Bifrost.Tests.Scheduling.dll --list-tests --treenode-filter '/*/*/*IntervalInvariantTests/*'
```

Final invariants: 11/11, 452 ms. Startup: 7/7, 449 ms. Final full scheduling suite: 298/298, 1.214 s. Direct discovery prints 298 names in 307 ms (this runner ignores the filter for listing); new invariant names and parameter cases are present. Logs retained beside this document. `git diff --check` passed. Sandbox-only builds exited silently during restore; the same authorized dotnet commands succeeded with escalation.

An earlier malformed union filter unexpectedly ran all 298 tests and produced one failure in existing `RouterCompletesDispatchThenThrows_InFlightBalanced_LoopRecoversToIdle` (expected FireCount 1, actual 0). Two subsequent complete runs passed (882 ms and 1.214 s). Root cause is not established; this is an observed intermittent failure, not proven baseline behavior. Exact output retained in `scheduler-intermittent-failure.txt`.

## Limits

The generated mathematical domain excludes jitter, invalid intervals, and unrepresentable date arithmetic. Generic Coalesce has O(backlog) CPU cost; it does not truncate results to the FireAll cap and retains only one occurrence. Arbitrary custom cadence behavior beyond the progress guard is not certified. Cron regression covers UTC minute cadence; DST behavior remains covered by the existing cron tests, not a new independent calendar oracle. Existing capped missed-count telemetry is unchanged. Narrow documentation-warning pragmas allow new public TUnit tests without adding code comments, as requested. No shared history package dependency, push, or PR.
