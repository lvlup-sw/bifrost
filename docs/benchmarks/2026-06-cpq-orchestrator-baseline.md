# CPQ Orchestrator FIFO Baseline — 2026-06-12

DR-7 reference artifact for the CPQ port + priority dispatch feature. Captures the
enqueue-to-dispatch round-trip performance of the **current** `WorkOrchestrator<TWork>`
(bounded `Channel<T>`, `BoundedChannelFullMode.Wait`) **before** the priority-dispatch port.
The post-port comparison run (T28) MUST re-run the same benchmark with **identical job
settings** (see below) and compare against this table.

## Benchmark

- Class: `OrchestratorBaselineBenchmarks` (`src/Bifrost.Benchmarks/Orchestrator/OrchestratorBaselineBenchmarks.cs`)
- Operation: enqueue **10,000** `int` payloads via `EnqueueAsync` and wait (CountdownEvent)
  for all handler invocations to complete. Handler is a no-op that signals the countdown.
- Orchestrator constructed directly: handler + `Options.Create(new WorkOrchestratorOptions { Capacity = 128, WorkerCount = N })` + `NullLogger` (no DI host).
- Params: `WorkerCount ∈ {1, 2, 8}`; `Capacity = 128` (fixed).
- `[MemoryDiagnoser]` enabled (per-op allocations).

## Exact job settings (T28 MUST match)

```
Command: dotnet run -c Release --project src/Bifrost.Benchmarks -- --filter "*OrchestratorBaseline*" --job Short
Job=ShortRun  InvocationCount=1  IterationCount=3
LaunchCount=1  UnrollFactor=1  WarmupCount=3
```

Note: `InvocationCount=1`/`UnrollFactor=1` are forced by `[IterationSetup]`
(countdown reset between invocations), independent of the Short job.

## Environment

```
BenchmarkDotNet v0.14.0, Pop!_OS 24.04 LTS
13th Gen Intel Core i9-13900K, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.202
  [Host]   : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX2
```

## Results (10,000 items per op)

| Method                   | WorkerCount | Mean      | Error    | StdDev    | Allocated |
|------------------------- |------------ |----------:|---------:|----------:|----------:|
| EnqueueDispatchRoundTrip | 1           |  2.109 ms | 1.671 ms | 0.0916 ms |       0 B |
| EnqueueDispatchRoundTrip | 2           |  3.130 ms | 7.114 ms | 0.3899 ms |  36,336 B |
| EnqueueDispatchRoundTrip | 8           | 10.342 ms | 5.492 ms | 0.3010 ms | 703,264 B |

Derived per-item figures (mean / 10,000 items):

| WorkerCount | ns/item | Items/sec (approx) | Allocated/item |
|-------------|--------:|-------------------:|---------------:|
| 1           |  ~211   | ~4.7M              | 0 B            |
| 2           |  ~313   | ~3.2M              | ~3.6 B         |
| 8           | ~1,034  | ~0.97M             | ~70 B          |

## Interpretation

- Single-worker FIFO dispatch is zero-allocation at ~211 ns/item round-trip.
- Allocations at WorkerCount ≥ 2 come from async `WriteAsync`/`ReadAllAsync` continuations
  under contention on the 128-capacity bounded channel (producer is frequently blocked by
  `FullMode.Wait`, so `ValueTask`s complete asynchronously and box). This is .NET
  `Channel<T>` runtime behavior, consistent with the 2026-02-06 full-suite findings.
- More workers are *slower* here because the handler is a no-op: the benchmark measures
  pure channel/dispatch overhead, and extra readers only add contention. That is the
  point of the baseline — the priority-dispatch port must be compared on this same
  contention-dominated round-trip.

## Caveats

- ShortRun with N=3 produces large Error (99.9% CI half-width) values; StdDev is the more
  representative spread. T28 should compare means and StdDev, and re-run on the same host.
- BenchmarkDotNet's MinIterationTime warning applies (iteration time 2–10 ms < 100 ms
  recommendation); acceptable for a relative before/after comparison at fixed settings.

## Raw data

- `docs/benchmarks/data/Bifrost.Benchmarks.Orchestrator.OrchestratorBaselineBenchmarks-report-github.md`
- `docs/benchmarks/data/Bifrost.Benchmarks.Orchestrator.OrchestratorBaselineBenchmarks-report.csv`
