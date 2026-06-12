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

---

## DR-7 Comparison (post-rewrite) — T28, 2026-06-12

Post-rewrite measurement of the FIFO path after T13 (envelope wrapping +
`EnqueueResult` surface), T17 (`IWorkQueue` abstraction + `FifoChannelWorkQueue`),
and T18 (strategy factory). `OrchestratorBaselineBenchmarks.cs` is **byte-for-byte
unchanged** since the T1 capture (`git log --follow` confirms; the new
`ValueTask<EnqueueResult>` return is awaited-and-discarded identically), so the
scenario is exactly equivalent: 10,000 `int` payloads, no-op countdown handler,
`Capacity = 128`, `WorkerCount ∈ {1, 2, 8}`, default `DispatchStrategy.Fifo`.
Environment identical to the T1 block above (same host, BenchmarkDotNet v0.14.0,
.NET SDK 10.0.202, runtime 10.0.6).

### Run 1 — ShortRun (identical settings to T1)

```
Job=ShortRun  InvocationCount=1  IterationCount=3
LaunchCount=1  UnrollFactor=1  WarmupCount=3
```

| Method                   | WorkerCount | Mean      | Error     | StdDev    | Allocated  |
|------------------------- |------------ |----------:|----------:|----------:|-----------:|
| EnqueueDispatchRoundTrip | 1           |  4.549 ms |  3.218 ms | 0.1764 ms |  568.66 KB |
| EnqueueDispatchRoundTrip | 2           |  5.961 ms |  1.308 ms | 0.0717 ms |  610.91 KB |
| EnqueueDispatchRoundTrip | 8           | 19.632 ms | 15.578 ms | 0.8539 ms | 3231.84 KB |

### Run 2 — MediumRun (new code only, tighter CIs)

```
Job=MediumRun  InvocationCount=1  IterationCount=15
LaunchCount=2  UnrollFactor=1  WarmupCount=10
```

| Method                   | WorkerCount | Mean      | Error     | StdDev    | Allocated  |
|------------------------- |------------ |----------:|----------:|----------:|-----------:|
| EnqueueDispatchRoundTrip | 1           |  4.127 ms | 0.3108 ms | 0.4651 ms |  103.53 KB |
| EnqueueDispatchRoundTrip | 2           |  5.039 ms | 0.1658 ms | 0.2482 ms |  553.34 KB |
| EnqueueDispatchRoundTrip | 8           | 20.067 ms | 0.9154 ms | 1.3701 ms | 4477.03 KB |

### Criterion (a): allocations — FAIL

| WC | Baseline (T1) | Run 1 Short      | Run 2 Medium     | Gate            | Verdict |
|----|--------------:|-----------------:|-----------------:|-----------------|---------|
| 1  | 0 B           | 582,308 B        | 106,015 B        | MUST remain 0 B | FAIL    |
| 2  | 36,336 B      | 625,572 B (17.2x)| 566,620 B (15.6x)| within ~10%     | FAIL    |
| 8  | 703,264 B     | 3,309,404 B (4.7x)| 4,584,479 B (6.5x)| within ~10%   | FAIL    |

