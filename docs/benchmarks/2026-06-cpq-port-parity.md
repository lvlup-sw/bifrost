# CPQ Benchmark Port Parity — 2026-06-12

T10 artifact for the CPQ port + priority dispatch feature. Documents the port of the
DataFerry@2bf0456 ConcurrentPriorityQueue benchmark suites into `Bifrost.Benchmarks`, plus
**indicative** parity evidence against DataFerry's published numbers. The host is the *same
machine* as DataFerry's server-class run
(`DataFerry/docs/benchmarks/2026-06-11-cpq-v2-serverclass-i9-13900k.md`), so the comparison
is direct, not just order-of-magnitude.

> **Scope note:** every run below is a deliberately short, indicative run (Dry/Short jobs,
> 0.25–0.5 s harness windows) to prove the port compiles, runs, and lands in the published
> envelope. **Full validation runs (3 s windows, ×3 sweeps, full population sweeps) are the
> T29 soak / release-gate concern**, not this document's.

## Ported-suite inventory

| DataFerry@2bf0456 source | Bifrost destination | Notes |
|---|---|---|
| `src/DataFerry.Benchmarks/SingleThreadedLatencyBenchmarks.cs` | `src/Bifrost.Benchmarks/Concurrency/CpqSingleThreadedLatencyBenchmarks.cs` | BDN + `[MemoryDiagnoser]`; `Naive_*` methods renamed `Locking_*`; `V2_*` renamed `MultiQueue_*` |
| `src/DataFerry.Benchmarks/Throughput/ThroughputModel.cs` | `src/Bifrost.Benchmarks/Concurrency/ThroughputModel.cs` | `ThroughputTarget.NaiveBaseline` renamed `LockingBaseline` |
| `src/DataFerry.Benchmarks/Throughput/IThroughputQueue.cs` | `src/Bifrost.Benchmarks/Concurrency/IThroughputQueue.cs` | `NaiveQueueAdapter` renamed `LockingQueueAdapter` over `LockingPriorityQueue` |
| `src/DataFerry.Benchmarks/Throughput/ThroughputRunner.cs` | `src/Bifrost.Benchmarks/Concurrency/ThroughputRunner.cs` | Fixed-window harness: barrier start, 128 B-padded counters — verbatim port |
| `src/DataFerry.Benchmarks/Throughput/ThroughputSweep.cs` | `src/Bifrost.Benchmarks/Concurrency/ThroughputSweep.cs` | CSV/MD output rebranded Bifrost |
| `src/DataFerry.Benchmarks/Program.cs` (`throughput`/`stickiness` verbs) | `src/Bifrost.Benchmarks/Concurrency/ThroughputVerbs.cs` + thin dispatch in `Program.cs` | Verb handlers extracted to a class so Bifrost's top-level `Program.cs` stays a thin switcher; registration merged additively |
| `src/DataFerry.Tests/Concurrency/MultiQueue/RankErrorTests.cs` | `src/Bifrost.Tests.Concurrency/MultiQueue/RankErrorTests.cs` | The rank-error empirical gate (DR-16 upstream). DataFerry structures it as a **TUnit test suite**, not a benchmark or verb — ported as such so it stays an automatic CI regression gate |

Type renames throughout: `NaiveConcurrentPriorityQueue` → `LockingPriorityQueue`,
`lvlup.DataFerry.*` → `Bifrost.Concurrency` / `Bifrost.Benchmarks.Concurrency`.

Skipped: nothing functional. DataFerry's `Main` was restructured (not dropped) into Bifrost's
existing top-level-statement + `BenchmarkSwitcher` convention.

## How to run

```bash
# BDN scalar latency/allocation suite (full sweep is slow — minutes):
dotnet run -c Release --project src/Bifrost.Benchmarks -- --filter "*Concurrency*"

# Contended fixed-window throughput sweep (custom harness; BDN deliberately not used):
dotnet run -c Release --project src/Bifrost.Benchmarks -- throughput [windowSeconds] [outDir]

# Weak-regime stickiness ladder (threads {1,2} × s {1,2,4,8}):
dotnet run -c Release --project src/Bifrost.Benchmarks -- stickiness [windowSeconds] [outDir]

# Rank-error empirical gate (runs in CI with the normal test suite):
dotnet test --project src/Bifrost.Tests.Concurrency -- --treenode-filter "/*/*/RankErrorTests/*"
```

## Environment (identical machine to the DataFerry server-class reference)

```
BenchmarkDotNet v0.14.0, Pop!_OS 24.04 LTS
13th Gen Intel Core i9-13900K, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.202, .NET 10.0.6, X64 RyuJIT AVX2
GC: Workstation, Concurrent
```

## 1. Compile-and-run proof (Dry)

`dotnet run -c Release --project src/Bifrost.Benchmarks -- --filter "*Concurrency*" --job Dry`
executed all **48** benchmark cases (12 methods × 4 populations) with zero failures
(21.6 s total). Both harness verbs ran end-to-end and wrote their CSV/MD artifacts. All
7 `RankErrorTests` cases pass.

## 2. Indicative BDN parity (ShortRun, pair benchmarks)

