# CPQ Buffered-vs-Unbuffered A/B + `0 B/op` Gate (DR-8, DR-1)

**Date:** 2026-06-15
**Feature:** `cpq-buffered-multiqueue` (ESA 2021 §4 per-sub-queue insertion/deletion buffers)
**Design:** [`docs/designs/2026-06-15-cpq-buffered-multiqueue.md`](../designs/2026-06-15-cpq-buffered-multiqueue.md) (DR-8 A/B gate, DR-1 `[InlineArray]` `0 B/op`)
**Spike (directional, GO):** [`docs/research/2026-06-15-cpq-buffering-spike.md`](../research/2026-06-15-cpq-buffering-spike.md)

This is the DR-8 buffered-vs-unbuffered A/B artifact. It toggles the queue's `bufferCapacity` dial
between `0` (off — bit-exact with the pre-buffering MultiQueue) and `16` (the ESA 2021 §4 optimum
`C`) and records three things the merge gate asks for:

1. the dense contended-throughput A/B (does throughput go **up at scale**?),
2. the single-thread latency-vs-heap-depth A/B (does the deep-heap slope **flatten**?),
3. the `0 B/op` confirmation for **both** a value-type (`int`,`int`) and a reference-type
   (`string`,`long`) instantiation of the buffered Enqueue+Dequeue hot path.

> **This is a DIRECTIONAL local run.** The host is an i9-13900K (32 logical / 24 physical cores),
> not the Xeon 8573C n=256/64T baseline. These numbers establish the *direction and sign* of the
> buffering effect and gate the GO/NO-GO; the formal `perf`-stat cache-misses/op A/B on the Xeon is
> deferred to **issue #30** (which also gates the eventual default-on flip of `bufferCapacity`).

## Environment

| | |
|---|---|
| Host | 13th Gen Intel Core **i9-13900K**, single socket |
| Topology | **32 logical / 24 physical cores** — hybrid: 8 P-cores (×2 HT = 16) + 8 E-cores. Past 16 worker threads the dense sweep is oversubscribed on the P-cores. |
| Sub-queues `n` | `RoundUpPow2(4 × ProcessorCount)` = `RoundUpPow2(128)` = **128**. Xeon baseline uses n=256/64T. |
| OS / Runtime | Pop!_OS 24.04 (Linux 7.0.11), .NET SDK 10.0.202, host 10.0.6, X64 RyuJIT **AVX2**, Workstation GC |
| Latency / alloc tool | BDN `CpqBufferedAbBenchmarks`, `--job Short` (1 launch × 3 warmup × 3 iters), `[MemoryDiagnoser]` |
| Throughput tool | fixed-window console harness (`ThroughputRunner`) behind the new `buffered-throughput` verb, 1 s window, `MultiQueueRelaxed`, `C ∈ {0, 16}` |
| Raw data | [`data/buffered-ab/latency-memdiag-raw.txt`](data/buffered-ab/latency-memdiag-raw.txt), [`data/buffered-ab/throughput-buffered-ab.{md,csv}`](data/buffered-ab/), reproducibility run [`data/buffered-ab/run2/throughput-buffered-ab.csv`](data/buffered-ab/run2/) |

> ### Caveats — stated, not hidden
> 1. **DIRECTIONAL vs the Xeon 8573C baseline.** This host is 32T/n=128; the Xeon baseline is
>    64T/n=256. These numbers detect the buffering effect's *direction*; they do **not** reproduce
>    absolute Xeon throughput. Final 64T parity + cache-misses/op is the **#30** follow-up.
> 2. **High-thread rows (32T, 64T) are oversubscribed** on a 32-logical-core part — absolute
>    ops/sec there is noisy and the C=0 curve plateaus/declines from contention, not from the queue.
>    The A/B *ratio* (C=16 vs. C=0 on the same host, same window) is the load-bearing number, and it
>    is reported across two independent runs to show the direction is stable, not a one-off.
> 3. **`--job Short`** (3 iters) gives a directional latency mean, not a tight CI; the StdDev column
>    is in the raw file. The `Allocated` column is exact regardless of iteration count.

---

## A — Dense contended-throughput A/B (`buffered-throughput` verb, 1 s window)

