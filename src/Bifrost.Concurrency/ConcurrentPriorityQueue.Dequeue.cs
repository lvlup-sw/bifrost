// =============================================================================
// <copyright file="ConcurrentPriorityQueue.Dequeue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bifrost.Concurrency;

/// <content>
/// The relaxed dequeue surface: the two-choice rule. A dequeue samples two random sub-queues,
/// reads their cached tops <i>without locking</i>, and pops the one whose published minimum is
/// smaller. This trades a bounded rank error (the popped element is one of the smallest priorities,
/// not necessarily the global minimum) for near-linear read scaling; strict-minimum semantics are
/// available separately through <c>TryDequeueMin</c>.
/// </content>
/// <remarks>
/// <para>
/// The dequeue is two-phase: bounded two-choice sampling (the fast path), then the authoritative
/// <see cref="TryDequeueVerificationScan"/>. The scan is what makes <see langword="false"/> safe to
/// return under concurrency: it concludes emptiness only after one full pass observes <i>every</i>
/// sub-queue empty, restarting (with <see cref="SpinWait"/> backoff) whenever a pass hits a
/// contended sub-queue: a held lock means another thread is mid-mutation there, so the pass's
/// evidence is void. The returned <see langword="false"/> therefore carries the
/// <c>ConcurrentQueue.TryDequeue</c>-precedent contract: "the queue was observed empty at some
/// point during the call".
/// </para>
/// </remarks>
public sealed partial class ConcurrentPriorityQueue<TElement, TPriority>
{
    /// <summary>
    /// The number of two-choice sampling rounds attempted before falling back to the full scan. A
    /// small bound keeps the common-case fast path short: on a populated queue a handful of fresh
    /// samples almost always lands a successful pop, and an exhausted budget hands off to the scan
    /// (which alone establishes the authoritative empty result).
    /// </summary>
    private const int SampleRounds = 4;

    /// <summary>
    /// TEST-ONLY: the number of sub-queues the final authoritative verification pass observed
    /// empty before the most recent <see langword="false"/> return from <see cref="TryDequeue"/>.
    /// The observed-empty contract requires this to equal the sub-queue count exactly. Written
    /// only on the false path; meaningful only in single-threaded tests.
    /// </summary>
    private int _debugLastFalseScanEmptyObservations;

    /// <inheritdoc cref="_debugLastFalseScanEmptyObservations"/>
    internal int DebugLastFalseScanEmptyObservationsForTest => _debugLastFalseScanEmptyObservations;

    /// <summary>
    /// TEST-ONLY instrumentation: the number of times the sparse-fallback routing phase (Phase 1.5,
    /// DR-3) popped an element — i.e. read the occupancy bitmask, routed to a populated sub-queue via
    /// <see cref="BitOperations.TrailingZeroCount(ulong)"/>, and returned without an O(n) scan.
    /// Incremented with <see cref="Interlocked.Increment(ref long)"/> since multiple consumers race.
    /// </summary>
    private long _debugRoutingHitCount;

    /// <summary>
    /// TEST-ONLY instrumentation: the number of times the authoritative verification scan (Phase 2)
    /// was entered after routing found nothing to pop (DR-3). On the dense path neither this nor
    /// <see cref="_debugRoutingHitCount"/> moves, since sampling lands a pop within budget.
    /// </summary>
    private long _debugScanEntryCount;

    /// <summary>TEST-ONLY: see <see cref="_debugRoutingHitCount"/>.</summary>
    internal long DebugRoutingHitCountForTest => Volatile.Read(ref _debugRoutingHitCount);

    /// <summary>TEST-ONLY: see <see cref="_debugScanEntryCount"/>.</summary>
    internal long DebugScanEntryCountForTest => Volatile.Read(ref _debugScanEntryCount);

