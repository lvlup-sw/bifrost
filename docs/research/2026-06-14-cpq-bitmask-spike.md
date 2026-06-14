# CPQ Transition-Bitmask — Discovery Spike (Evidence)

**Date:** 2026-06-14
**Status:** Throwaway discovery spike (DR-6). Output is EVIDENCE, not shippable code. Production
code is re-implemented later under TDD.
**Scope:** Tasks 1–5 of `docs/plans/2026-06-14-cpq-transition-bitmask.md` (Approach A floor +
Approach B spike gate).

> **Provenance note:** this spike ran on the i9-13900K dev host (32T, no AVX-512) via a standalone
> file-based harness in `/tmp/cpq-spike/` (now discarded), not the production benchmark project — a
> deliberate time-box choice. The harness faithfully mirrors the MultiQueue dequeue/scan/push
> structure but models the cached-top read as a plain word-atomic `long` read (no seqlock, valid
> for `TPriority=long`). Numbers are therefore **directional** (dense regression a conservative
> upper bound; sparse win real). Final DR-7 parity numbers re-run on the Azure Xeon 8573C.

## TL;DR — the three verdicts

| Verdict | Result | Number |
|---|---|---|
| **V-DENSE** | **MIXED — PASS at scale, FAIL at low thread count** | 32T −1.2% (within noise); 16T −1.2%; **4T −5.0% (real regression)** |
| **V-SPARSE** | **PASS — large win, zero steady-state cost** | pop-10: 177.3 ns → 100.5 ns (**1.76×**, 100% scan-avoidance); pop-1000: 88.6 → 88.7 ns (noise) |
| **V-B** | **B-NO-GO** | double-read not linearizable; sound generation-stamp reintroduces a global contended counter that erases the win |

**Recommendation:** proceed to the production TDD groups for **Approach A** (the floor); **do NOT
build Approach B** (Tasks 23–24); add an Approach-A mitigation for the low-thread dense regression
before shipping (see V-DENSE, and the orchestrator correction below).

---

## Environment (with caveats — stated honestly, not faked)

| | |
|---|---|
| Host | i9-13900K, **32 logical cores** (8 P-cores w/ HT + 16 E-cores), single socket, 62 GiB |
| **AVX-512** | **ABSENT** (Raptor Lake fuses off AVX-512). The SIMD occupancy-probe variant **CANNOT be measured here — DEFERRED TO XEON.** Not estimated, not faked. |
| Dense thread ceiling | ≤32 threads, so `n = RoundUpPow2(4×32) = 128` sub-queues. **DIRECTIONAL** vs the Xeon baseline's 64T/n=256 — adequate to detect a regression, not to reproduce absolute Xeon throughput. |
| OS / Runtime | Linux 7.0.11, .NET 10.0.202, Release, X64 RyuJIT, Workstation GC, Concurrent |
| Harness | Standalone file-based C# spike, NOT the production benchmark project. Reimplements two-choice sampling → O(n) verification scan as the sole `false` authority, with a `BITMASK` compile switch + instrumentation counters (occupancy writes, routing hits, scan fall-throughs). |
| Trials | Dense: 5 trials/config, 3 s windows. Sparse: 4 trials/config, 10⁶ pairs. V-B: 3 trials/config, 2×10⁶ ops, 32T. |

### Fidelity caveats

- **Models faithfully:** arity-4 implicit min-heap, per-sub-queue `Lock` with `TryEnter`
  resample-on-contention, cached-top read, striped count, two-choice sampling with 4 rounds, the
  O(n) verification scan as sole `false` authority, and the exact Approach-A Phase-1.5 routing
  (`Interlocked.Or/And` transitions under the lock; `TrailingZeroCount` iteration + `TryPopFrom`
  before the scan).
- **Does NOT model** the production **seqlock** (cached-top read is a plain volatile `long` read).
  This understates the baseline per-op cost slightly → dense regression is a *conservative upper
  bound*; sparse win is unaffected.

---

## Task 1 — the prototype (built, working)

Per-queue `ulong[] _occupancy` of length `(n+63)>>6`; bit `i` set ⇔ sub-queue `i` non-empty. Wired
into the sub-queue with its `_index` + a ref to the array. Transition writes UNDER the existing
`SyncLock`:

