# CPQ Transition-Bitmask — Before/After Benchmark (DR-7)

**Date:** 2026-06-14
**Feature:** `cpq-transition-bitmask` (occupancy bitmask + Phase-1.5 sparse-fallback routing)
**Design:** [`docs/designs/2026-06-14-cpq-transition-bitmask.md`](../designs/2026-06-14-cpq-transition-bitmask.md) (DR-5, DR-7)
**Spike (directional):** [`docs/research/2026-06-14-cpq-bitmask-spike.md`](../research/2026-06-14-cpq-bitmask-spike.md)

This is the **production-code** before/after artifact required by DR-7. It confirms (or refutes)
the discovery spike's directional numbers on the real `Bifrost.Concurrency` CPQ, run through the
real benchmark harness, not the spike's throwaway standalone harness.

- **before** = `main` (`74a95bb`) — no bitmask.
- **after** = `feature/cpq-transition-bitmask` (`9e4db8c`) — occupancy bitmask + Phase-1.5 routing.

Both runs were taken **in the same git worktree** by checking out each branch in turn (no nested
worktrees — avoids the BenchmarkDotNet stale-`.csproj` `NotSupportedException` hazard). Each run's
raw output was saved before switching branches. Raw data: [`data/before/`](data/before/),
[`data/after/`](data/after/).

## Environment

| | |
|---|---|
| Host | 13th Gen Intel Core **i9-13900K**, single socket, 62 GiB |
| Topology | **32 logical / 24 physical cores** — **hybrid**: 8 P-cores (×2 HT = 16) + 8/16 E-cores. |
| **AVX-512** | **ABSENT** (Raptor Lake fuses it off — confirmed in `/proc/cpuinfo`: `avx2` yes, no `avx512*`). SIMD occupancy probe is **out of scope here / deferred to Xeon**. |
| Sub-queues `n` | `RoundUpPow2(4 × Environment.ProcessorCount)` = `RoundUpPow2(128)` = **128** (4 occupancy words). Xeon baseline used n=256/64T. |
| OS / Runtime | Pop!_OS 24.04 (Linux 7.0.11), .NET SDK 10.0.202, host 10.0.6, X64 RyuJIT **AVX2**, Workstation GC |
| Latency tool | production BDN `CpqSingleThreadedLatencyBenchmarks`, `--job Short` (1 launch × 3 warmup × 3 iters), `[MemoryDiagnoser]` |
| Throughput tool | fixed-window console harness (`ThroughputRunner`), 2 s window, MultiQueueRelaxed |

> ### Caveats — stated, not hidden
> 1. **DIRECTIONAL vs the Xeon 8573C baseline.** This host is 32T/n=128; the Xeon baseline is
>    64T/n=256. These numbers detect regressions and confirm the sparse win's *direction*; they do
>    **not** reproduce absolute Xeon throughput. Final 64T parity is a Xeon follow-up, not this run.
> 2. **The 4T dense number is inherently noisy on a hybrid CPU.** Run-to-run P/E-core placement of 4
>    threads on a 8P+16E part swings ~±4% between trials. The 4T verdict is reported with this caveat
>    and pooled over 8 trials to suppress placement noise.
> 3. **No AVX-512** → the SIMD occupancy-read variant cannot be measured here (deferred to Xeon).

---

## V-SPARSE — single-thread Enqueue+Dequeue pair latency by population

Production BDN `MultiQueue_EnqueueDequeue_Int`, n=128. Two `--job Short` runs each; values are the
two-run mean (run-to-run StdDev < 0.6 ns). Raw: `data/{before,after}/latency-multiqueue-pair-raw*.txt`.

| Population | before (ns/pair) | after (ns/pair) | speedup | Δ | Allocated (both) |
|---:|---:|---:|---:|---:|---:|
| **10** (sparse, ~92% empty) | **92.5** | **79.6** | **1.16×** | **−13.9%** | **0 B** |
| 1 000 (steady) | 48.7 | 48.7 | 1.00× | +0.0% | 0 B |
| 100 000 | 80.3 | 81.0 | 0.99× | +0.9% | 0 B |
| 1 000 000 | 107.4 | 106.6 | 1.01× | −0.8% | 0 B |

