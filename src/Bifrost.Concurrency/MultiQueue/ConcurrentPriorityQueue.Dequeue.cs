// =============================================================================
// <copyright file="ConcurrentPriorityQueue.Dequeue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry/Concurrency/MultiQueue/ConcurrentPriorityQueue.Dequeue.cs)

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Bifrost.Concurrency.MultiQueue;

namespace Bifrost.Concurrency;

/// <content>
/// The relaxed dequeue surface (DR-8): the two-choice rule. A dequeue samples two random sub-queues,
/// reads their cached tops <i>without locking</i>, and pops the one whose published minimum is
/// smaller. This trades a bounded rank error (the popped element is one of the smallest priorities,
/// not necessarily the global minimum) for near-linear read scaling — strict-minimum semantics will
/// arrive separately through <c>TryDequeueMin</c>.
/// </content>
/// <remarks>
/// <para>
/// The dequeue is two-phase: bounded two-choice sampling (the fast path), then the authoritative
/// <see cref="TryDequeueVerificationScan"/>. The scan is what makes <see langword="false"/> safe to
/// return under concurrency: it concludes emptiness only after one full pass observes <i>every</i>
/// sub-queue empty, restarting (with <see cref="SpinWait"/> backoff) whenever a pass hits a
/// contended sub-queue — a held lock means another thread is mid-mutation there, so the pass's
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
    /// was observed empty at some point during the call — a full verification pass saw every
    /// sub-queue empty (the <c>ConcurrentQueue.TryDequeue</c> precedent: concurrent enqueues that
    /// complete after that observation window may of course be present by the time the caller
    /// reacts).
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Relaxed contract (DR-8).</b> This removes an element with <i>one of</i> the smallest
    /// priorities, not necessarily the global minimum: the two-choice rule samples two sub-queues
    /// and pops the better-looking one. The expected rank of the removed element (0 = the true
    /// minimum) is approximately <c>(5/6)·n</c>, where <c>n</c> is the sub-queue count
    /// (≈ 4 × processor count by default). Strict-minimum semantics are available through
    /// <c>TryDequeueMin</c>, or by wrapping <see cref="PriorityQueue{TElement, TPriority}"/> in a
    /// lock. When the queue collapses to a single sub-queue the relaxation is invisible and this
    /// returns the exact minimum.
    /// </para>
    /// <para>
    /// <b>Relaxation scales with core count — and with the stickiness factor <c>s</c> (read this
    /// before deploying across hardware tiers).</b>
    /// Because the sub-queue count is <c>n ≈ 4 × ProcessorCount</c> and the expected rank error is
    /// <c>(5/6)·n</c>, the looseness of this dequeue is a function of the <i>host's</i> processor
    /// count — not a fixed constant. The same binary that pops a top-≈27 element on an 8-core box
    /// (<c>n = 32</c>) pops a top-≈213 element on a 64-core server (<c>n = 256</c>). This is by
    /// design: more sub-queues are exactly what buys the reduced contention and throughput scaling,
    /// so the relaxation the caller tolerates and the parallelism they gain rise <i>together</i>.
    /// If a queue is constructed with a stickiness factor <c>s &gt; 1</c> (the optional throughput
    /// dial — see the stickiness constructor), the expected rank error scales again, roughly linearly,
    /// to <c>(5/6)·n·s</c>: the looseness then compounds across <i>both</i> the core count and <c>s</c>.
    /// The default <c>s = 1</c> leaves this bound at <c>(5/6)·n</c>, unchanged.
    /// Two consequences worth internalizing: (1) correctness must never depend on how close to the
    /// true minimum a pop lands — any such dependency will surface only on larger machines or under a
    /// larger <c>s</c>; and (2) if a fixed relaxation bound is required regardless of hardware, pin the
    /// sub-queue count through the internal constructor, keep <c>s = 1</c>, use <c>TryDequeueMin</c>,
    /// or wrap <see cref="PriorityQueue{TElement, TPriority}"/> in a lock.
    /// </para>
    /// <para>
    /// <b>Thread Safety:</b> This method is thread-safe and may be called concurrently from multiple
    /// threads. The sampling phase reads cached tops lock-free and pops under a try-lock that never
    /// blocks on a contended sub-queue; contention resamples rather than waits (DR-8 mirrors DR-7's
    /// wait-free locking).
    /// </para>
    /// </remarks>
    public bool TryDequeue([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
    {
        ThreadHandle handle = ThreadHandle.Current;
        int mask = _subQueueMask;

        // Phase 1 — two-choice sampling: a few bounded rounds of "sample two, pop the better top".
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
            // never blocks — stickiness stays wait-free). Neither outcome concludes the queue is
            // globally empty — only the authoritative scan does that.
            handle.ResetStickyDequeue();
        }

        // Phase 2 — the authoritative verification scan: sampling did not land a pop within its
        // budget, so settle the outcome (a real pop, or an authoritative observed-empty false).
        return TryDequeueVerificationScan(out element, out priority);
    }

    /// <summary>
    /// The authoritative empty verification scan (DR-9). Loops full passes over every sub-queue
    /// until it either pops an entry or completes one pass in which <i>every</i> sub-queue was
    /// observed empty — the only state in which returning <see langword="false"/> is legal.
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
    /// Anything else — a published top, or an unreadable top (a writer mid-publication) — must be
    /// settled under the lock via <see cref="TryPopFrom"/>: <see cref="SubQueuePopStatus.Success"/>
    /// returns the entry; <see cref="SubQueuePopStatus.Empty"/> is a lock-authoritative emptiness
    /// observation; <see cref="SubQueuePopStatus.Contended"/> voids the pass — a held lock means
    /// another thread is mid-mutation there, so the pass restarts after a <see cref="SpinWait"/>
    /// step (whose escalation to yields keeps a long contention storm from burning a core).
    /// </para>
    /// <para>
    /// There is deliberately no fixed pass cap: every restart requires an observed contention,
    /// i.e. another thread making progress on the same structure, which is the same system-wide
    /// progress argument the design's "wait-free locking" rests on. A false result without a clean
    /// all-empty pass would violate the observed-empty contract, so no bound may convert
    /// "contended" into "empty".
    /// </para>
    /// </remarks>
    private bool TryDequeueVerificationScan([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
    {
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
                    // empty queue) — observed empty under the lock, which is authoritative.
                    emptyObserved++;
                    continue;
                }

                // Contended: a writer holds this sub-queue's lock right now. The pass's evidence
                // is void — restart it rather than ever counting a contended queue as empty.
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
    /// so the sampling and scan paths (and task 011's rigorous scan) stay consistent.
    /// </summary>
    /// <param name="index">The sub-queue index to pop from.</param>
    /// <param name="element">The popped element on <see cref="SubQueuePopStatus.Success"/>; otherwise the default value.</param>
    /// <param name="priority">The popped priority on <see cref="SubQueuePopStatus.Success"/>; otherwise the default value.</param>
    /// <returns>The three-way pop outcome from the sub-queue.</returns>
    /// <remarks>
    /// On <see cref="SubQueuePopStatus.Success"/> this releases one bounded-capacity reservation
    /// (<see cref="OnElementRemovedFromBounded"/>) — the single release site that covers <i>both</i>
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
    /// Compares two priorities through the DR-6 dual path: the devirtualized
    /// <see cref="Comparer{T}.Default"/> call when the stored comparer is null (value-type priorities
    /// with default ordering), otherwise the cached comparer field. Mirrors
    /// <c>SubQueue.CompareEffective</c> so the queue-level two-choice ordering uses the same
    /// devirtualized dispatch as the sub-queue heaps. Deliberately duplicated rather than shared:
    /// the BCL's <see cref="PriorityQueue{TElement, TPriority}"/> keeps its dual comparer paths
    /// local to the type for the same JIT-constant-folding reason.
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