- `TryLockedPush`, on `wasEmpty`: `Interlocked.Or(ref _occ[i>>6], 1UL<<(i&63))`.
- `TryLockedPop`, when the pop leaves `_size==0`: `Interlocked.And(ref _occ[i>>6], ~(1UL<<(i&63)))`.

Routing (Approach A, Phase 1.5): after the sampling loop misses and **before** the verification
scan, iterate set bits via `BitOperations.TrailingZeroCount` and `TryPopFrom(index)`. On no set
bits, fall through to the existing scan, **which stays the sole `false` authority**. Test-only
counters: occupancy-write count, routing hits, scan fall-throughs. Both variants build clean and
pass a conservation smoke test.

---

## V-DENSE (Task 2) — does the bitmask regress the dense path?

UniformMixed 50/50, fixed 3 s window, deep steady population (`n×512` pre-loaded so sub-queues
rarely hit empty). 5 trials each.

| Threads | n | base (M ops/s) | bitmask (M ops/s) | Δ | occWrites / ~op-count |
|---|---|---|---|---|---|
| 4 | 16 | 28.44 (sd 0.32) | 27.03 (sd 0.61) | **−4.97%** | ~79k / ~82M = 0.10% |
| 16 | 64 | 72.79 | 71.94 | −1.2% | ~510k / ~216M = 0.24% |
| 32 | 128 | 114.52 (sd 0.72) | 113.11 (sd 1.04) | **−1.24%** | ~810k / ~340M = 0.24% |

Occupancy-write count is low but NOT zero: the relaxed two-choice dequeue concentrates pops on the
smaller-cached-top stripe, so stripes still drain/refill even under deep aggregate population
(~0.1–0.25% of ops). The "≈ 0 under load" claim holds *relative to op count* but is not literally
zero.

**Verdict: MIXED.** 16T/32T within the ~1% noise band → PASS in the scaling regime the design
targets. **4T −5.0% is real and consistent** (sd ~1–2%, outside noise) → a genuine FAIL at low
thread count, reported not hidden.

---

## V-SPARSE (Task 3) — quantify the sparse win

Single-thread Enqueue+Dequeue pair latency, `n=128` fixed (pop-10 ≈ 92% empty). 10⁶ pairs/trial,
4 trials.

| Population | base (ns/pair) | bitmask (ns/pair) | speedup | routing hits | scan fall-throughs |
|---|---|---|---|---|---|
| **10** (sparse) | **177.3** | **100.5** | **1.76×** | 529k / 1M | **0** |
| **1000** (steady) | 88.6 | 88.7 | 1.00× (noise) | 0 | 0 |

