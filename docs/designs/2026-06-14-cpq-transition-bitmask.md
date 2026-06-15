# Design: CPQ Transition Bitmask (sparse-regime occupancy index)

**Feature ID:** `cpq-transition-bitmask`
**Related:** `src/Bifrost.Concurrency/` (MultiQueue CPQ), [`BACKGROUND.md`](../../src/Bifrost.Concurrency/BACKGROUND.md), [Xeon 8573C benchmark](../benchmarks/2026-06-13-cpq-xeon-8573c.md), design outline [`docs/research/convo.md`](../research/convo.md), [issue #17](https://github.com/lvlup-sw/bifrost/issues/17) (CPQ home)
**Date:** 2026-06-14

## Problem Statement

The MultiQueue's two-choice dequeue samples sub-queues **uniformly at random with no
emptiness awareness** (`ConcurrentPriorityQueue.Dequeue.cs:123`, `ThreadHandle.NextStickyPair`).
That is correct and fast while the queue is populated, but it degrades as the queue drains: each
of the `SampleRounds = 4` rounds (`Dequeue.cs:40`) increasingly draws empty sub-queues, and once
the budget is spent `TryDequeue` falls through to the authoritative **O(n)
`TryDequeueVerificationScan`** (`Dequeue.cs:195`).

The Xeon 8573C benchmark quantifies the cliff: with `n = 256` sub-queues a population-10 queue is
≈96% empty, so every `TryDequeue` pair costs ~260 ns versus ~80 ns steady-state — the
sparse-structure regime called out in [Figure 6](../benchmarks/2026-06-13-cpq-xeon-8573c.md). The
relaxation cost (rank error ≈212 at this `n`) is the price paid for the 114 M ops/s dense scaling;
the sparse penalty is pure waste on top of it.

Today's only emptiness signal is the **per-sub-queue** `EmptyFlag` inside each seqlock header
(`SubQueueHeader.cs:80`). It makes a *sampled* empty queue cheap to skip, but it neither biases
sampling nor short-circuits the scan, and there is no global occupancy structure. As the queue
drains, the sampler keeps drawing blind and the scan keeps running O(n).

We add a global **transition bitmask**: one bit per sub-queue (`1` = non-empty), written only when
a sub-queue crosses the empty↔non-empty boundary. On a sample-miss the consumer reads the bitmask
and routes directly to a populated sub-queue via `BitOperations.TrailingZeroCount`, collapsing the
sparse penalty to ~O(1) routing — without touching the hot dense path, where the bitmask is never
written and sits dormant in cache.

## Decisions (settled in ideation, 2026-06-14)

| Decision | Choice |
|---|---|
| **Sequencing** | **Discover spike first.** The dense-regression and linearizability claims are hypotheses; an `/exarchos:discover` spike prototypes + measures them before the production plan commits (see DR-6). |
| **Sampling integration** | **Fallback-only.** The uniform two-choice hot path stays bit-identical; the bitmask is consulted *only after* the 4 sample rounds miss. The published rank-error contract `≈(5/6)·n` is unchanged. |
| **Representation** | **Scalar `ulong[]` first.** `BitOperations.TrailingZeroCount`/`PopCount` over `ceil(n/64)` words. Portable, AOT-clean, host-independent. A `Vector256`/AVX-512 read is explicitly out of scope here and recorded as a spike-measured future enhancement (DR-6). |
| **Empty-verdict correctness** | **Approach A is the committed floor; Approach B is spike-gated.** A = bitmask-as-hint, the existing verification scan stays the empty authority (correct under *any* staleness). B = bitmask-as-authority O(1)-empty via a snapshot/generation protocol — adopted **only if** the spike proves it sound under the .NET memory model (DR-6). |
| **Transition-write placement** | **Under the sub-queue lock**, co-located with the seqlock publish. Already required for B; harmless for A. Keeps the bitmask B-ready without a second design pass. |
| **Scope boundary** | `ConcurrentPriorityQueue` only. `LockingPriorityQueue` (exact, single-lock) is untouched. No public API change in Approach A. |

## Approaches Considered

The architectural fork is the **correctness model for the empty verdict** (the sampling and
representation forks were settled in the Decisions table: fallback-only, scalar `ulong[]`).

### Option 1: Bitmask-as-hint, verification scan stays the authority

**Approach:** The bitmask only accelerates routing. On a sample-miss, route via
`TrailingZeroCount` to a populated sub-queue; if no bits are set, fall through to the existing
`TryDequeueVerificationScan`, which remains the sole authority for returning `false`.

**Pros:**
- Correct under *any* staleness — stale-set falls to the scan, stale-clear is caught by the scan.
- Preserves the "observed empty" contract verbatim; smallest correctness surface.

**Cons:**
- The genuinely-empty path stays O(n) (the scan still runs to prove empty).
- Wins only the sparse *non-empty* case — but that is the common drain case.

**Best when:** You want the perf win now without betting the empty contract on a memory-model proof.

### Option 2: Bitmask-as-authority via a snapshot/generation protocol

**Approach:** Make a stably-all-zero read authoritative (double-read or generation stamp), so
`TryDequeue` returns `false` in O(1) with no scan — the "honest emptiness in O(1)" of `convo.md` §3.

**Pros:**
- Both sparse paths (non-empty *and* empty) collapse to ~O(1). Fully realizes the proposal.

**Cons:**
- Requires a rigorous linearization-point argument across non-atomic multi-word reads + concurrent
  under-lock sets (the part `convo.md` hand-waves). Real false-empty risk under weak ordering (arm64).
- The generation stamp reintroduces a shared written word (false-sharing to reason about).

**Best when:** A spike proves the protocol sound under the .NET memory model.

### Option 3: Replace the verification scan entirely

**Approach:** The bitmask is the sole empty signal; delete `TryDequeueVerificationScan`.

**Pros:** Simplest final code; no O(n) path anywhere.

**Cons:** Strictly dominated by Option 2 on risk — same proof burden, no backstop if the proof has a
hole. Loses the conservative fallback.

**Best when:** Rarely; only if Option 2 is proven *and* the scan's upkeep is judged not worth it.

## Chosen Approach

**Option 1 as the committed floor, with Option 2 spike-gated** (selected in ideation 2026-06-14).
Option 1 is guaranteed shippable and contract-preserving, so it anchors the production plan. The
`/exarchos:discover` spike (DR-6) then attempts to prove Option 2's O(1)-empty protocol sound; the
design graduates to it **only if proven**, with Option 1's scan retained as the fallback unless the
spike explicitly clears its removal. Option 3 is off the table unless Option 2 is airtight. This
keeps a landable scope regardless of how the spike resolves, and it is why the transition writes are
specified under the sub-queue lock from the start (DR-2) — Option 1 does not require it, but it makes
the floor B-ready without a second design pass.

## Requirements

### DR-1: Per-instance occupancy bitmask

A `ConcurrentPriorityQueue<TElement, TPriority>` gains a private occupancy bitmask sized to its
sub-queue count: `ulong[] _occupancy` of length `(n + 63) >> 6`. Sub-queue `i` maps to word
`i >> 6`, bit `i & 63`; a set bit means "this sub-queue published non-empty". The array is
allocated once in the core constructor (`ConcurrentPriorityQueue.cs:228`) and shared by reference
with each `SubQueue`, which is told its own index.

**Acceptance criteria:**
- Given a fresh queue with `n` sub-queues
  When it is constructed
  Then `_occupancy` has `ceil(n/64)` words, all zero (every sub-queue starts empty, matching
  `SubQueue`'s initial `EmptyFlag = 1` at `SubQueue.cs:108`).
- Given `n = 1` (the collapsed/exact case) or any non-power-of-64 `n` (e.g. 32)
  When the bitmask is sized
  Then unused high bits in the final word stay `0` and are never interpreted as occupied.
- The bitmask adds **zero per-operation allocation**; it is one array field, indexed by shift/mask
  (no division, mirroring the `_subQueueMask` discipline at `ConcurrentPriorityQueue.cs:85`).

### DR-2: Boundary-only transition writes, under the sub-queue lock

The bitmask is written *only* when a sub-queue crosses the emptiness boundary, and *only* while
that sub-queue's `SyncLock` is held:

- **0 → non-empty** (in `SubQueue.TryLockedPush`, `SubQueue.cs:560`, when `wasEmpty` is true):
  `Interlocked.Or(ref _occupancy[i >> 6], 1UL << (i & 63))`.
- **non-empty → 0** (in `SubQueue.PopHeldRoot`/`TryLockedPop` when `_size` reaches `0`, and in
  `LockedClear`, `SubQueue.cs:675`): `Interlocked.And(ref _occupancy[i >> 6], ~(1UL << (i & 63)))`.

A push onto an already-populated sub-queue, or a pop that leaves entries behind, **does not touch
the bitmask** — this is what keeps it dormant under load.

**Acceptance criteria:**
- Given a sub-queue with ≥1 entry
  When another item is pushed or a non-last item popped
  Then no `Interlocked` write to `_occupancy` occurs (verified by an instrumentation counter in a
  test build).
- Given the dense UniformMixed 64-thread workload
  When measured over a 3 s window
  Then `_occupancy` write count is ~0 (sub-queues never reach size 0), and dense throughput is
  within benchmark noise of baseline (DR-5).
- Transition writes use `Interlocked.Or`/`Interlocked.And` (atomic per-word; no read-modify-write
  race between two sub-queues sharing a word) and occur strictly inside the `SyncLock` critical
  section.

### DR-3: Sparse-fallback routing (Approach A)

After the two-choice sampling loop exhausts its `SampleRounds` budget without a pop
(`Dequeue.cs:160`), insert a **bitmask-guided routing phase** before the verification scan: read
the occupancy words, and for each set bit (lowest-first via `TrailingZeroCount`) attempt
`TryPopFrom(index)`. A `Success` returns immediately. An `Empty`/`Contended` outcome (a stale-set
bit, or a race) skips that bit and continues. When no set bits remain, fall through to the existing
`TryDequeueVerificationScan`, which remains the sole authority for returning `false`.

**Acceptance criteria:**
- Given a queue with one item in sub-queue `k` and all others empty
  When `TryDequeue` is called and sampling misses
  Then the routing phase reads the bitmask, `TrailingZeroCount`s to `k`, pops it, and returns
  `true` **without** an O(n) scan over all sub-queues.
- Given a queue that is genuinely empty
  When `TryDequeue` is called
  Then the routing phase finds no set bits and the verification scan runs and returns `false`
  (the "observed empty at some point during the call" contract is preserved verbatim).
- The dense path (sampling succeeds within budget) never enters the routing phase — confirmed by a
  test asserting the routing branch is not taken when a sampled pop succeeds.

### DR-4: Staleness safety and the emptiness contract (failure modes)

The bitmask is a lock-free-read hint and *will* be observed stale. Correctness rests on the
staleness being **asymmetric-safe** under Approach A:

- A **stale-set** bit (reads `1`, sub-queue actually empty) is safe: routing attempts the pop, gets
  `Empty`, and moves on — at worst wasted work, never a wrong answer.
- A **stale/lost-clear** bit must never cause a false-empty. Under Approach A this is structurally
  impossible because the verification scan — not the bitmask — authorizes every `false`. The scan
  locks each sub-queue and observes the heap directly, so a missed clear can only cost a redundant
  routing attempt, never a lost element.
- Multi-word reads are **not** an atomic snapshot. Approach A never treats an all-zero read as
  proof of emptiness, so non-atomicity is irrelevant to correctness (it only affects whether
  routing finds a populated queue on the first pass).

**Acceptance criteria:**
- Given a high-churn near-empty workload (many producers each inserting single items while
  consumers drain), run in Release for ≥10⁶ operations
  When any `TryDequeue` returns `false`
  Then no element was provably resident at the call's linearization window (no lost element; no
  conservation violation) — the existing `EmptySemantics`/`ConservationStress` families extended
  with a bitmask-churn scenario.
- Given a sub-queue lock held by a writer mid-publish
  When a consumer reads `_occupancy` lock-free
  Then a torn/stale read can only produce extra routing attempts that resolve via `TryPopFrom`'s
  three-way status, never an incorrect `true`/`false`.
- `n = 1` collapse: routing and emptiness behave identically to today (the single sub-queue's bit
  mirrors its `EmptyFlag`; the relaxed dequeue returns the exact minimum, per `Dequeue.cs:78`).

### DR-5: Zero dense-regime regression, zero allocation

The optimization must be invisible on the hot path it does not target.

**Acceptance criteria:**
- Dense throughput (UniformMixed5050 and NarrowKeyRange, 1→64 threads) stays within ±2% of the
  pre-change baseline in [the Xeon harness](../benchmarks/2026-06-13-cpq-xeon-8573c.md) (BDN-pair
  tolerance precedent).
- Single-thread Enqueue+Dequeue stays `0 B/op` at all populations; the bitmask adds no allocation.
- Sparse latency at population 10 (`n = 256`) drops materially from the ~260 ns baseline pair
  toward the routing-bounded floor; the exact target is set by the spike (DR-6) but the gate is
  "strictly faster than the O(n) scan, no regression elsewhere".
- No new false-sharing regression: the `_occupancy` words' cache-line behavior under a
  churn-near-empty workload is measured; if padding is needed it is added (spike-decided), but the
  dense path — where the words are never written — must show no coherence-traffic regression.

### DR-6: Discover spike — validate before committing (B-graduation gate)

Before the production plan is finalized, run an `/exarchos:discover` spike on a throwaway branch
that produces evidence, not shippable code. The spike's findings gate which requirements graduate.

**Spike deliverables:**
1. Scalar prototype wiring DR-1/DR-2/DR-3 (Approach A) minimally.
2. **Dense-regression measurement** — UniformMixed 64T before/after; confirms the "never written
   when dense" claim holds in practice (DR-5 gate).
3. **Sparse-win measurement** — population-10 pair latency before/after; quantifies the DR-5 target.
4. **Linearizability analysis for Approach B** — can a stably-all-zero multi-word read (double-read
   or generation-stamped) be a sound linearization point for O(1)-empty under the .NET memory model
   (arm64 included)? A stress harness that tries to force a false-empty, plus a written argument.
   Output: **B-go or B-no-go**.
5. **SIMD probe** — does a `Vector256` occupancy read beat scalar `BitOperations` on the sparse
   path by enough to justify a second code path? Output: a number and a recommendation, no code.

**Acceptance criteria:**
- The spike writes a findings doc under `docs/research/` with the four measurements + the B verdict.
- **B-go:** the production plan adds a DR for the snapshot/generation protocol (O(1)-empty) layered
  on the A floor; A's scan stays as the fallback unless the spike explicitly clears its removal.
- **B-no-go:** the plan ships Approach A only; DR-6's B and SIMD items are recorded as deferred
  follow-ups, not built.
- The spike branch is deleted after findings are captured (no stale worktree — repo convention).

### DR-7: Before/after benchmark suite — prove the problem, then prove the fix

This optimization is justified entirely by numbers, so the benchmark evidence is a first-class
deliverable, not an afterthought. We capture a **baseline (before)** that reproduces the problem on
the current `main` code, then an **after** on the change, with paired charts so the delta is
self-evident. Both runs use the existing Xeon harness and methodology
([2026-06-13-cpq-xeon-8573c.md](../benchmarks/2026-06-13-cpq-xeon-8573c.md)) so they are directly
comparable, and both raw datasets are committed under `docs/benchmarks/data/`.

**Part 1 — Reproduce the problem (before):** quantify the sparse cliff *and* the dense scaling we
must not break, on unmodified `main`.

- **Sparse latency sweep** (the cliff): single-thread Enqueue+Dequeue pair latency across
  populations `{10, 100, 1k, 100k, 1M}` at `n = 256`. Population 10 (≈96% empty) must reproduce the
  documented ~260 ns pair vs. ~80 ns steady-state — the before-state that motivates the work.
- **Sparse contended** (the cliff under load): a drain-dominated workload (consumers > producers,
  queue hovering near empty) at 2/8/32/64 threads, recording throughput and the O(n)-scan
  fall-through rate.
- **Dense baseline** (the regression guard): UniformMixed5050 and NarrowKeyRange, 1→64 threads,
  reproducing the ~110–114 M ops/s curve and `0 B/op`.

**Part 2 — Verify the solution (after):** the same matrix on the change.

**Acceptance criteria:**
- Given the population sweep on `main` (before)
  When run in the Xeon harness
  Then population 10 reproduces the ~O(n) sparse pair latency (the problem is demonstrated, not
  assumed) and the result is committed as the baseline dataset.
- Given the identical sweep on the change (after)
  When compared to baseline
  Then population-10 pair latency is strictly lower and trends toward the routing-bounded floor
  (`O(n/64)` read + one locked pop), while populations 1k–1M are within ±2% of baseline.
- Given the dense matrix before vs. after
  Then UniformMixed5050 / NarrowKeyRange throughput at every thread count is within ±2% and
  `0 B/op` is preserved — the dense path is provably untouched.
- Given the drain-dominated contended workload before vs. after
  Then after-throughput is ≥ before at every thread count and the O(n)-scan fall-through rate drops
  toward zero (routing replaces scanning).
- A results doc `docs/benchmarks/YYYY-MM-DD-cpq-bitmask-before-after.md` is committed with
  **paired before/after charts** (regenerable via the harness's `generate_charts.py` pattern), an
  environment table, and a one-line verdict per workload. Raw CSVs live under
  `docs/benchmarks/data/<run>/`.
- The spike (DR-6) produces the *preliminary* numbers that size the targets; DR-7 is the
  *final, committed* before/after artifact on the production change. Spike numbers do not substitute
  for it.

## Technical Design

### Data structure and indexing

```
_occupancy : ulong[ceil(n/64)]            // bit set ⇔ sub-queue non-empty
word(i)    = i >> 6                        // 64 bits per word
bit(i)     = 1UL << (i & 63)
set(i)     : Interlocked.Or (ref _occupancy[word(i)],  bit(i))   // under SyncLock
clear(i)   : Interlocked.And(ref _occupancy[word(i)], ~bit(i))   // under SyncLock
```

At `n = 256` that is four `ulong`s (32 bytes); at `n = 32`, one word with 32 live bits; at
`n = 512` (128-core), eight words. `SubQueue` gains two readonly fields set at construction: its
`int _index` and a reference to the shared `_occupancy` array.

### Dequeue control flow (Approach A)

```
TryDequeue:
  Phase 1  two-choice sampling × SampleRounds            ── unchanged, dense fast path
           └─ success → return true
  Phase 1.5 (NEW) bitmask-guided routing                 ── sparse fast path
           read _occupancy words
           for each set bit b (TrailingZeroCount, lowest-first):
              TryPopFrom(b): Success → return true
                             Empty/Contended → clear-local, continue
           no set bits → fall through
  Phase 2  TryDequeueVerificationScan                    ── unchanged, sole `false` authority
           └─ pop, or observed-empty → return false
```

Phase 1.5 reads `O(n/64)` words and pops the first populated sub-queue it can lock. The verification
scan is reached only when routing found nothing — i.e. the genuinely-empty case (or a transient
all-stale-clear window, which the scan then resolves correctly).

### Where transitions hook in (under the lock)

| Site | File:line | Condition | Action |
|---|---|---|---|
| Push | `SubQueue.cs:560` `TryLockedPush` | `wasEmpty` true after successful push | `set(i)` |
| Pop  | `SubQueue.cs:639` `PopHeldRoot` (covers `TryLockedPop`) | `_size == 0` after pop | `clear(i)` |
| Clear | `SubQueue.cs:675` `LockedClear` | entries removed | `clear(i)` |

All three already run inside `SyncLock` and already publish the seqlock top in the same critical
section — the bitmask write joins them.

## Integration Points

- **`ConcurrentPriorityQueue.cs:251`** — ctor allocates `_occupancy` and passes `(i, _occupancy)`
  to each `new SubQueue(...)`.
- **`SubQueue.cs`** — new `_index`/`_occupancy` fields; `set`/`clear` calls in the three transition
  sites. The seqlock publish logic is unchanged; the bitmask is a sibling write.
- **`ConcurrentPriorityQueue.Dequeue.cs:160`** — insert Phase 1.5 between the sampling loop and the
  verification scan.
- **`ConcurrentPriorityQueue.Count.cs` (`IsEmpty`)** — *optional, spike-gated*: an all-zero
  `_occupancy` read could short-circuit `IsEmpty`; but as a `false`-authority this is exactly the
  Approach-B question, so it is deferred to the B verdict, not built in the A floor.
- **No change** to `ThreadHandle`, the seqlock, the rank-error contract, or any public API.

## Testing Strategy

- **Unit:** indexing math (`word`/`bit`, unused-high-bit masking at `n = 32`); transition writes
  fire exactly on boundary crossings and nowhere else (instrumented counter); routing picks the
  lowest set bit and skips stale-set bits.
- **Property/stress (extends existing families):** `ConservationStress` + `EmptySemantics` gain a
  churn-near-empty scenario (single-item insert/drain storm) asserting no false-empty and no lost
  element over ≥10⁶ ops in **Release** (per the project lesson that Debug-green ≠ Release-green for
  async/concurrency timing).
- **Benchmark gates (before/after, DR-7):** a committed baseline on `main` that *reproduces the
  problem* (population-10 sparse cliff + dense scaling curve), then the same matrix on the change
  proving the sparse win and dense non-regression (±2%, DR-5). Paired charts + raw CSVs land in
  `docs/benchmarks/`; `0 B/op` preserved via `[MemoryDiagnoser]`.
- **AOT:** the scalar `BitOperations`/`Interlocked` path is trim/AOT-clean; covered by the existing
  AOT smoke publish.

## Spike outcome (2026-06-14, resolves DR-6)

The discover spike ran ([`docs/research/2026-06-14-cpq-bitmask-spike.md`](../research/2026-06-14-cpq-bitmask-spike.md)):

- **V-SPARSE PASS** — pop-10 177→100 ns (**1.76×**), 100% scan-avoidance; invisible at steady
  density. The premise holds → **build Approach A** (DR-1–DR-5).
- **V-B NO-GO** — authoritative O(1)-empty is not worth it (double-read non-linearizable;
  generation-stamp reintroduces a global contended counter). **Tasks 23–24 are deferred**, not built.
- **V-DENSE MIXED** — 16T/32T within noise, but **4T −5.0%** in the spike harness (uncertain cause;
  possibly hybrid-CPU/standalone-harness artifact). This was carried to DR-7 for confirmation on
  production code → **refuted** (see *DR-7 production benchmark outcome* below).

## DR-7 production benchmark outcome (2026-06-14, resolves DR-5/DR-7)

Before/after on production code, `main` (`74a95bb`) vs the feature branch — full report in
[`docs/benchmarks/2026-06-14-cpq-bitmask-before-after.md`](../benchmarks/2026-06-14-cpq-bitmask-before-after.md)
(i9-13900K, 32T/n=128, no AVX-512 → **directional vs the Xeon 8573C**, the parity host):

- **V-SPARSE — PASS (direction), magnitude smaller than the spike.** Pop-10 pair latency
  **92.5 → 79.6 ns (1.16×, −14%), 0 B/op.** The spike's 177→100 ns / 1.76× was a *standalone-harness*
  artifact: the real verification scan takes the cheap lock-free `EmptyFlag` route, so the "before"
  is already ~92 ns at n=128 — not the ~177/260 ns cliff. The win is real but modest at this `n` and
  scales with `n`, so the larger payoff is expected on the Xeon (n=256). DR-7's "reproduce the
  ~260 ns cliff" criterion was a harness/Xeon-n=256 figure, not a local-n=128 one.
- **V-DENSE-NOREG — PASS.** Dense throughput within ±2% at 16T/32T (pooled over 8 trials);
  steady-state latency at pops 1k/100k/1M within noise; **`0 B/op` at every population**; drain
  after ≥ before.
- **V-4T — REFUTED on production code.** 4T dense = UniformMixed −1.61% / NarrowKeyRange +0.67%,
  inside ±2% and inside this hybrid CPU's ~±4% 4-thread placement noise. The spike's 4T −5% did
  **not** reproduce — consistent with the only new dense-path work being a field load + an
  already-present branch (the transition write is strictly off the dense path). **No mitigation
  needed.**

## Open Questions (resolved / deferred)

- **Approach B** — **NO-GO** (spike). Revisit only if a future profile shows the genuinely-empty
  scan is a measured hot spot *and* a per-word/sharded generation scheme (not a single global
  counter) is shown linearizable.
- **4T dense regression** — **RESOLVED (refuted on production code, DR-7).** No mitigation built.
  Were it to surface on other hardware, the sound fix is one occupancy slot **per stripe**
  (`byte[]`/`int[]`, plain ordered store under the held lock) — *not* a plain store on the packed
  `ulong[]`, since distinct stripes share a word under different locks and would race.
- **Sparse-win magnitude on server hardware** — open, deferred to the **Xeon 8573C** (n=256, 64T):
  the local 1.16× should widen as the verification scan grows with `n`.
- **`_occupancy` padding** — decided by the DR-5 false-sharing measurement on production code.
- **SIMD** — `Vector256`/AVX-512 emptiness read **deferred to the Xeon** (no AVX-512 on the dev
  host); marginal at n=256 (4 words). Measured future enhancement, not in scope.
