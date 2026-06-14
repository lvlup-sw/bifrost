# CPQ Consumer-Shaped Soak (DR-8) — 2026-06-12

T29 artifact for the CPQ port + priority dispatch feature. DataFerry's published results cover
contended micro-benchmarks; the regime Bifrost actually lives in — 1–8 workers, seconds-long
work items, low queue contention — is exactly where the lock baseline is competitive ("loses
only where the baseline's critical section is too cheap to convoy"). This soak produces the
numbers behind the README's strategy-selection guidance ("ship whichever measures better and
keep both honest" — issue #17).

> **Scope note:** every number below comes from deliberately short **45 s smoke runs** — enough
> to prove the harness works end-to-end and to give an indicative reading, **NOT the release
> artifact**. The release-gate runs are the 600 s commands in
> [Release-gate runs](#release-gate-runs-600-s) below, wired into `.github/workflows/soak.yml`
> (manual dispatch + nightly).

## Harness

`soak` CLI verb in `Bifrost.Benchmarks` (`SoakVerbs`/`SoakSweep`/`SoakRunner`/`SoakModel` under
`src/Bifrost.Benchmarks/Concurrency/`), plain console code — not BenchmarkDotNet, which has no
notion of a long mixed-arrival window with seconds-long simulated work items. It drives **both**
internal priority bindings (`LockingPriorityWorkQueue`, `ConcurrentPriorityWorkQueue`) via the
existing `InternalsVisibleTo` grant, constructing envelopes directly with the same
timestamp-at-enqueue pattern the orchestrator uses.

Workload shape (all defaults overridable via flags — see `soak` usage):

| Knob | Default | Notes |
|---|---|---|
| Bindings | both | `--binding both\|locking\|multiqueue` |
| Workers | {2, 8} | one full run per binding × worker count |
| Window | 600 s | `--seconds` override (smoke runs use 45) |
| Work-item duration | log-uniform 50 ms – 5 s | `Task.Delay`-simulated — the soak measures QUEUE behavior, not CPU |
| Arrival mix | Interactive 20 % / Default 30 % / Batch 50 % | per-class producers with exponential inter-arrival gaps |
| Arrival rate | feedback-steered | a pure open-loop Poisson rate cannot hold a finite queue at mid occupancy, so a slow controller (0.5 s tick, ×1.10 below band / ×0.85 above) nudges the total rate toward the 40–60 % occupancy band |
| Capacity | 128 | DR-6 default watermarks: Batch 0.90 / Default 0.95 / Interactive 1.0 |
| Batch bursts | 64 items every 30 s | back-to-back enqueues that push a mid-band queue into watermark territory, exercising the admission shed |
| Boost window | 30 s | the DR-5 starvation bound the probe checks |
| Seed | 42 | per-producer derived streams |

Measured per class: queue-wait p50/p95/p99/max (envelope `EnqueuedAtTicks` →
`GetElapsedTime` at dequeue), dispatch fairness (dispatch share vs offered share), rejection
counts. Per run: occupancy tracking against the band, allocation stability
(`GC.GetTotalAllocatedBytes` per-interval rate + gen 0/1/2 counts), and the starvation probe.

**Starvation probe semantics.** The DR-5 bound is on priority-induced *overtaking*: an item that
has waited longer than the boost window outranks every fresh arrival. An admitted Batch item's
absolute wait is therefore bounded by the boost window **plus** the drain time of the depth in
front of it, so the probe checks max observed Batch wait against the depth-adjusted ceiling
`boostWindow + capacity × meanWork / workers` (98.8 s at 2 workers, 47.2 s at 8, with the
defaults), reporting the raw 30 s window alongside for context.

## INDICATIVE results — 45 s smoke runs (NOT release numbers)

Host: 32-core x64, Pop!\_OS 24.04, .NET 10.0.6, workstation GC (same machine as the
[port-parity runs](2026-06-cpq-port-parity.md)). One invocation,
`soak --seconds 45`, four runs in sweep order. Caveats inherent to 45 s windows: the ramp to
the occupancy band eats a third of the window (inflating absolute waits and depressing in-band
percentages, especially at 2 workers where drain is slow), the window is only 1.5× the boost
window, and per-class sample counts are tens-to-hundreds — read the *comparisons*, not the
absolute values.

### Per-class queue-wait and fairness (INDICATIVE)

| Binding | Workers | Class | Offered | Rejected | Dispatched | Offered % | Dispatch % | p50 (ms) | p95 (ms) | p99 (ms) |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| LockingPriority | 2 | Interactive | 34 | 0 | 33 | 15.7 | 41.3 | 4,658 | 8,417 | 8,811 |
| LockingPriority | 2 | Default | 37 | 0 | 9 | 17.1 | 11.3 | 28,517 | 33,464 | 33,464 |
| LockingPriority | 2 | Batch | 146 | 51 | 38 | 67.3 | 47.5 | 28,769 | 39,394 | 39,419 |
| LockingPriority | 8 | Interactive | 85 | 0 | 85 | 19.2 | 26.1 | 327 | 1,780 | 2,013 |
| LockingPriority | 8 | Default | 109 | 13 | 69 | 24.7 | 21.2 | 13,938 | 18,578 | 18,857 |
| LockingPriority | 8 | Batch | 248 | 21 | 172 | 56.1 | 52.8 | 9,813 | 17,876 | 18,801 |
| MultiQueuePriority | 2 | Interactive | 34 | 0 | 25 | 15.7 | 32.5 | 7,578 | 30,964 | 32,252 |
| MultiQueuePriority | 2 | Default | 37 | 0 | 11 | 17.1 | 14.3 | 21,292 | 35,839 | 35,839 |
| MultiQueuePriority | 2 | Batch | 146 | 55 | 41 | 67.3 | 53.2 | 10,426 | 38,196 | 40,129 |
| MultiQueuePriority | 8 | Interactive | 88 | 0 | 76 | 19.6 | 23.2 | 4,484 | 17,798 | 21,656 |
| MultiQueuePriority | 8 | Default | 109 | 16 | 72 | 24.3 | 22.0 | 8,480 | 21,916 | 22,765 |
| MultiQueuePriority | 8 | Batch | 251 | 24 | 179 | 56.0 | 54.7 | 4,320 | 20,764 | 22,954 |

### Run-level (INDICATIVE)

| Binding | Workers | Occ mean % | In-band % | Alloc MiB/min (mean, min–max) | Gen0/1/2 | Max batch wait (s) | Depth-adj bound (s) | Bound |
| --- | ---: | ---: | ---: | --- | --- | ---: | ---: | --- |
| LockingPriority | 2 | 72.7 | 4.5 | 0.09 (0.00–0.52) | 0/0/0 | 39.4 | 98.8 | HELD |
| LockingPriority | 8 | 50.8 | 21.3 | 0.11 (0.00–0.19) | 0/0/0 | 18.8 | 47.2 | HELD |
| MultiQueuePriority | 2 | 71.2 | 3.4 | 0.10 (0.00–0.36) | 0/0/0 | 40.1 | 98.8 | HELD |
| MultiQueuePriority | 8 | 49.3 | 29.2 | 0.21 (0.00–0.37) | 0/0/0 | 25.1 | 47.2 | HELD |

### Reading

- **Queue-wait discrimination — the locking binding wins this regime, clearly.** Interactive
  p95: **8.4 s vs 31.0 s** at 2 workers, **1.8 s vs 17.8 s** at 8. The locking binding's exact
  min-key dequeue delivers the class separation the virtual-time key promises (8-worker p50s:
  0.33 s / 13.9 s / 9.8 s for Interactive/Default/Batch). The MultiQueue's relaxed two-choice
  dequeue has an expected rank error of (5/6)·n with n ≈ 4 × processor count — **n ≈ 128
  sub-queues on this host, against a queue that only ever holds ≤ 128 items**, so the rank
  error is the same order as the entire population and class ordering is largely washed out
  (8-worker p50s: 4.5 s / 8.5 s / 4.3 s — Batch effectively ties Interactive). This is the
  design's competitive finding made concrete: relaxation buys contended throughput, and this
  regime has none to sell.
- **Fairness and shed order — identical and correct on both.** Zero Interactive rejections
  anywhere; Batch shed first and hardest (the bursts working as intended), Default shed a
  little during bursts at 8 workers. Dispatch share redistributes from Batch/Default toward
  Interactive exactly as the boost intends — more sharply under locking, consistent with the
  ordering fidelity above.
- **Allocation stability — both flat.** ≤ 0.21 MiB/min process-wide (harness included),
  **zero** gen 0/1/2 collections in every run.
- **Starvation bound — held on both** bindings at both worker counts (max Batch wait within
  the depth-adjusted ceiling; at 8 workers it is comfortably inside even the raw 30 s window).
- Occupancy in-band percentages are low at 2 workers because the 45 s window is mostly ramp +
  burst recovery at that drain rate; the 600 s runs are expected to settle properly. Verify in
  the release runs.

## Strategy-selection guidance — DRAFT (pending 600 s release runs)

For Bifrost's consumer-shaped regime (1–8 workers, seconds-long work items, low queue
contention): **prefer the locking binding (`LockingPriorityWorkQueue`) as the priority
strategy's default.** At this regime it delivers materially better interactive queue-wait
percentiles and visibly truthful class ordering, with identical shed behavior, identical
starvation-bound adherence, and equally flat allocations. Queue operations are a negligible
fraction of seconds-long work items, so its global lock never convoys.

The MultiQueue binding (`ConcurrentPriorityWorkQueue`) remains the right choice where its
relaxation actually buys something: high contention — many workers hammering the queue with
micro work items, where the locking baseline's critical section convoys (DataFerry's published
contended results: 1.7–17.5× from 4T up). Its rank error also shrinks relative to population on
hosts with fewer cores (n = 4 × processor count) or with much deeper queues.

