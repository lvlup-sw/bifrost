// =============================================================================
// <copyright file="ConcurrentPriorityQueue.Dequeue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bifrost.Concurrency;

/// <content>
/// The relaxed dequeue surface: the two-choice rule. A dequeue samples two random sub-queues,
/// reads their cached tops <i>without locking</i>, and pops the one whose published minimum is
/// smaller. This trades exact minimum selection for concurrency. Best-observed inspection scans
/// more stripes but also does not promise a global minimum under concurrent mutation.
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

    // CS0649: when BIFROST_TEST_HOOKS is undefined (the shipped package, see Bifrost.Concurrency.csproj)
    // these two counters are read by their ...ForTest getters but never assigned, since the only writers
    // are the BIFROST_TEST_HOOKS-gated increments below. That is the intended stripped-build state.
#pragma warning disable CS0649

    /// <summary>
    /// TEST-ONLY instrumentation: the number of times the sparse-fallback routing step popped an
    /// element, i.e. read the occupancy bitmask, routed to a populated sub-queue via
    /// <see cref="BitOperations.TrailingZeroCount(ulong)"/>, and returned without an O(n) scan.
    /// Incremented with <see cref="Interlocked.Increment(ref long)"/> since multiple consumers race.
    /// </summary>
    private long _debugRoutingHitCount;

    /// <summary>
    /// TEST-ONLY instrumentation: the number of times the verification scan ran after routing found
    /// nothing to pop. Under dense load neither this nor <see cref="_debugRoutingHitCount"/> moves,
    /// since sampling lands a pop within budget.
    /// </summary>
    private long _debugScanEntryCount;