`MultiQueueRelaxed`, `C ∈ {0, 16}`, default thread ladder. Ops/sec, two independent runs; the
**Speedup** column is run-1 `C=16 / C=0`. Full table for every thread count is in the raw `.md`/`.csv`;
the high-thread regime (where the buffer's "shorten the critical section" win matters most) is below.

| Workload | Threads | C=0 ops/s (run 1) | C=16 ops/s (run 1) | Speedup | C=16 ops/s (run 2) |
|---|---:|---:|---:|---:|---:|
| UniformMixed5050 | 1 | 17.08 M | 21.65 M | **1.27×** | — |
| UniformMixed5050 | 8 | 54.52 M | 63.93 M | **1.17×** | — |
| UniformMixed5050 | 16 | 76.98 M | 94.04 M | **1.22×** | — |
| UniformMixed5050 | 32 | 83.50 M | 131.98 M | **1.58×** | 133.97 M |
| UniformMixed5050 | 64 | 80.45 M | 117.15 M | **1.46×** | 119.63 M |
| SplitProducerConsumer | 32 | 88.48 M | 116.55 M | **1.32×** | — |
| SplitProducerConsumer | 64 | 79.82 M | 108.40 M | **1.36×** | — |
| NarrowKeyRange | 32 | 81.20 M | 131.44 M | **1.62×** | 131.07 M |
| NarrowKeyRange | 64 | 78.76 M | 118.03 M | **1.50×** | 122.01 M |
| Drain | 32 | 49.62 M | 72.02 M | **1.45×** | 75.77 M |
| Drain | 64 | 39.63 M | 72.65 M | **1.83×** | 73.59 M |

**What this shows.** Buffering is throughput-positive on **every** workload and thread count, and
the win **grows at scale** — the opposite of "flat at scale." The unbuffered (C=0) curve plateaus and
then declines past 32 threads (oversubscription on a 32-logical-core part); the buffered (C=16) curve
holds up far better because the buffer shortens the under-lock critical section (pop returns
`D.front()` without sifting the arity-4 heap on the common path), so each lock holder vacates faster
and contention does not snowball. This is precisely the "raise the absolute ceiling" effect the #26
buffering spike predicted for the coherence/bandwidth-bound high-thread regime. Run 2 reproduces the
direction and magnitude (32T UniformMixed 84.0 M→134.0 M = 1.59×; 64T 81.3 M→119.6 M = 1.47×).

## B — Single-thread latency-vs-heap-depth A/B (`CpqBufferedAbBenchmarks`, `--job Short`)

Steady-state Enqueue+Dequeue pair, population held flat one-in/one-out, so each row is a true
per-population point. The buffer's win is a **deep-heap** effect — it removes the walk through a tall
arity-4 heap from the hot path — so the interesting rows are the large populations.

**Reference-element (`string`,`long`) — the write-barrier-safe path:**

| Population | C=0 (ns/pair) | C=16 (ns/pair) | Speedup | Allocated (both) |
|---:|---:|---:|---:|---:|
| 10 | 80.92 | 83.09 | 0.97× | **0 B** |
| 1 000 | 67.77 | 74.32 | 0.91× | **0 B** |
| 100 000 | 100.23 | 75.95 | **1.32×** | **0 B** |
| 1 000 000 | 115.42 | 74.49 | **1.55×** | **0 B** |

**Value-element (`int`,`int`):**

| Population | C=0 (ns/pair) | C=16 (ns/pair) | Speedup | Allocated (both) |
|---:|---:|---:|---:|---:|
| 10 | 77.92 | 80.54 | 0.97× | **0 B** |
| 1 000 | 49.88 | 49.80 | 1.00× | **0 B** |
| 100 000 | 84.25 | 87.83 | 0.96× | **0 B** |
| 1 000 000 | 107.24 | 113.17 | 0.95× | **0 B** |

**What this shows.** The reference-element path shows the deep-heap slope **flattening** exactly as
DR-8 predicted: C=0 climbs 80→115 ns as the heap deepens 10→1M, while C=16 stays flat at ~74–83 ns
across the whole range (1.55× faster at 1M). The buffer keeps the hot pop on `D.front()` and the
heap walk off the per-op path, so per-op latency stops tracking heap depth. The value-element path
is roughly neutral single-threaded at these depths (the int sift is cheap enough that the buffer's
bookkeeping ~offsets it); its win shows up in the **contended** table above (section A), where the
shortened critical section is what matters. Shallow populations (10, 1 000) are slightly slower with
buffering on — expected, since there the heap is never deep enough to amortize the buffer
bookkeeping; this is the regime the `default 0=off` knob protects until #30 confirms the flip.

## C — `0 B/op` gate (DR-1)

The `[MemoryDiagnoser]` `Allocated` column reads **`-` (0 B)** for **every** row of section B — both
the value-type (`int`,`int`) and the reference-type (`string`,`long`) instantiation, buffered (C=16)
and unbuffered (C=0). The buffered steady-state Enqueue+Dequeue introduces **no per-operation heap
allocation**: the `[InlineArray(16)]` insertion/deletion buffers are fixed inline storage and all
buffer moves go through `Span.CopyTo` / gated `Span.Clear` (DR-1, DR-4), so the steady state reuses
the inline slots and never touches the heap. The reference instantiation is the load-bearing
confirmation — a `string` element exercises the write-barrier-safe slot-clear path that an `int`
element elides, and it too is `0 B`. **Gate: PASS (value + reference).**

---

## GO / NO-GO

**GO (directional).** Throughput is up at scale on every workload (not flat — it *grows* with thread
count, 1.32×–1.83× in the high-thread regime, reproduced across two runs), the single-thread
deep-heap latency slope flattens on the reference path (1.55× at 1M population), and the `0 B/op`
gate holds for both value and reference instantiations. No rank-error regression is implicated here
(buffering preserves per-sub-queue dequeue order by construction — DR-2 differential tests, not this
benchmark, are the rank-error authority, and they are green in the merged core).

NO-GO conditions (throughput flat at scale **or** rank error past gate) are **not** met.

**Deferred to issue [#30](https://github.com/lvlup-sw/bifrost/issues/30):** the formal `perf`-stat
cache-misses/op buffered-vs-unbuffered A/B on the Xeon 8573C (n=256/64T) to confirm the win's
magnitude on the production-baseline topology, and to gate the eventual flip of `bufferCapacity`'s
default from `0` (off) to on. Until then the dial stays opt-in (`default 0=off`).