    /// <summary>
    /// TEST-ONLY: drives the post-sampling dequeue path in isolation — Phase 1.5 (bitmask routing)
    /// then, only if routing found nothing, Phase 2 (the verification scan) — <i>skipping</i> the
    /// random Phase 1 two-choice sampling. Lets the routing tests assert the routing/scan split
    /// deterministically, which the random sampler would otherwise only reach probabilistically. The
    /// production <see cref="TryDequeue"/> reaches the exact same Phase 1.5/Phase 2 sequence after its
    /// sampling budget is spent.
    /// </summary>
    /// <param name="element">The popped element on success; otherwise the default value.</param>
    /// <param name="priority">The popped priority on success; otherwise the default value.</param>
    /// <returns><see langword="true"/> if routing or the scan popped an element; otherwise <see langword="false"/>.</returns>
    internal bool TryDequeueRoutingOnlyForTest([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
    {
        if (TryRouteViaOccupancy(out element, out priority))
        {
            return true;
        }

        return TryDequeueVerificationScan(out element, out priority);
    }

    /// <summary>
    /// Attempts to remove and return an element with one of the smallest priorities in the queue.
    /// </summary>
    /// <param name="element">
    /// When this method returns <see langword="true"/>, the removed element; otherwise the default
    /// value of <typeparamref name="TElement"/>.
    /// </param>
    /// <param name="priority">
    /// When this method returns <see langword="true"/>, the priority of the removed element;
    /// otherwise the default value of <typeparamref name="TPriority"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when an element was removed; <see langword="false"/> when the queue
    /// was observed empty at some point during the call: a full verification pass saw every
    /// sub-queue empty (the <c>ConcurrentQueue.TryDequeue</c> precedent: concurrent enqueues that
    /// complete after that observation window may be present by the time the caller reacts).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Relaxed contract: this removes an element with <i>one of</i> the smallest
    /// priorities, not necessarily the global minimum. The two-choice rule samples two sub-queues
    /// and pops the better-looking one. The expected rank of the removed element (0 = the true
    /// minimum) is approximately <c>(5/6)·n</c>, where <c>n</c> is the sub-queue count
    /// (≈ 4 × processor count by default). Strict-minimum semantics are available through
    /// <c>TryDequeueMin</c>, or by wrapping <see cref="PriorityQueue{TElement, TPriority}"/> in a
    /// lock. When the queue collapses to a single sub-queue the relaxation is invisible and this
    /// returns the exact minimum.
    /// </para>
    /// <para>
    /// Relaxation scales with the core count and with the stickiness factor <c>s</c>; read this
    /// before deploying across hardware tiers. Because the sub-queue count is
    /// <c>n ≈ 4 × ProcessorCount</c> and the expected rank error is <c>(5/6)·n</c>, the looseness
    /// of this dequeue depends on the <i>host's</i> processor count, not a fixed constant. The same
    /// binary that pops a top-≈27 element on an 8-core box (<c>n = 32</c>) pops a top-≈213 element
    /// on a 64-core server (<c>n = 256</c>). That is by design: more sub-queues are what buy the
    /// reduced contention and throughput scaling, so the relaxation a caller tolerates and the
    /// parallelism they gain rise together. A stickiness factor <c>s &gt; 1</c> (the optional
    /// throughput dial; see the stickiness constructor) scales the expected rank error again, roughly
    /// linearly, to <c>(5/6)·n·s</c>, compounding across both the core count and <c>s</c>; the default
    /// <c>s = 1</c> leaves the bound at <c>(5/6)·n</c>. Two consequences follow: correctness must
    /// never depend on how close to the true minimum a pop lands (any such dependency surfaces only
    /// on larger machines or under a larger <c>s</c>); and for a fixed relaxation bound regardless of
    /// hardware, pin the sub-queue count through the internal constructor, keep <c>s = 1</c>, use
    /// <c>TryDequeueMin</c>, or wrap <see cref="PriorityQueue{TElement, TPriority}"/> in a lock.
    /// </para>
    /// <para>
    /// <b>Thread Safety:</b> This method is thread-safe and may be called concurrently from multiple
    /// threads. The sampling phase reads cached tops lock-free and pops under a try-lock that never
    /// blocks on a contended sub-queue; contention resamples rather than waits (the same wait-free
    /// locking the enqueue path uses).
    /// </para>
    /// </remarks>
    public bool TryDequeue([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
    {
        ThreadHandle handle = ThreadHandle.Current;
        int mask = _subQueueMask;

        // Phase 1 (two-choice sampling): a few bounded rounds of "sample two, pop the better top".
        for (int round = 0; round < SampleRounds; round++)
        {
            int i, j;
            if (mask == 0)
            {
                // n == 1 (mask 0): NextTwoDistinct cannot return distinct indices, so sample the
                // single sub-queue as both choices.
                i = 0;
                j = 0;
            }
            else
            {
                handle.NextStickyPair(mask, _stickiness, out i, out j);
            }

            // Read both cached tops without locking. An empty or unknown ("false") read is treated
            // as +infinity, so the other (finite) queue is preferred; if both are non-finite the
            // round simply pops from i and observes Empty/Contended, advancing to the next round.
            bool iFinite = _queues[i].TryReadTop(out TPriority topI, out bool emptyI) && !emptyI;
            bool jFinite = _queues[j].TryReadTop(out TPriority topJ, out bool emptyJ) && !emptyJ;

            // Choose the index with the smaller finite top; +infinity loses every comparison.
            int chosen;
            if (iFinite && jFinite)
            {
                chosen = CompareEffective(topI, topJ) <= 0 ? i : j;
            }
            else if (jFinite)
            {
                chosen = j;
            }
            else
            {
                chosen = i;
            }

            SubQueuePopStatus status = TryPopFrom(chosen, out element, out priority);
            if (status == SubQueuePopStatus.Success)
            {
                return true;
            }

            // Empty or Contended: end the sticky period so the next round re-rolls a FRESH pair
            // instead of re-sampling the same drained/contended selection (and so a contended pair
            // never blocks; stickiness stays wait-free). Neither outcome concludes the queue is
            // globally empty; only the authoritative scan does that.
            handle.ResetStickyDequeue();
        }

        // Phase 1.5 (sparse-fallback routing, DR-3): sampling missed its budget. Before paying for
        // the O(n) scan, consult the occupancy bitmask and route straight to a populated sub-queue.
        // This collapses the sparse-but-non-empty drain case (the common one as the queue empties)
        // to an O(n/64)-word read plus one locked pop. Routing is a HINT only — it never returns
        // false; on no set bits (or all-stale-clear) it falls through to the scan below.
        if (TryRouteViaOccupancy(out element, out priority))
        {
            return true;
        }

        // Phase 2 (the authoritative verification scan): sampling and routing did not land a pop, so
        // settle the outcome (a real pop, or an authoritative observed-empty false). The scan stays
        // the SOLE authority for returning false.
        return TryDequeueVerificationScan(out element, out priority);
    }

    /// <summary>
    /// The sparse-fallback routing phase (Phase 1.5, DR-3): reads the occupancy bitmask and, for each
    /// set bit lowest-first (via <see cref="BitOperations.TrailingZeroCount(ulong)"/>), attempts a
    /// locked pop of that sub-queue. A <see cref="SubQueuePopStatus.Success"/> returns immediately; an
    /// <see cref="SubQueuePopStatus.Empty"/> (a stale-set bit) or <see cref="SubQueuePopStatus.Contended"/>
    /// (a race) skips that bit and continues. When no set bit yields a pop, returns
    /// <see langword="false"/> so the caller falls through to the verification scan.
    /// </summary>
    /// <param name="element">The popped element on success; otherwise the default value.</param>
    /// <param name="priority">The popped priority on success; otherwise the default value.</param>
    /// <returns>
    /// <see langword="true"/> when a populated sub-queue was found and popped; <see langword="false"/>
    /// when routing found nothing to pop (no set bits, or every set bit was stale/contended).
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Staleness safety (DR-4).</b> The occupancy words are read lock-free and <i>will</i> be
    /// observed stale. This is asymmetric-safe under Approach A: a <b>stale-set</b> bit (reads 1,
    /// sub-queue actually empty) yields <see cref="SubQueuePopStatus.Empty"/> from the locked pop and
    /// is skipped — wasted work, never a wrong answer; a <b>stale/lost-clear</b> bit can only cost a
    /// redundant routing attempt because this method never returns <see langword="false"/> as a proof
    /// of emptiness — the verification scan, which locks and inspects every sub-queue directly, is the
    /// sole <see langword="false"/> authority. A multi-word read is not an atomic snapshot, but since
    /// an all-zero read is never treated as proof of emptiness here, non-atomicity only affects whether
    /// routing finds a populated queue on the first pass, never correctness.
    /// </para>
    /// <para>
    /// The words are re-read on each outer iteration so a bit set by a concurrent producer after the
    /// initial read can still be routed to within the same call, tightening the sparse win under churn.
    /// Progress is bounded: the loop advances only on a freshly observed set bit, and a pop that
    /// succeeds returns, so it cannot spin without either making progress or running out of set bits.
    /// </para>
    /// </remarks>
    private bool TryRouteViaOccupancy([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
    {
        ulong[] occupancy = _occupancy;

        for (int word = 0; word < occupancy.Length; word++)
        {
            // Snapshot this word; route to each currently-set bit lowest-first. A bit cleared by a
            // concurrent drainer after the snapshot resolves to Empty under the lock and is skipped.
            ulong bits = Volatile.Read(ref occupancy[word]);

            while (bits != 0)
            {
                int bit = BitOperations.TrailingZeroCount(bits);
                int index = (word << 6) + bit;

                SubQueuePopStatus status = TryPopFrom(index, out element, out priority);
                if (status == SubQueuePopStatus.Success)
                {
                    Interlocked.Increment(ref _debugRoutingHitCount);
                    return true;
                }

                // Empty (stale-set bit) or Contended (a race): drop this bit and try the next set
                // bit in the word. Neither outcome lets routing conclude anything about emptiness.
                bits &= bits - 1;
            }
        }

        // No set bit yielded a pop. Routing NEVER returns false as proof of emptiness — the caller
        // falls through to the verification scan, which is the sole false authority.
        element = default;
        priority = default;
        return false;
    }

    /// <summary>
    /// The authoritative empty verification scan. Loops full passes over every sub-queue
    /// until it either pops an entry or completes one pass in which <i>every</i> sub-queue was
    /// observed empty, the only state in which returning <see langword="false"/> is legal.
    /// </summary>
    /// <param name="element">The popped element on success; otherwise the default value.</param>
    /// <param name="priority">The popped priority on success; otherwise the default value.</param>
    /// <returns>
    /// <see langword="true"/> when a sub-queue yielded an entry; <see langword="false"/> only after
    /// one full pass observed every sub-queue empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Per sub-queue, the pass first takes the cheap lock-free route: a stable seqlock read that
    /// reports empty counts as that sub-queue's emptiness observation without touching its lock.
    /// Anything else (a published top, or an unreadable top from a writer mid-publication) must be
    /// settled under the lock via <see cref="TryPopFrom"/>: <see cref="SubQueuePopStatus.Success"/>
    /// returns the entry; <see cref="SubQueuePopStatus.Empty"/> is a lock-authoritative emptiness
    /// observation; <see cref="SubQueuePopStatus.Contended"/> voids the pass, since a held lock means
    /// another thread is mid-mutation there, so the pass restarts after a <see cref="SpinWait"/>
    /// step (whose escalation to yields keeps a long contention storm from burning a core).
    /// </para>
    /// <para>
    /// There is no fixed pass cap, by design: every restart requires an observed contention,
    /// i.e. another thread making progress on the same structure, which is the same system-wide
    /// progress argument the design's "wait-free locking" rests on. A false result without a clean
    /// all-empty pass would violate the observed-empty contract, so no bound may convert
    /// "contended" into "empty".
    /// </para>
    /// </remarks>
    private bool TryDequeueVerificationScan([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
    {
        // TEST-ONLY instrumentation: count entry into the O(n) scan. On the sparse path routing
        // (Phase 1.5) handles the pop and this stays put; the scan is reached only on a genuinely
        // (or transiently all-stale-clear) empty queue (DR-3).
        Interlocked.Increment(ref _debugScanEntryCount);

        SubQueue<TElement, TPriority>[] queues = _queues;
        SpinWait spinner = default;

        while (true)
        {
            int emptyObserved = 0;
            bool passVoided = false;

            for (int index = 0; index < queues.Length; index++)
            {
                // Cheap route: a stable seqlock read that says "empty" is this sub-queue's
                // emptiness observation for the pass, with no lock traffic.
                if (queues[index].TryReadTop(out _, out bool empty) && empty)
                {
                    emptyObserved++;
                    continue;
                }

                // Published-non-empty or unknown: settle under the lock.
                SubQueuePopStatus status = TryPopFrom(index, out element, out priority);
                if (status == SubQueuePopStatus.Success)
                {
                    return true;
                }

                if (status == SubQueuePopStatus.Empty)
                {
                    // Emptied between the read and the pop (or the read was "unknown" over an
                    // empty queue): observed empty under the lock, which is authoritative.
                    emptyObserved++;
                    continue;
                }

                // Contended: a writer holds this sub-queue's lock right now. The pass's evidence
                // is void; restart it rather than ever counting a contended queue as empty.
                passVoided = true;
                break;
            }

            if (!passVoided)
            {
                Debug.Assert(emptyObserved == queues.Length, "A non-voided pass must have observed every sub-queue empty.");
                _debugLastFalseScanEmptyObservations = emptyObserved;
                element = default;
                priority = default;
                return false;
            }

            spinner.SpinOnce();
        }
    }

    /// <summary>
    /// Attempts a non-blocking pop from a single sub-queue by index, shared by the two-choice
    /// sampling and the full-scan fallback. A thin pass-through over
    /// <see cref="SubQueue{TElement, TPriority}.TryLockedPop"/> that centralizes index-based popping
    /// so the sampling and scan paths stay consistent.
    /// </summary>
    /// <param name="index">The sub-queue index to pop from.</param>
    /// <param name="element">The popped element on <see cref="SubQueuePopStatus.Success"/>; otherwise the default value.</param>
    /// <param name="priority">The popped priority on <see cref="SubQueuePopStatus.Success"/>; otherwise the default value.</param>
    /// <returns>The three-way pop outcome from the sub-queue.</returns>
    /// <remarks>
    /// On <see cref="SubQueuePopStatus.Success"/> this releases one bounded-capacity reservation
    /// (<see cref="OnElementRemovedFromBounded"/>), the single release site that covers <i>both</i>
    /// the two-choice sampling pop and the verification scan's pop, since both funnel through here.
    /// </remarks>
    private SubQueuePopStatus TryPopFrom(int index, out TElement element, out TPriority priority)
    {
        SubQueuePopStatus status = _queues[index].TryLockedPop(out element, out priority);
        if (status == SubQueuePopStatus.Success)
        {
            // A successful removal frees a bounded slot (no-op on an unbounded queue). This one site
            // covers both the sampling and verification-scan pop paths.
            OnElementRemovedFromBounded();
        }

        return status;
    }

    /// <summary>
    /// Compares two priorities through the dual path: the devirtualized
    /// <see cref="Comparer{T}.Default"/> call when the stored comparer is null (value-type priorities
    /// with default ordering), otherwise the cached comparer field. Mirrors
    /// <c>SubQueue.CompareEffective</c> so the queue-level two-choice ordering uses the same
    /// devirtualized dispatch as the sub-queue heaps. Duplicated rather than shared: the BCL's
    /// <see cref="PriorityQueue{TElement, TPriority}"/> keeps its dual comparer paths local to the
    /// type for the same JIT-constant-folding reason.
    /// </summary>
    /// <param name="x">The left priority.</param>
    /// <param name="y">The right priority.</param>
    /// <returns>The comparison result per <see cref="IComparer{T}.Compare"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CompareEffective(TPriority x, TPriority y)
        => typeof(TPriority).IsValueType && _comparer is null
            ? Comparer<TPriority>.Default.Compare(x, y)
            : _comparer!.Compare(x, y);
}