#pragma warning restore CS0649

    /// <summary>TEST-ONLY: see <see cref="_debugRoutingHitCount"/>.</summary>
    internal long DebugRoutingHitCountForTest => Volatile.Read(ref _debugRoutingHitCount);

    /// <summary>TEST-ONLY: see <see cref="_debugScanEntryCount"/>.</summary>
    internal long DebugScanEntryCountForTest => Volatile.Read(ref _debugScanEntryCount);

    /// <summary>
    /// TEST-ONLY: invoked with the stripe index before the verification scan observes it.
    /// Production leaves this null. A test uses it to move an item across stripes
    /// between observations.
    /// </summary>
    internal Action<int>? DebugBeforeVerificationIndex { get; set; }

    /// <summary>
    /// TEST-ONLY: drives the post-sampling dequeue path in isolation — bitmask routing first, then
    /// the verification scan only if routing found nothing — <i>skipping</i> the random two-choice
    /// sampling that normally runs ahead of them. This lets the routing tests assert the routing/scan
    /// split deterministically, where the random sampler would only reach it now and then. Production
    /// <see cref="TryDequeue"/> runs the same routing-then-scan sequence once its sampling budget is
    /// spent.
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
    /// Two distinct sampled stripes compete by cached priority; contention resamples. Occupancy
    /// fallback and verification scans retain the existing relaxed algorithm. Removal is not globally
    /// minimum and no fixed rank bound is promised. Verification can retry indefinitely while a
    /// populated stripe remains locked. Use a synchronized BCL PriorityQueue for exact ordering.
    /// </para>
    /// <para>
    /// Two-choice sampling runs a few bounded rounds (<see cref="SampleRounds"/>) of "sample two, pop
    /// the better top". When <c>mask == 0</c> (one sub-queue), <c>NextStickyPair</c> cannot return
    /// distinct indices, so the round samples the single sub-queue as both choices. Each round reads
    /// both cached tops through <c>TryReadTop</c> without locking. An empty or unknown
    /// (<see langword="false"/>) read counts as +infinity, which loses every comparison, so the round
    /// prefers the other (finite) sub-queue. If both reads are non-finite, the round pops from
    /// <c>i</c>, observes Empty or Contended, and advances to the next round.
    /// </para>
    /// <para>
    /// After an Empty or Contended pop, <c>ResetStickyDequeue</c> ends the sticky period. Thus the next
    /// round re-rolls a FRESH pair and does not re-sample the same drained or contended selection.
    /// A contended pair therefore never blocks, and stickiness stays retry-based. Neither outcome
    /// concludes that the queue is globally empty. Only the authoritative scan does that.
    /// </para>
    /// <para>
    /// When sampling misses its budget, sparse-fallback routing (<see cref="TryRouteViaOccupancy"/>)
    /// runs before the O(n) scan. It consults the occupancy bitmask and routes straight to a populated
    /// sub-queue. This collapses the sparse-but-non-empty drain case (the common case as the queue
    /// empties) to an O(n/64)-word read plus one locked pop. Routing is only a hint. It never returns
    /// <see langword="false"/>, and when no bit is set (or every set bit is stale) it falls through to
    /// the scan.
    /// </para>
    /// <para>
    /// If sampling and routing did not land a pop, <see cref="TryDequeueVerificationScan"/> settles
    /// the outcome: either a real pop or an authoritative observed-empty <see langword="false"/>.
    /// The scan stays the sole authority for returning <see langword="false"/>.
    /// </para>
    /// </remarks>
    public bool TryDequeue([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
    {
        ThreadHandle handle = ThreadHandle.Current;
        int mask = _subQueueMask;

        for (int round = 0; round < SampleRounds; round++)
        {
            int i, j;
            if (mask == 0)
            {
                i = 0;
                j = 0;
            }
            else
            {
                handle.NextStickyPair(mask, _stickiness, out i, out j);
            }

            bool iFinite = _queues[i].TryReadTop(out TPriority topI, out bool emptyI) && !emptyI;
            bool jFinite = _queues[j].TryReadTop(out TPriority topJ, out bool emptyJ) && !emptyJ;

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

            handle.ResetStickyDequeue();
        }

        if (TryRouteViaOccupancy(out element, out priority))
        {
            return true;
        }

        return TryDequeueVerificationScan(out element, out priority);
    }

    /// <summary>
    /// The sparse-fallback routing step: reads the occupancy bitmask and, for each set bit
    /// lowest-first (via <see cref="BitOperations.TrailingZeroCount(ulong)"/>), attempts a locked pop
    /// of that sub-queue. A <see cref="SubQueuePopStatus.Success"/> returns immediately; an
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
    /// <b>Staleness safety.</b> The occupancy words are read lock-free and <i>will</i> sometimes be
    /// stale, and that is safe in both directions. A <b>stale-set</b> bit (reads 1 while the sub-queue
    /// is actually empty) yields <see cref="SubQueuePopStatus.Empty"/> from the locked pop and is
    /// skipped: wasted work, never a wrong answer. A <b>stale or lost clear</b> can only cost a
    /// redundant routing attempt, because this method never returns <see langword="false"/> as proof
    /// of emptiness; that is the job of the verification scan, which locks and inspects every
    /// sub-queue directly. A multi-word read is not an atomic snapshot, but since an all-zero read is
    /// never treated as proof of emptiness here, the non-atomicity only affects whether routing finds
    /// a populated queue on the first pass, not correctness.
    /// </para>
    /// <para>
    /// The words are re-read on each outer iteration so a bit set by a concurrent producer after the
    /// initial read can still be routed to within the same call, tightening the sparse win under churn.
    /// Progress is bounded: the loop advances only on a freshly observed set bit, and a pop that
    /// succeeds returns, so it cannot spin without either making progress or running out of set bits.
    /// </para>
    /// <para>
    /// Each <c>Volatile.Read</c> of an occupancy word is a snapshot. The loop routes to each set bit
    /// of the snapshot, lowest-first. A bit that a concurrent drainer cleared after the snapshot
    /// resolves to Empty under the lock and is skipped. On Empty (a stale-set bit) or Contended (a
    /// race), <c>bits &amp;= bits - 1</c> drops that bit and the loop tries the next set bit in the
    /// word. Neither outcome lets routing conclude anything about emptiness. When no set bit yields a
    /// pop, routing NEVER returns <see langword="false"/> as proof of emptiness. The caller falls
    /// through to the verification scan, which is the sole authority for <see langword="false"/>.
    /// </para>
    /// </remarks>
    private bool TryRouteViaOccupancy([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
    {
        ulong[] occupancy = _occupancy;

        for (int word = 0; word < occupancy.Length; word++)
        {
            ulong bits = Volatile.Read(ref occupancy[word]);

            while (bits != 0)
            {
                int bit = BitOperations.TrailingZeroCount(bits);
                int index = (word << 6) + bit;

                SubQueuePopStatus status = TryPopFrom(index, out element, out priority);
                if (status == SubQueuePopStatus.Success)
                {
#if BIFROST_TEST_HOOKS
                    Interlocked.Increment(ref _debugRoutingHitCount);
#endif
                    return true;
                }

                bits &= bits - 1;
            }
        }

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
    /// progress argument the design's "retry-based locking" rests on. A false result without a clean
    /// all-empty pass would violate the observed-empty contract, so no bound may convert
    /// "contended" into "empty".
    /// </para>
    /// <para>
    /// The <c>BIFROST_TEST_HOOKS</c> block counts entry into the O(n) scan (test-only
    /// instrumentation). On the sparse path, routing handles the pop and this count does not move. The
    /// scan is reached only on a genuinely empty queue, or on one that looks empty for a moment
    /// because every bit was just cleared. The block is compiled out of the shipped package
    /// (<c>BIFROST_TEST_HOOKS</c> is undefined; see Bifrost.Concurrency.csproj).
    /// </para>
    /// <para>
    /// The cheap route is a stable seqlock read through <c>TryReadTop</c>. When it says "empty", that
    /// is the emptiness observation of the sub-queue for the pass, with no lock traffic. A
    /// published-non-empty or unknown read is settled under the lock. An <c>Empty</c> result from
    /// <see cref="TryPopFrom"/> means that the sub-queue was emptied between the read and the pop, or
    /// that the read was "unknown" over an empty queue. The lock makes that observation authoritative.
    /// A Contended result means that a writer holds the lock of that sub-queue now, so
    /// <c>passVoided</c> voids the evidence of the pass. The pass restarts and never counts a contended
    /// sub-queue as empty. A pass also retries when <c>TryValidateEmptySnapshot</c> fails, because an
    /// item moved behind the scan.
    /// </para>
    /// </remarks>
    private bool TryDequeueVerificationScan([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
    {
#if BIFROST_TEST_HOOKS
        Interlocked.Increment(ref _debugScanEntryCount);
#endif

        SubQueue<TElement, TPriority>[] queues = _queues;
        SpinWait spinner = default;
        uint[]? rentedVersions = null;
        Span<uint> versions = queues.Length <= 256
            ? stackalloc uint[queues.Length]
            : (rentedVersions = ArrayPool<uint>.Shared.Rent(queues.Length)).AsSpan(0, queues.Length);

        try
        {
        while (true)
        {
            int emptyObserved = 0;
            bool passVoided = false;

            for (int index = 0; index < queues.Length; index++)
            {
                this.DebugBeforeVerificationIndex?.Invoke(index);

                if (queues[index].TryReadTop(out _, out bool empty) && empty)
                {
                    emptyObserved++;
                    continue;
                }

                SubQueuePopStatus status = TryPopFrom(index, out element, out priority);
                if (status == SubQueuePopStatus.Success)
                {
                    return true;
                }

                if (status == SubQueuePopStatus.Empty)
                {
                    emptyObserved++;
                    continue;
                }

                passVoided = true;
                break;
            }

            if (!passVoided && TryValidateEmptySnapshot(queues, versions))
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
        finally
        {
            if (rentedVersions is not null)
            {
                ArrayPool<uint>.Shared.Return(rentedVersions);
            }
        }
    }

    /// <summary>
    /// Returns true only when every stripe is empty at one common instant: two seqlock
    /// reads observe the same even version and an empty flag. A move from an unscanned
    /// stripe into an already-scanned stripe changes a version and must not count as empty.
    /// </summary>
    private static bool TryValidateEmptySnapshot(
        SubQueue<TElement, TPriority>[] queues,
        Span<uint> versions)
    {
        for (int index = 0; index < queues.Length; index++)
        {
            if (!queues[index].TryReadStableObservation(out uint version, out bool empty) || !empty)
            {
                return false;
            }

            versions[index] = version;
        }

        for (int index = 0; index < queues.Length; index++)
        {
            if (!queues[index].TryReadStableObservation(out uint version, out bool empty)
                || !empty
                || version != versions[index])
            {
                return false;
            }
        }

        return true;
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
    /// On an unbounded queue the release does nothing.
    /// </remarks>
    private SubQueuePopStatus TryPopFrom(int index, out TElement element, out TPriority priority)
    {
        SubQueuePopStatus status = _queues[index].TryLockedPop(out element, out priority);
        if (status == SubQueuePopStatus.Success)
        {
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