**What reproduced and what did not.** The bitmask helps the sparse case and is invisible everywhere
else — the *direction* the design predicted. But the **magnitude did not reproduce**: the spike's
177→100 ns (1.76×) was measured in its standalone harness; on production code the **before pop-10 is
already ~92 ns, not the documented ~177/260 ns cliff.** The production BDN pair (enqueue *then*
dequeue against a population-10 queue) rarely drains a sub-queue deep enough to spend the full
sampling budget and fall into the O(n) scan, so the "before" never pays the full cliff the spike (and
the Xeon doc) provoked. The win is therefore real but modest here: **1.16× (−14%), 0 B/op preserved.**

> **Verdict — V-SPARSE: PASS (direction), but the cliff did not reproduce at spike magnitude.**
> Pop-10 pair latency **92.5 → 79.6 ns (1.16×, −14%)**, `0 B/op`. The spike's 1.76× / ~177 ns cliff
> is a standalone-harness artifact; the production harness's "before" is already ~92 ns. The
> larger win likely needs the genuinely drain-dominated path (see Drain, below) or the Xeon's
> n=256 width — recommend the Xeon follow-up quantify the true cliff.

---

## V-DENSE-NOREG — dense throughput (regression guard) + steady-state latency

### Dense throughput (MultiQueueRelaxed, 2 s window, pooled 8 trials, median)

Pooled across the 3-trial and 5-trial confirmation runs (8 samples/cell) to suppress hybrid-CPU
placement noise. Raw: `data/{before,after}/throughput-raw.csv` + `…-dense-confirm-raw.csv`.

| Workload | Threads | before (M ops/s) | after (M ops/s) | Δ (median) | within ±2%? |
|---|---:|---:|---:|---:|:--:|
| UniformMixed5050 | 4 | 34.05 | 33.50 | **−1.61%** | yes |
| UniformMixed5050 | 16 | 77.94 | 78.17 | +0.31% | yes |
| UniformMixed5050 | 32 | 83.33 | 83.02 | −0.38% | yes |
| NarrowKeyRange | 4 | 35.99 | 36.23 | +0.67% | yes |
| NarrowKeyRange | 16 | 79.10 | 79.36 | +0.33% | yes |
| NarrowKeyRange | 32 | 83.30 | 82.16 | **−1.37%** | yes |

Every dense cell at 16T and 32T is within the ±2% DR-5 tolerance. (An initial 3-trial-only pass
showed NarrowKeyRange-32T at −3.96% and UniformMixed-4T at −2.48%; both collapsed back inside ±2%
once pooled to 8 trials — they were trial noise, not regressions.)

### Steady-state / dense latency + allocation

From the V-SPARSE table: populations 1k / 100k / 1M are **+0.0% / +0.9% / −0.8%** (within noise) and
`0 B/op` at every population. The bitmask adds no allocation and no measurable steady-state cost —
consistent with "never written on the dense path, sits dormant in cache."

> **Verdict — V-DENSE-NOREG: PASS.** Dense throughput within ±2% at 16T/32T (and at 4T once
> placement noise is pooled out); steady-state pair latency within noise at populations 1k–1M;
> **`0 B/op` preserved at every population.** No dense regression.

---

## V-4T — confirm-or-refute the spike's 4T dense −5%

The spike reported a real, consistent **−5.0%** at 4 threads in its standalone harness and flagged it
as a gating item with an unconfirmed cause (atomic RMW vs. extra branch/field-load vs. measurement
artifact). DR-7's job: does it reproduce on production code?

| Workload | before (M ops/s) | after (M ops/s) | Δ (pooled median) | 4T trial sd |
|---|---:|---:|---:|---:|
| UniformMixed5050 @ 4T | 34.05 | 33.50 | **−1.61%** | ~3.8% |
| NarrowKeyRange @ 4T | 35.99 | 36.23 | **+0.67%** | ~1.0% |

