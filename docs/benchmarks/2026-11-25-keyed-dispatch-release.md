# 0.7.0 keyed dispatch release check

This report records the 0.7.0 release benchmarks (#135). It measures the allocations of the existing FIFO and priority paths after the keyed dispatch changes, and keyed dispatch under contention. The raw BenchmarkDotNet reports are in [`data/keyed-release-2026-11-25`](data/keyed-release-2026-11-25/).

## Environment

| | |
|---|---|
| Machine | Apple M3 laptop, 8 cores, macOS Sonoma 14.4.1 |
| Runtime | .NET 10.0.13, Arm64 RyuJIT, SDK 10.0.303 |
| BenchmarkDotNet | 0.14.0 |
| Source | branch `claude/keyed-release` (the top of the 0.7.0 keyed dispatch stack) |

Two runs were made:

- **Run 1** — `--job short` (3 iterations) for all five classes.
- **Run 2** — `--job short --iterationCount 15 --warmupCount 5` for the two new classes and `WorkerLoopAllocationBenchmarks`.

```bash
dotnet run -c Release --project src/Bifrost.Benchmarks -- --job short \
  --filter "*DispatchPathAllocationBenchmarks*" "*KeyedDispatchContentionBenchmarks*" \
  "*EnqueueAllocationBenchmarks*" "*WorkerLoopAllocationBenchmarks*" "*PriorityWaitAllocationBenchmarks*"
```

**How to read the times.** These are short jobs on a laptop. The benchmarks that measure a whole batch use `InvocationCount=1`, and each iteration takes only a few milliseconds, so the means have wide error bars and the two runs differ by up to 4x. Treat the times as a rough comparison on one machine. The allocation figures come from the memory diagnoser, do not depend on timing, and matched across both runs.

## Existing paths: allocations

| Benchmark | Path | Allocated per op, 0.7.0 | Earlier record |
|---|---|---:|---:|
| `EnqueueAllocationBenchmarks.TryEnqueue_ZeroAlloc` | FIFO admission | 0 B | 0 B ([Feb 2026](2026-02-06-full-suite-results.md)) |
| `EnqueueAllocationBenchmarks.EnqueueAsync_ZeroAlloc` | FIFO admission | 0 B | 0 B ([Feb 2026](2026-02-06-full-suite-results.md)) |
| `WorkerLoopAllocationBenchmarks.WorkerLoop_SteadyState` | FIFO enqueue and worker loop, per item | 0 B | 0 B ([library audit](../testing/evidence/library-audit/bench-after/)) |
| `PriorityWaitAllocationBenchmarks.ParkWake_SteadyState` | priority binding park and wake | 208 B | 208 B ([library audit](../testing/evidence/library-audit/bench-after/)) |
| `DispatchPathAllocationBenchmarks` (`Fifo`) | FIFO enqueue, dispatch, handler, outcome, per item | 0 B | new |
| `DispatchPathAllocationBenchmarks` (`PriorityExact`) | exact-priority enqueue, dispatch, handler, outcome, per item | 0 B | new |

The existing FIFO and priority paths allocate nothing per item, as before 0.7.0. The terminal outcome stage, the lease seam, and the dispatchable-backlog accounting added no steady-state allocation to them. The 208 B park-and-wake figure is unchanged.

## Keyed dispatch: allocations

| Benchmark | Load | Run 1 | Run 2 |
|---|---|---:|---:|
| `DispatchPathAllocationBenchmarks` (`Keyed`) | 1 producer, 1 worker, 64 keys | 39 B | 40 B |
| `KeyedDispatchContentionBenchmarks` (`Keyed1`) | 4 producers, 4 workers, 1 key | 48 B | 58 B |
| `KeyedDispatchContentionBenchmarks` (`Keyed64`) | 4 producers, 4 workers, 64 keys | 46 B | 39 B |
| `KeyedDispatchContentionBenchmarks` (`Keyed4096`) | 4 producers, 4 workers, a new key per item | 170 B | 169 B |

Keyed dispatch allocates per key lifetime, not per item. The lease is the key's existing state object, so taking and releasing a slot allocates nothing. The queue removes a key when it has no queued and no running items, and creates its state again when the next item for that key arrives. That state and its item queue are the allocation. The figure per item therefore depends on how often keys drain:

- `Keyed4096` gives every item a new key, so each item pays for one key state and its queue (about 170 B).
- With 64 keys or one hot key, keys drain whenever the workers catch up with the producers, so the cost is spread over the items queued in between (about 40 to 60 B).

A key that stays busy costs nothing more per item. Pooling key state could remove this cost. It is not part of 0.7.0.

## Keyed dispatch under contention: times

`KeyedDispatchContentionBenchmarks` runs 4 producers and 4 workers over 4,096 items per invocation, with a handler that only signals a countdown. The time is per item and covers admission, the queue, wake-ups, the lease, and the terminal outcome stage.

| Mode | Run 1 mean | Run 1 median | Run 2 mean | Run 2 median |
|---|---:|---:|---:|---:|
| `Fifo` | 916 ns | 651 ns | 1,286 ns | 784 ns |
| `Keyed1` (one hot key, one item at a time) | 1,904 ns | 1,462 ns | 1,850 ns | 1,801 ns |
| `Keyed64` | 1,020 ns | 938 ns | 4,498 ns | 3,575 ns |
| `Keyed4096` | 812 ns | 753 ns | 1,686 ns | 1,159 ns |

- With many keys, keyed dispatch ran in the same range as FIFO in run 1 (753 to 938 ns median against 651 ns). Run 2 was noisier, and its `Keyed64` result (3.6 µs median) was not reproduced in run 1. A longer job on a quiet machine is needed before any claim about the gap between FIFO and keyed dispatch.
- `Keyed1` is the expected worst case: one key with a concurrency limit of 1 runs one item at a time, so three of the four workers stay idle. Its cost per item was about 2x FIFO in both runs. That is the price of per-key ordering, not lock overhead.
- No run rejected an item or timed out. Every batch drained completely.

## Single-producer dispatch path: times

| Strategy | Run 1 mean | Run 2 mean | Run 2 median |
|---|---:|---:|---:|
| `Fifo` | 762 ns | 4,417 ns | 1,984 ns |
| `PriorityExact` | 1,549 ns | 3,235 ns | 1,434 ns |
| `Keyed` (64 keys) | 709 ns | 2,516 ns | 893 ns |

One producer and one worker spend most of each item waking the worker, so these times are dominated by park and wake latency and vary widely between runs. They show no keyed-specific slowdown.

## Regression tests

The same branch passed the FIFO and priority regression tests (`DispatchStrategyTests`, `PriorityDispatchAcceptanceTests`, `AgingBoundAcceptanceTests`, `OrchestratorWorkQueueRewriteTests`, the `Queues` tests, and the full `dotnet test --solution src/Bifrost.sln` run). The keyed Native AOT smoke ([`samples/Bifrost.Keyed.AotSmoke`](../../samples/Bifrost.Keyed.AotSmoke/Program.cs)) published with `PublishAot=true` for `osx-arm64` without trim or AOT warnings and passed as a native binary.
