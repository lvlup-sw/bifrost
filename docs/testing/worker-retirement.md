# Worker retirement and request cancellation

## Reproduction and root cause

The dependency register's D-001 also reproduces on PR #84 at
`d4a2a4c4ff44cecc44dd2bd5ff79315b11da3400`. All nine retirement cases fail there:
idle and busy workers through the coordinator, plus idle workers through the
autoscaling decorator, across FIFO, exact priority, and relaxed priority dispatch.
The original worker stays blocked so only the dynamic worker can execute more work.

`WorkerRegistry` sets `WorkerInfo.StopRequested`, but the consumer loop never reads
that flag and its queue wait has no retirement signal. Consequently idle workers
remain parked and busy workers continue consuming after their handler finishes.
The registry removes a worker only when its function returns, so both registry
membership and `ActiveWorkers` remain elevated.

A separate regression reproduces another lifetime error in all three strategies:
`AutoscalingCoordinator` passes the scale-up request token as the worker token.
Canceling an already completed request cancels the worker's active handler.
The decorator already uses the orchestrator shutdown token for worker lifetime.

## Fix and contract

Each dynamic worker owns a retirement signal. The consumer links it to queue waits
and checks it before further reads. Handlers receive only the existing shutdown
and worker-lifetime cancellation signal. Retirement therefore wakes idle workers
and lets a busy worker finish its current item. A dequeue concurrent with retirement
can finish as the current item; a stop request is not a completion barrier.

A priority wait consumes a semaphore permit. If retirement wins before dequeue,
the retiring worker hands a wake to another consumer. FIFO waits do not consume
an item permit. A controlled acquire/read-boundary test checks progress without
another enqueue or queue completion.

Signal cleanup is serialized with stop requests, and concurrent batch-stop requests
count each selected worker once. The coordinator now uses the request token only
to cancel worker creation and uses the shutdown token for worker lifetime.
The decorator retains its existing idle-only selection policy.

`IWorkOrchestrator<T>.CreateWorkerFunction(Action<bool>?, CancellationToken)` is an
additive overload. Every built-in decorator forwards it. Custom orchestrators and
custom decorators must implement/forward it to support graceful autoscaling. Its
default implementation reports unsupported operation instead of silently ignoring
retirement. Existing worker-factory overloads retain their original behavior.

## Verification

On Fedora Linux 45 x64, .NET SDK 10.0.303 and runtime 10.0.11:

- Unchanged PR #84: nine retirement regressions fail; three request-lifetime regressions fail.
- Fixed implementation: 840 main, 160 concurrency, and 359 scheduling tests pass.
- All 23 maintained invariant suites pass.
- NativeAOT scheduling smoke passes and executes dynamic-worker retirement. The local
  SDK defaults to vulnerable 10.0.11 compiler packs, so this run explicitly uses
  cached 10.0.15 packs with `-p:RuntimeFrameworkVersion=10.0.15`. Audit remains enabled.
- Fifteen new cases cover three retirement cycles, all built-in decorators, busy
  handler completion, retained work, drain, abort, concurrent requests, and wake handoff.

Run the maintained regression class:

```sh
dotnet run --project src/Bifrost.Tests -c Release -- --treenode-filter '/*/*/WorkerRetirementTests/*'
```

The invariant runner includes this class. These are bounded histories, not a proof
of every native interleaving or a release qualification on Windows.

## CI test repair

The first hosted CI run exposed an existing race in
`DLQ_DoesNotRetryEnqueueFailures`: its 100 ms sleep did not guarantee handler entry.
When the first item remained queued, both the fill item and overflow item were
rejected, producing two DLQ entries instead of one. The test now waits for an
explicit handler-entry signal, asserts that the fill item is admitted, and disposes
the service provider even if an assertion fails. No DLQ production behavior changes.

## Performance check

The extra cancellation source is allocated once per dynamic worker. The per-item
path uses a token check, with no new registry lookup, lock, timer, or allocation.

The existing BenchmarkDotNet ShortRun benchmarks were run baseline then fixed on
one i9-13900K host with concurrent Workstation GC. Each run uses three warmups and
three measured iterations:

| Measurement | PR #84 | Fixed |
|---|---:|---:|
| Steady-state loop, per item | 230.6 ns, 0 B | 227.5 ns, 0 B |
| 100,000 items, 1 worker | 23.13 ms | 21.23 ms |
| 100,000 items, 4 workers | 62.79 ms | 66.18 ms |
| 100,000 items, 16 workers | 287.86 ms | 221.01 ms |

These timings have broad confidence intervals and short-iteration warnings. They
support an allocation smoke check, not a throughput improvement or a tight regression
bound. The benchmarks use initial workers and do not measure scale-up allocation.
They do not resolve the separate historical CPQ investigation in issue #77.

```sh
dotnet run --project src/Bifrost.Benchmarks -c Release -- --filter '*WorkerThroughputBenchmarks*' '*WorkerLoopAllocationBenchmarks*' --job Short --artifacts <output-directory>
```

Local raw reports are in `/tmp/bifrost-dependency-perf/{baseline,fixed}/`.