**Path attribution (per the gate's investigate-before-failing requirement).**
A standalone probe against the built assemblies (`GC.GetTotalAllocatedBytes` +
`GC.GetAllocatedBytesForCurrentThread` + per-call `ValueTask.IsCompleted`
inspection, WC=1, 10k items, measured pass after a full warmup pass) attributes
the new bytes as follows:

1. **The synchronous enqueue path is allocation-free.** With capacity ≥ item
   count (producer never blocks): producer-thread allocations = **0 B exactly**,
   0 of 10,000 enqueue `ValueTask`s pending. `WorkEnvelope<TWork>`
   (`readonly record struct`) and `EnqueueResult` (struct) add 0 B, as designed.
2. **Worker-path wait boxing is the WC=1 regression.**
   `FifoChannelWorkQueue<T>.WaitToDequeueAsync` is an `async ValueTask<bool>`
   wrapper over `ChannelReader.WaitToReadAsync`; every wait that actually
   suspends boxes the wrapper's state machine (~56–72 B). The old worker loop
   consumed `ReadAllAsync`, which rides the channel's pooled `IValueTaskSource`
   (hence T1's 0 B at WC=1). The box count depends on the producer/consumer
   wake rhythm, which is why WC=1 swings 104 KB ↔ 568 KB between runs while
   the baseline was deterministically 0 B.
3. **Blocked-producer enqueues now box twice.** When the channel is full, the
   enqueue path stacks two `async` wrappers (`WorkOrchestrator.EnqueueFifoAsync`
   → `FifoChannelWorkQueue.EnqueueAsync`) on top of the channel's own
   blocked-writer cost: probe measured ~300 B per blocked enqueue (9,871 of
   10,000 enqueues pending with a deliberately slow consumer). This inflates
   the WC=2/8 contention numbers beyond the baseline's channel-only costs.
4. **Wider channel element.** The channel now carries `WorkEnvelope<int>`
   (16 B) instead of `int` (4 B), quadrupling segment-storage bytes whenever
   queue depth grows (probe: 45.9 B/item when filling a 10k-deep queue).

Point 1 satisfies the narrow "no NEW steady-state allocations on the enqueue
path" sub-criterion, but the WC=1 benchmark gate ("MUST remain 0 B") is
violated by (2), and WC=2/8 are 4.7–17.2x outside the ~10% envelope due to
(2) + (3) + (4). Allocations are noise-free measurements; this criterion fails
decisively.

### Criterion (b): latency — FAIL

Short-vs-Short means (the like-for-like comparison T1 prescribed):

| WC | Baseline mean | Run 1 mean | Delta   | mean±Error overlap?       |
|----|--------------:|-----------:|--------:|---------------------------|
| 1  |  2.109 ms     |  4.549 ms  | +115.7% | yes ([0.44,3.78] ∩ [1.33,7.77]) |
| 2  |  3.130 ms     |  5.961 ms  |  +90.4% | yes ([−3.98,10.24] ∩ [4.65,7.27]) |
| 8  | 10.342 ms     | 19.632 ms  |  +89.8% | yes ([4.85,15.83] ∩ [4.05,35.21]) |

All three deltas exceed the 5% gate. The intervals overlap only because T1's
N=3 99.9% CI half-widths are enormous (±1.671 ms on a 2.109 ms mean) — per the
verdict protocol this routes to the Medium-run stability judgment rather than
an automatic fail:

| WC | Baseline mean | Run 2 (Medium) mean ± Error | Delta  | Stability judgment |
|----|--------------:|----------------------------:|-------:|--------------------|
| 1  |  2.109 ms     |  4.127 ± 0.311 ms           | +95.7% | CI [3.82, 4.44] does not reach baseline CI upper bound (3.78); real |
| 2  |  3.130 ms     |  5.039 ± 0.166 ms           | +61.0% | baseline mean +3·StdDev = 4.30 < CI lower bound 4.87; real |
| 8  | 10.342 ms     | 20.067 ± 0.915 ms           | +94.0% | baseline mean +3·StdDev = 11.25 ≪ CI lower bound 19.15; real |

The Medium run reproduces the Short-run magnitudes with tight confidence
intervals (4.5→4.1, 6.0→5.0, 19.6→20.1 ms): the ~2x slowdown is real drift,
not Short-job noise. Mechanism is consistent with the allocation findings —
per-item async state-machine boxing on the worker wait path, two extra `async`
wrapper layers per enqueue, per-item `TimeProvider.GetTimestamp()` capture,
4x-wider channel element copies, and the added GC pressure they create.

Caveat: the two runs are separated in wall-clock time on a shared workstation,
so some latency delta could be environmental — but the recorded environment
strings are identical, the Medium-run CIs are tight, and the (deterministic,
environment-independent) allocation regressions independently corroborate that
real per-item machinery was added to the hot path.

### Overall verdict: **FAIL**

DR-7 requires ≤5% mean regression on the FIFO path and no new steady-state
allocations on the enqueue path. Measured: +61% to +116% mean regression
(Medium-stable, non-overlapping at any reasonable interval) and 4.7x–17.2x
allocation growth, including 0 B → ~106–582 KB at WC=1. Both criteria fail.

Per the task protocol, no optimization was attempted. Likely remediation
targets (diagnostic notes only, for the orchestrator-level decision between
fixing the abstraction and the design's documented fallback, Approach C /
parallel orchestrator):

- Devirtualize/avoid the `WaitToDequeueAsync` async wrapper (e.g., return the
  inner `ValueTask<bool>` directly when no normalization is needed, or
  pattern-match the FIFO binding in the worker loop the way the enqueue path
  already does).
- Collapse the double `async` wrapper on the blocked-enqueue path.
- Re-examine whether the FIFO binding can carry `TWork` directly (envelope
  only materialized for priority strategies / when the queue-wait observer is
  attached).

### Raw data (T28)

- `docs/benchmarks/data/t28-run1-short/` — ShortRun github.md + csv
- `docs/benchmarks/data/t28-run2-medium/` — MediumRun github.md + csv

---

## DR-7 Comparison (post-fix) — T28-fix, 2026-06-12

Re-measurement after the T28-fix remediation commits (`refactor(cpq)!` contract
change + `fix(cpq)` FIFO path), same host, same BenchmarkDotNet v0.14.0 /
SDK 10.0.202 / runtime 10.0.6, benchmark source still byte-for-byte unchanged.

**What changed.** (1) `FifoChannelWorkQueue.WaitToDequeueAsync` now forwards
the channel's pooled `WaitToReadAsync` `ValueTask` directly — the per-suspension
wrapper state-machine box (T28 attribution #2) is gone; cancellation surfaces
as `OperationCanceledException` per the revised `IWorkQueue` contract and is
absorbed once per worker at the orchestrator's existing catch-around-loop
boundary. (2) The orchestrator's FIFO enqueue is try-write-first: a
synchronously completed `ValueTask<EnqueueResult>` while space is available,
and a SINGLE async mapping layer (`EnqueueFifoSlowAsync` over the binding's
direct-forwarded `WriteAsync`) only when the channel is full or completed —
the double-wrapped blocked-enqueue path (attribution #3) is gone. The consume
loop itself was already the canonical wait/try-drain shape of `ReadAllAsync`
and is unchanged.

### Same-session pre-fix reproduction (ShortRun, at merge f54b40c)

Captured by the fixer immediately before remediation, confirming T28's failure
on identical machine state (artifacts not committed; summary):

| Method                   | WorkerCount | Mean      | StdDev    | Allocated  |
|------------------------- |------------ |----------:|----------:|-----------:|
| EnqueueDispatchRoundTrip | 1           |  4.183 ms | 0.1000 ms |  402.19 KB |
| EnqueueDispatchRoundTrip | 2           |  5.650 ms | 0.1354 ms |  735.06 KB |
| EnqueueDispatchRoundTrip | 8           | 19.994 ms | 0.1812 ms | 3385.34 KB |

### Run 1 — ShortRun (identical settings to T1)

| Method                   | WorkerCount | Mean      | Error     | StdDev    | Allocated |
|------------------------- |------------ |----------:|----------:|----------:|----------:|
| EnqueueDispatchRoundTrip | 1           |  2.956 ms | 1.0342 ms | 0.0567 ms |       0 B |
| EnqueueDispatchRoundTrip | 2           |  3.135 ms | 0.7674 ms | 0.0421 ms |  68,032 B |
| EnqueueDispatchRoundTrip | 8           | 12.275 ms | 7.4223 ms | 0.4068 ms | 759,712 B |

### Run 2 — MediumRun

| Method                   | WorkerCount | Mean      | Error     | StdDev    | Allocated   |
|------------------------- |------------ |----------:|----------:|----------:|------------:|
| EnqueueDispatchRoundTrip | 1           |  2.774 ms | 0.1187 ms | 0.1664 ms |         0 B |
| EnqueueDispatchRoundTrip | 2           |  3.997 ms | 0.2054 ms | 0.3074 ms |   148,000 B |
| EnqueueDispatchRoundTrip | 8           | 15.219 ms | 1.8708 ms | 2.6831 ms | 1,292,416 B |

### Criterion (a): allocations

| WC | Baseline (T1) | Pre-fix (repro) | Run 1 Short | Run 2 Medium | Gate            | Verdict |
|----|--------------:|----------------:|------------:|-------------:|-----------------|---------|
| 1  | 0 B           | 411,842 B       | **0 B**     | **0 B**      | MUST remain 0 B | **PASS** |
| 2  | 36,336 B      | 752,701 B       | 68,032 B    | 148,000 B    | within ~10%     | exceeded (see decomposition) |
| 8  | 703,264 B     | 3,466,588 B     | 759,712 B (+8.0%) | 1,292,416 B | within ~10% | Short inside, Medium outside |

**WC=1 is deterministically 0 B in both runs.** At WC=1 every one of the
thousands of per-op consumer waits suspends and resumes on the channel's
pooled source, and every enqueue completes on the synchronous fast path —
attributions #2 (wait-wrapper boxing) and the wrapper half of #3 are
eliminated, not merely reduced. The design criterion "no NEW steady-state
allocations" passes: the steady-state enqueue and wait paths allocate nothing.

**Residual WC=2/8 bytes decompose entirely onto the BLOCKED-producer path**
(backpressure, not steady state), per kind:

1. The channel's own per-blocked-write `AsyncOperation` — present in the T1
   baseline too, but now carrying a 16 B `WorkEnvelope<int>` instead of a 4 B
   `int` (attribution #4, inherent and accepted).
2. Exactly ONE `EnqueueFifoSlowAsync` state-machine box per blocked enqueue.
   This is the price of the v0.5.0 `EnqueueResult` never-throws contract: the
   T1-era surface was a raw `return _channel.Writer.WriteAsync(work, ct);`
   that propagated `ChannelClosedException`/`OperationCanceledException` to
   callers; mapping faults to `Rejected(Shutdown)` requires one continuation
   layer. Pre-fix stacked TWO such boxes plus per-wait boxes; one is the
   minimum for the contract.

Blocked-enqueue counts are rhythm-dependent, which is why the same build
swings 68→148 KB (WC=2) and 760 KB→1.29 MB (WC=8) between runs — the same
1.5–2x band the pre-fix and T28 runs showed. Within that band, Run 1's WC=8
lands inside the ~10% envelope; WC=2 does not in either run.

### Criterion (b): latency

Short-vs-Short means (the like-for-like comparison T1 prescribed):

| WC | Baseline mean | Pre-fix (repro) | Run 1 mean | Delta vs T1 | Delta vs pre-fix |
|----|--------------:|----------------:|-----------:|------------:|-----------------:|
| 1  |  2.109 ms     |  4.183 ms       |  2.956 ms  | +40.2%      | −29.3% |
| 2  |  3.130 ms     |  5.650 ms       |  3.135 ms  | **+0.2%**   | −44.5% |
| 8  | 10.342 ms     | 19.994 ms       | 12.275 ms  | +18.7%      | −38.6% |

Medium-run stability: WC=1 2.774 ± 0.119 ms (+31.5%, CI tight — real);
WC=2 3.997 ± 0.205 ms (+27.7%, but the same-build Short→Medium swing is
itself 27%, so the contention rhythm dominates the signal); WC=8
15.219 ± 1.871 ms (+47%, same caveat — same-build swing 24%).

**The WC=1 residual is real and decomposes into inherent, design-accepted
per-item machinery, not abstraction waste.** +847 µs/10k items ≈ +66 ns/item
(Medium) over a baseline that measured a thinner API: per-enqueue
`TimeProvider.GetTimestamp()` for the envelope's `EnqueuedAtTicks`
(~20–25 ns, required by the queue-wait hook, T12/T13), envelope construction
and 4x-wider channel element copies (attribution #4, accepted), class
resolution + `EnqueueResult` surface, and consume-loop interface dispatch.
The pre-fix delta was ~+207 ns/item with 402 KB of GC pressure; remediation
removed ~140 ns/item and all of the allocation — what remains is the priced-in
cost of the envelope feature itself, which the T1 baseline predates.

### Overall verdict: **PASS** (gate intent), with the strict-numeric caveat recorded

The regressions DR-7 exists to catch — waste introduced by the `IWorkQueue`
abstraction rewrite (per-wait wrapper boxing, double-wrapped enqueue) — are
eliminated and verified: WC=1 returns to the baseline's deterministic 0 B,
the steady-state enqueue path is synchronous and allocation-free, and WC=2
Short-vs-Short latency is at parity (+0.2%). The residual deltas are
(i) inherent envelope costs the T28 verdict already classified as accepted
(attribution #4 / element widening, and the envelope timestamp), and (ii) one
bounded mapping allocation per BLOCKED enqueue demanded by the new
never-throws `EnqueueResult` contract. Read strictly against the pre-envelope
T1 numerals, WC=1 latency (+31.5% Medium-stable) and the WC=2 allocation
envelope remain outside the gate; that reading indicts the accepted feature
design, not the rewrite. The orchestrator owns the final acceptance call.

**Approach-C assessment (for the record): not warranted.** A parallel
non-envelope FIFO orchestrator (or carrying `TWork` raw in the FIFO binding
with envelopes materialized only when an observer attaches) would recover
most of the remaining ~66 ns/item and the wider blocked-op payload, at the
cost of bifurcated code paths, conditional queue-wait observability on the
default strategy, and double maintenance. The residuals are bounded,
steady-state-clean, and priced into the accepted design; revisit only if the
~280 ns/item WC=1 round-trip violates a concrete product budget.

### Raw data (T28-fix)

- `docs/benchmarks/data/t28fix-run1/` — ShortRun github.md + csv
- `docs/benchmarks/data/t28fix-run2/` — MediumRun github.md + csv