The spike's −5% **does not reproduce on production code.** UniformMixed-4T is −1.61% (well inside the
~±4% run-to-run noise this hybrid CPU shows at 4 threads); NarrowKeyRange-4T is actually slightly
*positive* (+0.67%). The Drain-4T workload is −0.77% (also noise). There is **no production-code 4T
dense regression above the ±2% gate** — the spike's −5% reads as a standalone-harness / hybrid-CPU
placement artifact, exactly the caveat the design folded into DR-5/DR-7.

> **Verdict — V-4T: REFUTED on production code (with hybrid-CPU caveat).** Production 4T dense is
> **UniformMixed −1.61% / NarrowKeyRange +0.67%** — inside ±2% and inside 4T placement noise. The
> spike's −5% did not reproduce. **No mitigation needed.** The 4T number is the noisiest cell on this
> hybrid part; definitive 4T/64T parity is the Xeon follow-up, but nothing here gates the ship.

---

## Drain (drain-dominated — routing-replaces-scan path)

Pure dequeue-only drain of a 1 M-element shared queue (MultiQueueRelaxed, 3 trials, median). This is
the workload where Phase-1.5 routing is expected to replace O(n) scans as the queue empties.

| Threads | before (M ops/s) | after (M ops/s) | Δ |
|---:|---:|---:|---:|
| 4 | 25.78 | 25.59 | −0.77% (noise) |
| 16 | 53.11 | 55.28 | **+4.09%** |
| 32 | 54.86 | 55.20 | +0.62% |

After ≥ before at 16T/32T (consistent with routing replacing scan fall-throughs as sub-queues
drain); 4T within noise. Supports DR-7's "after-throughput ≥ before on the drain workload."

---

## Summary verdicts

| Verdict | Result | Number |
|---|---|---|
| **V-SPARSE** | **PASS (direction); cliff did not reproduce at spike magnitude** | pop-10 pair **92.5 → 79.6 ns (1.16×, −14%)**, `0 B/op`. (Spike's 1.76×/177 ns is a standalone-harness artifact; production "before" already ~92 ns.) |
| **V-DENSE-NOREG** | **PASS** | Dense 16T/32T within ±2% (UniformMixed −0.38%/+0.31%; NarrowKeyRange −1.37%/+0.33%); latency 1k–1M within noise; **`0 B/op`**. |
| **V-4T** | **REFUTED on production code** | 4T dense **UniformMixed −1.61% / NarrowKeyRange +0.67%** — inside ±2% and inside 4T hybrid-placement noise. Spike's −5% did not reproduce; no mitigation needed. |

**Bottom line.** On production code, the bitmask is a clean, contract-preserving, zero-allocation
change: it gives a modest sparse win, no dense regression, and the spike's worrying 4T −5% does not
reproduce. The headline sparse magnitude (1.76×, ~177 ns cliff) is a property of the spike's
standalone harness, not the production code/harness — the production "before" is already ~92 ns at
pop-10. Quantifying the *full* cliff (and 64T parity) is the deferred Xeon 8573C follow-up.

## Provenance / reproducibility

- Single-thread latency: production `CpqSingleThreadedLatencyBenchmarks.MultiQueue_EnqueueDequeue_Int`,
  `--job Short --memory true`. Two runs/branch (raw in `data/`).
- Throughput: a throwaway driver `<Reference>`-ing the per-branch-built `Bifrost.Concurrency.dll` and
  re-using the (branch-identical) harness files, restricted to the cells this verdict needs. See
  [`data/_driver/README.md`](data/_driver/README.md). The full `throughput` verb sweep would also be
  comparable (harness byte-identical on both branches) but is far larger than the time box.
- Method per design DR-7: same worktree, checkout-and-rerun, raw saved before each branch switch, no
  nested worktrees.
