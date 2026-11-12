# Library defect investigation

Reviewed on 2026-11-11 (America/Los_Angeles). Baseline: PR #88 at
`70dfa245ce2d774831add100cab6f79b5260f458`. Initial validated code and tests:
`18a0fc6b1367c63daea91cd479d517b2b530e596`. Candidate with coverage-fixture
isolation: `a7d59e00fff32cb6c49c87bf800e9b6219de86b9`. Production behavior is unchanged between these two snapshots. The final
test-fixture candidate is `1864801d8a2bf70c21a28e9555d0e5adc76b04f5`; later commits
contain documentation and evidence only. The five new layers build on #84 → #87 → #88.

This pass covers the library's ten investigation areas below. It adds 52 test
cases, including controls and a child-process entry point. Across staged
reproductions, 37 cases failed before their fixes. Passing controls are not
reported as defects. Consumer-only D-002 through D-005, including Ev2, are excluded.

## Coverage and disposition

| Area and contract | Investigation, oracle, and result | Remaining limit |
|---|---|---|
| Events and callbacks: observations cannot lose accepted work or strand readers | Fixed registration/disposal races, blocking DLQ callback prefixes, and throwing worker/rejection observations. Eleven failing cases now pass, including all three dispatch strategies and 2,000 subscription races. | Native schedules are uncontrolled. DLQ callbacks remain concurrent, best-effort, unbounded, and not drained. |
| Admission, retry, and DLQ: cancellation and producer identity survive routing | Fixed replaced drain tokens and missing rejection correlation. Existing resilience/DLQ interaction and lifecycle outcome tests cover terminal failure routing. | Synchronous rejection persistence remains asynchronous best-effort routing. External after-effect failures cannot imply rollback. |
| Workers and hosting: retirement/teardown cannot consume work accidentally | Retained #87/#88 retirement, request-token lifetime, and hosted Stop/Dispose regressions. Main tests include autoscaling lifecycle, scaling clocks, drain/abort, and worker accounting. | Arbitrarily hanging handlers and prolonged thread-pool starvation are not bounded by these tests. |
| Scheduler persistence: host startup preserves saved state and required occurrence identities | Fixed startup overwrites and repeated/incorrect past one-shot arming. Eleven hosted cases cover lifecycle states, missed fires, and checkpoint outages. Four actual process kills verify checkpoint boundaries. | The single-record file fixture is not a production adapter, multi-writer test, or power-loss test. |
| Queues and overload: admitted items remain accounted for and capacity stays bounded | All 161 concurrency tests passed, including stress. Four 60-second runs cover exact/relaxed priority with 2/8 workers, capacity 128, and oversized bursts. Capacity, conservation, and progress checks pass. | This is four minutes of aggregate queue load, not a days-long resource qualification or latency SLA. |
| Time and arithmetic: valid inputs do not wrap ordering or fault on exhaustion | Fixed exact-unit conversion, unrepresentable boosts, signed key wrap, interval exhaustion, and jitter overflow. Nine new cases cover fractional frequencies, extremes, and 1,000 jitter draws. Existing cron/DST and Coalesce tests pass. | Random jitter coverage is finite. Generic/custom/jittered Coalesce scans remain O(backlog). Saturated extreme keys can tie. |
| DI and extensions: actual composition preserves ownership and saved state | Real hosted DI startup exposed the persistence overwrite. Scoped handler, decorator chain, job-fire scope, and shutdown tests pass in the full suites. | Same-name awaited reentrant store mutations can deadlock. Current gates are per-name, not the historical 64 stripes described in #85. |
| HTTP resilience: cancellation and configured budgets retain their meaning | Fixed negative retry counts silently disabling retry. Three new configured-pipeline tests verify caller cancellation, attempt/total timeout interaction, and circuit opening/recovery. | These are in-process pipeline tests; live transport, DNS, and prolonged network outages are not simulated. |
| Health and telemetry: observation does not corrupt processing or misstate scheduled instants | Fixed observer exception propagation and two preview defects: first-interval drift and expired one-shots. Existing health thresholds, metrics, events, and inspector tests pass. | Health/metrics are diagnostic snapshots, not atomic processing-state guarantees. Later jittered previews are projections. |
| Packaging and deployment: candidate builds and executes in supported local mode | Nine packages and symbol packages build. Each package contains its README and net10.0 assembly. Both NativeAOT smoke binaries pass with warnings treated as errors. | Tested Linux x64 only. Repeat gates on the integrated release SHA; Windows, ARM64, actual adapters, and full HTTP NativeAOT composition remain unqualified. |