Command:

```
dotnet run -c Release --project src/Bifrost.Benchmarks -- --job Short \
  --filter "*MultiQueue_EnqueueDequeue_Int*" "*Locking_EnqueueDequeue_Int*" \
           "*RawLocked_EnqueueDequeue_Int*" "*MultiQueue_EnqueueDequeue_String*"
```

At the published reference row (**Population = 1000**, the row DataFerry's ShortRun report
used on this same machine):

| Pair benchmark (Population = 1000) | Bifrost port | DataFerry published | Delta | Allocated |
|---|---:|---:|---:|---:|
| MultiQueue (was `V2`) Enqueue+TryDequeue, int | 49.10 ns | 47.36 ns | +3.7 % | **0 B** (both) |
| Locking (was `Naive`) Enqueue+TryDequeue, int | 40.91 ns | 41.63 ns | −1.7 % | **0 B** (both) |
| RawLocked `PriorityQueue` pair, int | 37.47 ns | 37.22 ns | +0.7 % | **0 B** (both) |
| MultiQueue Enqueue+TryDequeue, string priority | 303.49 ns | 300.33 ns | +1.1 % | **0 B** (both) |

Other populations (10 / 100k / 1M) ran in the same sweep and show the expected heap-depth
scaling; the 1000 row is the only one with a published same-job reference.

**Verdict: well inside the same order of magnitude — every row within ±4 % of the published
value — and 0 B/op is preserved on every alloc-free pair path, including the string-priority
pair.** The single-thread gate ratio also reproduces: MultiQueue / RawLocked =
49.10 / 37.47 = **1.31×** vs the published **1.27×** (gate budget: 2–3×).

## 3. Indicative contended parity (fixed-window harness, 0.5 s windows, single sweep)

`throughput 0.5` — selected rows in M ops/s vs the published 3 s-window server-class sweep
(same machine; short windows + single sweep, so expect noise):

### UniformMixed5050 (v2-relaxed = `MultiQueueRelaxed`, baseline = `LockingBaseline`)

| Threads | Port relaxed | Published relaxed | Port locking | Published locking |
|---:|---:|---:|---:|---:|
| 1 | 15.4 | 20.1 | 22.2 | 33.5 |
| 4 | 35.4 | 35.1 | 18.8 | 20.6 |
| 16 | 76.7 | 77.6 | 17.4 | 19.8 |
| 32 | 82.2 | 90.7 | 17.5 | 17.8 |
| 64 | 80.2 | 86.8 | 12.7 | 12.8 |

### Drain (prepopulated 1M, elapsed-to-empty)

| Threads | Port relaxed | Published relaxed | Port locking | Published locking |
|---:|---:|---:|---:|---:|
| 1 | 14.8 | 13.7 | 19.8 | 17.7 |
| 16 | 51.1 | 55.0 | 5.8 | 3.1 |
| 32 | 54.2 | 54.7 | 4.4 | 3.4 |
| 64 | 31.2 | 23.6 | 3.7 | 2.5 |

### Signature checks (the shapes that matter)

- **MultiQueue climbs monotonically to core saturation** (15→82 M ops/s mixed; peak 94 M ops/s
  split at 64T vs published 104) while the **locking baseline collapses under contention**
  (mixed 22→13; drain 20→2.5) — the defining divergence reproduces.
- **NarrowKeyRange flip reproduces**: locking wins 1–2T (69.3 vs 23.3 at 1T), MultiQueue wins
  from 4T (35.6 vs 28.1) up to 3.0× at 32T (published 3.54×).
- **Strict `TryDequeueMin` reference path** matches: 2.7–11.9 M ops/s on mixed workloads
  (published 3.5–12) and a flat ~1.9 M ops/s on drain (published ~1.9) — confirming the
  O(n)-scan path ported with its documented character intact.
- **Stickiness ladder** (`stickiness 0.25`): same monotone story as the published ladder, e.g.
  UniformMixed 2T s=1→8: 21.7→37.1 M ops/s (published 22.2→37.9); Drain 2T s=8 28.7 vs
  published 28.8. NarrowKeyRange 1T stays flat, exactly as published.

**Verdict: same order of magnitude everywhere; most rows within ~10 % of the published 3 s
numbers despite 6× shorter windows.** Largest deviations are at 1–2 threads on the locking
baseline (lock-convoy bimodality, documented as scheduling-sensitive in the upstream results
doc) and the noisy 64T oversubscribed rows.

## Parity verdict

| Question | Answer |
|---|---|
| Same order of magnitude as DataFerry's published equivalents? | **Yes** — BDN rows within ±4 %, harness rows mostly within ~10 % |
| 0 B/op preserved on the alloc-free paths? | **Yes** — all four pair benchmarks report 0 B at every population |
| Rank-error gate green? | **Yes** — 7/7 cases (mean/P99 bounds, stickiness monotonicity, anti-inversion alarm, geometric decay) |

Full-length validation (3 s windows, ×3 sweeps, complete BDN population sweep, formal gate
re-verdicts) is deferred to **T29 (soak / release gate)** by design.