Keep both honest: the contended throughput sweep (`throughput` verb) guards the MultiQueue's
regime; this soak guards the locking binding's. Re-validate with the 600 s runs before quoting
in the README.

## Release-gate runs (600 s)

The release artifact is the full 10-minute-per-run sweep — 4 runs ≈ 40 minutes plus build:

```bash
# Full release-gate soak (both bindings × workers {2,8}, 600 s each):
dotnet run -c Release --project src/Bifrost.Benchmarks -- soak --seconds 600 --out soak-results

# Single-binding variants (e.g. to re-run one side after a change):
dotnet run -c Release --project src/Bifrost.Benchmarks -- soak --seconds 600 --binding locking --out soak-results-locking
dotnet run -c Release --project src/Bifrost.Benchmarks -- soak --seconds 600 --binding multiqueue --out soak-results-multiqueue

# Smoke (what produced the INDICATIVE numbers above — ~3 min total):
dotnet run -c Release --project src/Bifrost.Benchmarks -- soak --seconds 45 --out /tmp/soak-smoke
```

Artifacts land as `soak-classes.csv` (per-class rows), `soak-runs.csv` (run-level rows), and
`soak.md` (both tables), each with a self-describing environment header.

CI: `.github/workflows/soak.yml` runs the 600 s sweep on `workflow_dispatch` (with a `seconds`
input override) and a nightly cron, uploading `soak-results/` as a build artifact. Per DR-8 the
soak is a release-gate/nightly artifact, **not** a per-PR gate.