pop-10 baseline 177.3 ns reproduces the O(n)-scan cliff (the Xeon doc's ~260 ns at n=256; this
host's n=128 scan is half the width). pop-10 prototype routes straight to an occupied stripe via
`TrailingZeroCount` — **`scanFallThroughs = 0`: the verification scan is never entered in the sparse
regime → 100% scan-avoidance for the post-sampling path.** pop-1000: routingHits = 0 AND
scanFallThroughs = 0 — at steady density sampling always lands within 4 rounds, so the bitmask is
**invisible** (0% delta).

**Verdict: PASS.** 1.76× (43% reduction), routing-bounded, 100% scan-avoidance, zero steady-state
cost.

---

## V-B (Task 4) — is authoritative O(1)-empty from the bitmask sound?

### Empirical: Approach-A correctness stress

32 threads (16 producers / 16 drainers), 2×10⁶ items, 3 trials each, Release.

| mode | enqueued | dequeued | conservation |
|---|---|---|---|
| base | 2,000,000 | 2,000,000 | **OK** (count + sum exact), all 3 |
| bitmask | 2,000,000 | 2,000,000 | **OK** (count + sum exact), all 3 |

Conservation holds exactly in every run — every enqueued item dequeued exactly once, both modes.
(A cheap "false-empty while resident" aggregate counter was tried; it is unsound — fired in both
modes with no consistent ordering — because a global resident-count delta around a `TryDequeue`
cannot witness that the *same* item stayed resident across the scan. Methodological takeaway: only
conservation + a linearization argument can settle this, not a residency aggregate.)

### Why Approach A is correct regardless of the bitmask (independent of B)

The routing path can only (a) short-circuit to a *successful* pop, or (b) fall through to the
verification scan. It **never** returns `false`. So a **stale set bit** → `TryPopFrom` Empty/Contended
→ continue/fall-through (one wasted try-lock); a **stale clear bit** → routing skips it but the scan
still inspects every sub-queue and finds it. Approach A inherits the existing `false` contract
verbatim and ships independent of B.

### Approach B linearization argument (the go/no-go)

B asks: can `TryDequeue` return `false` **directly from a stably-all-zero occupancy read**, skipping
the scan? The occupancy is `⌈n/64⌉` words (3 at n=128, 4 at the Xeon's n=256). A multi-word read is
**not atomic**.

1. **Double-read (snapshot stability).** Read all words; if all-zero, re-read with an acquire fence;
   if all-zero again and unchanged, treat as empty. **Flaw:** all-zero twice does not linearize. The
   bit and the heap size are *two locations*; a producer setting bit `b` and a drainer clearing it
   can leave the bit momentarily 0 while `_size > 0`, so the array reads all-zero at both instants
   while an element is continuously resident in that stripe. **Not linearizable.**

2. **Generation stamp.** Add a single global `_generation` bumped on *every* empty→non-empty
   transition; read `g0` → words → `g1`; all-zero AND `g0==g1` ⇒ true snapshot ⇒ authoritative
   `false`. **Sound** (seqlock pattern lifted to the array; arm64 store-store contained by the
   release-after-set + acquire-before-`g1`, the same argument `PublishTop`/`TryReadTop` use). **But
   the cost kills the win:** the generation is a single global counter written on every transition
   across all stripes — a process-wide contended cache line that converts per-stripe lock-striping
   back into a single hot atomic. V-DENSE already shows the *per-stripe* write costs ~5% at 4T; a
   *global* write on the same transitions is strictly worse and scales negatively — exactly the
   single-point contention the MultiQueue exists to avoid.

**Economics:** B's only payoff over A is skipping the scan *on a genuinely-empty queue* — rare under
load, and cheap when it happens (the common sparse-but-nonempty case is already handled by A's
routing — the V-SPARSE win). B trades a sound, cheap, scan-as-authority design for either unsoundness
or global contention to optimize the rarest case.

**Verdict: B-NO-GO.** Revisit only if a future profile shows the genuinely-empty scan is a measured
hot spot AND a *per-word/sharded* generation scheme (not a single global counter) can be shown
linearizable — neither condition is met today.

---

## SIMD occupancy probe — DEFERRED TO XEON

The AVX-512 all-zero / first-set vector probe (`VPTESTMQ`-style) cannot be measured on this host
(Raptor Lake fuses off AVX-512). Deferred to the Xeon 8573C (AVX-512F+CD+BW+DQ+VL+VBMI) where n=256
(4 occupancy words) makes a vector probe more relevant. Note: at 4 words the scalar
`TrailingZeroCount` loop is already 4 iterations — SIMD upside is marginal and should be gated on a
measured win.

---

## Orchestrator correction — the proposed dense mitigation needs care

The spike suggested replacing `Interlocked.Or/And` with a plain ordered store under the held lock.
**That is unsound as stated for a packed bitmask:** distinct stripes share a 64-bit word and are
guarded by *different* per-stripe locks, so two plain stores to the same word race and lose updates.
Dropping the interlocked requires **one occupancy slot per stripe** (a `byte[]`/`int[]`, not a packed
`ulong[]`), which removes word-sharing at the cost of compactness and the `TrailingZeroCount`
fast-iteration (routing becomes a byte scan; SIMD still possible). Also: the 0.1% transition rate at
4T does not obviously account for a 5% throughput drop, so the **cause is not firmly the atomic RMW** —
the production TDD task should *profile the actual cause* (extra boundary-check branch + field load on
every push/pop? occupancy-array cache traffic? harness noise amplified at low thread?) before picking
a mitigation. Treat the −5% as a real gating item with an unconfirmed cause, not a solved one.