The critical loss/availability paths map to admission → queue → worker → retry/DLQ,
and registration → dispatch → checkpoint → recovery. Tests distinguish rejected
work, abort-discard behavior, and permitted pre-checkpoint replay from defects.
No exactly-once application-effect guarantee is added.

## Root causes and fixes

- [Observer boundaries](../rca/library-observer-boundaries.md): callback isolation and subscription ownership.
- [Input boundaries](../rca/library-input-boundaries.md): cancellation, correlation, policy, identity, and clock sampling.
- [Scheduler recovery](../rca/library-scheduler-recovery.md): real startup, one-shot reconciliation, and previews.
- [Arithmetic boundaries](../rca/library-arithmetic-boundaries.md): exact units and representable ranges.

NaN jitter was a falsified lead: the existing .NET range guard already rejects it
on all three construction/mutation paths. Those passing controls remain in the suite.

## Validation

Host: Fedora Linux 45, Intel Core i9-13900K, x64, 32 logical processors, SDK
10.0.303, JIT runtime 10.0.11, Release configuration. NativeAOT explicitly uses
10.0.15 runtime/compiler packs with NuGet audit enabled. The installed SDK's
older default AOT pack was already identified as an audit failure in the prior pass.

| Gate | Result |
|---|---|
| Main library | 867 passed, 0 failed |
| Scheduling | 387 passed, 0 failed |
| Concurrency, including stress | 161 passed, 0 failed |
| Maintained invariant lane | 136 cases in 25 suites passed; clean source SHA recorded |
| Separate process termination | All four cuts reached and recovered with expected identities |
| NativeAOT scheduling/concurrency | Both publish and execute successfully |
| Local NuGet pack | Nine packages plus symbols; no warning/error in retained build logs |
| Overload checks | Four runs passed capacity, conservation, and per-class progress |

The 1,415 full-suite cases include the invariant cases; do not add the two counts.
The concurrency suite was run before the final observer fast-path and preview
changes; neither changed the concurrency assembly. Main and scheduling were rerun
after those changes. New tests then received a test-only commit with no source edits.
The invariant lane ran against that clean commit. After the fixture correction
below, all 867 main tests also passed with four-processor coverage, and all 136
invariant cases passed again against the clean final candidate.

Process cuts and observed total attempts: before dispatch 1; after dispatch 2;
before checkpoint effect 2; after checkpoint effect/before acknowledgment 1.
All retain the required occurrence identity. Dispatch is deliberately synchronous
in the fixture to pin the cut; file contents are flushed before atomic rename.
The fixture does not establish directory durability after power loss.

Overload acceptance was capacity ≤128, positive dispatch in every class, and
`offered = rejected + dispatched + residual`. All four runs satisfy it. Residuals
were 115, 4, 10, and 3; this harness stops at the window and does not drain.
Maximum sampled occupancy was 98.44%. Allocation rate includes harness tasks,
timers, and retained samples; no memory or wall-clock starvation bound is inferred
from the harness's historical `StarvationBoundHeld` field. NativeAOT/build and
invariant work overlapped portions of this soak, so its latency is not isolated.

## Performance evidence

Baseline and candidate ran sequentially on the same host, with two launches,
five warmups, and ten measured iterations per launch. Raw iteration times and
BenchmarkDotNet reports are retained in [evidence](evidence/library-audit/).

| Workload | Baseline mean ± reported error | Fixed mean ± reported error |
|---|---:|---:|
| Steady worker loop | 248.2 ±55.68 ns | 256.6 ±16.15 ns |
| 100,000 items, 1 worker | 21.70 ±1.724 ms | 21.42 ±5.339 ms |
| 100,000 items, 4 workers | 64.65 ±6.743 ms | 71.49 ±5.600 ms |
| 100,000 items, 16 workers | 265.43 ±18.269 ms | 249.07 ±9.351 ms |
| Priority park/wake | 3.475 ±1.149 μs | 3.780 ±1.038 μs |

The steady worker loop reports 0 B allocated before and after; priority park/wake
reports 208 B in both. Throughput allocation varies with asynchronous scheduling.
The sample spread and fixed before/after order do not establish a strict regression
bound or speedup. The four-worker mean is higher, with overlapping reported error
ranges. A tightly controlled balanced campaign remains appropriate for a release
performance budget. These tests do not resolve the historical deep-reference/drain
comparison in [#77](https://github.com/lvlup-sw/bifrost/issues/77).

The implementation keeps null-observer checks outside exception-isolation helpers,
adds no lock to event publishing, checks both DLQ tokens without a linked source,
and performs wide priority conversion only at configuration. Runtime key computation
remains allocation-free integer arithmetic. Saved-state loading adds startup I/O,
not per-dispatch I/O. DLQ observer isolation deliberately adds a task per subscriber.

## Reproduction

Run from the repository root:

```sh
dotnet run --project src/Bifrost.Tests -c Release -- --treenode-filter '/*/*/*/*[Category!=Stress]'
dotnet run --project src/Bifrost.Tests.Scheduling -c Release -- --treenode-filter '/*/*/*/*[Category!=Stress]'
dotnet run --project src/Bifrost.Tests.Concurrency -c Release -- --treenode-filter '/*/*/*/*'
python3 scripts/run-invariant-tests.py --skip-build --output /tmp/bifrost-library-invariants
dotnet run --project src/Bifrost.Benchmarks -c Release -- --filter '*WorkerLoopAllocationBenchmarks*' '*WorkerThroughputBenchmarks*' '*PriorityWaitAllocationBenchmarks*' --job short --iterationCount 10 --warmupCount 5 --launchCount 2 --artifacts /tmp/bifrost-library-bench
dotnet run --project src/Bifrost.Benchmarks -c Release -- soak --seconds 60 --binding both --workers 2,8 --capacity 128 --min-work-ms 1 --max-work-ms 50 --burst-interval 5 --burst-size 256 --out /tmp/bifrost-library-soak
dotnet pack src/Bifrost.sln -c Release -o /tmp/bifrost-library-packages
```

For each `Bifrost.Scheduling.AotSmoke` and `Bifrost.Concurrency.AotSmoke` sample,
run `dotnet publish samples/<sample>/<sample>.csproj -c Release -r linux-x64
-p:PublishAot=true -p:RuntimeFrameworkVersion=10.0.15
-p:CustomAfterMicrosoftCommonTargets=<absolute-path-to-evidence/aot.targets>
-o /tmp/<sample>`, then execute `/tmp/<sample>/<sample>`.
The compiler override is an external validation input, not a repository dependency change.

## Remaining qualification work

[#85](https://github.com/lvlup-sw/bifrost/issues/85) remains open. This pass closes
specific defect and process-cut evidence gaps, not its entire release-risk program.
Long outages, memory pressure, thread-pool exhaustion, scheduler command-backlog
budgets, production durable adapters, multi-writer modes, and target-platform/release
qualification remain. Custom stores/handlers/callbacks must honor cancellation and
return; exceptions after external effects do not establish rollback. Existing SMT,
Coyote, randomized mutation, and native-schedule exploration retain their published
bounds. No finite suite establishes that latent defects are absent.

No issue was closed manually, and no package was published. Consumer-only exclusions
are not counted as unresolved library work. The older dependency-review ledger
remains a historical record of #87/#88; this document adds the current findings.

## CI fixture correction

The first remote runs passed scheduling and concurrency but failed the new blocking
subscriber probe before its callback entered. A local full-suite run with
`DOTNET_PROCESSOR_COUNT=4 taskset -c 0-3 dotnet run --project src/Bifrost.Tests
-c Release --no-restore -- --treenode-filter '/*/*/*/*[Category!=Stress]' --coverage`
reproduced both callback variants timing out. Isolating this deliberately blocking
test with `NotInParallel` and awaiting callback cleanup preserved its assertion and
deadlines. The observer layer then passed all 854 tests under the same conditions.
The final stack passed all 867 with coverage, followed by the 136-case invariant lane.
Both failed and successful constrained-run logs are retained.

Remote CI was restarted on the corrected heads. The separate
[Project Automation run](https://github.com/lvlup-sw/bifrost/actions/runs/43145370406)
reported `startup_failure` without starting jobs or returning check annotations.
That workflow configuration problem is outside this library defect change and
is not represented as a passing check. No workflow or permissions were altered.

The four implementation PRs subsequently passed remote Build & Test and coverage
gates. Qualification CI exposed two pre-existing one-second processing deadlines
in `WorkOrchestratorTests` under coverage. These two timing probes now run outside
unrelated parallel fixtures without changing their deadlines. The teardown probe
also uses `await using`, ensuring disposal after a failed assertion. The final
test-fixture candidate passed all 867 main tests with four-processor coverage again.
The failing CI excerpt and successful final run are retained. Scheduling and
concurrency production/tests are unchanged by these fixture-only corrections.
