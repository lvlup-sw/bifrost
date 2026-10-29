// =============================================================================
// <copyright file="ConcurrentPriorityQueue.Inspect.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

namespace Bifrost.Concurrency;

/// <content>
/// The non-destructive inspection surface: <see cref="TryPeekBestObserved"/> returns the minimum-priority
/// element without removing it. The scan of every sub-queue's <i>published top</i> is lock-free:
/// the seqlock (<see cref="SubQueue{TElement, TPriority}.TryReadTop"/>) publishes only the priority,
/// so a single brief lock on the winning sub-queue retrieves the element. The method never blocks:
/// it uses <c>TryEnter</c> only, takes at most one lock per attempt, never locks a non-winning
/// sub-queue, and after a bounded number of scan+retrieve attempts gives up with
/// <see langword="false"/>.
/// </content>
public sealed partial class ConcurrentPriorityQueue<TElement, TPriority>
{
    /// <summary>
    /// The bound on scan+retrieve attempts before <see cref="TryPeekBestObserved"/> reports failure. Each
    /// attempt is a full lock-free scan plus a single <c>TryEnter</c>-guarded element retrieval; an
    /// attempt is consumed only when the winner's lock is contended or its root no longer matches
    /// the scanned minimum (a concurrent writer changed the winner between scan and retrieval).
    /// A small bound keeps the worst case short instead of livelocking against churn, matching the
    /// bounded-retry discipline of the seqlock read path.
    /// </summary>
    private const int PeekRetryLimit = 4;

    /// <summary>
    /// The bound on scan+lock+revalidate attempts before <see cref="TryDequeueBestObserved"/> settles. Each
    /// attempt is a full lock-free scan, a single <c>TryEnter</c>-guarded revalidation of the live
    /// winner against the scanned runner-up, and (when revalidation holds) the pop. An attempt is
    /// consumed when the winner's lock is contended, the winner is now empty, the live root has
    /// become strictly worse than the runner-up (the scanned minimum was popped and a larger element
    /// exposed, so a better candidate may now live elsewhere), or the post-revalidation pop itself
    /// races to empty/contended. After the bound is exhausted the method settles on the best
    /// currently-observable winner rather than livelocking against churn.
    /// </summary>
    private const int DequeueMinRetryLimit = 3;

    /// <summary>Scans published tops and observes a best-observed candidate.</summary>
    /// <param name="element">The selected element, or default on failure.</param>
    /// <param name="priority">Its priority, or default on failure.</param>
    /// <returns>True on success; false when empty or when contention prevents observation.</returns>
    /// <remarks>
    /// This is not an exact global-minimum operation. The scan is non-atomic, skips unknown tops,
    /// and locks only the selected stripe. Another stripe may contain a smaller priority throughout
    /// the call. Bounded retries may return false while the queue contains elements. Only quiescent,
    /// uncontended use yields exact minimum order. Equal priorities have no stable FIFO guarantee.
    /// </remarks>
    public bool TryPeekBestObserved([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
    {
        SubQueue<TElement, TPriority>[] queues = _queues;

        for (int attempt = 0; attempt < PeekRetryLimit; attempt++)
        {
            // Lock-free pass over every published top; find the winning sub-queue's index and the
            // minimum priority observed this pass.
            if (!TryScanForMinimum(out int winner, out TPriority scannedMin))
            {
                // Every sub-queue scanned empty (or unknown): nothing to peek.
                break;
            }

            SubQueue<TElement, TPriority> subQueue = queues[winner];

            // At most ONE lock per attempt, on the winner only, never blocking.
            if (!subQueue.SyncLock.TryEnter())
            {
                // Contended winner: do not wait; rescan (the picture may also have moved).
                continue;
            }

            try
            {
                if (subQueue.TryPeekLiveMin(out TElement rootElement, out TPriority rootPriority) &&
                    CompareEffective(rootPriority, scannedMin) <= 0)
                {
                    // The live minimum is no worse than the minimum we scanned: accept it. TryPeekLiveMin
                    // reads D.front() on the buffered path (the published top), not the heap root, so the
                    // buffered resident minimum revalidates correctly here.
                    element = rootElement;
                    priority = rootPriority;
                    return true;
                }

                // The winner was mutated out from under us (empty now, or its root is strictly
                // larger than the scanned minimum): rescan for a possibly-better candidate.
            }
            finally
            {
                subQueue.SyncLock.Exit();
            }
        }

        element = default;
        priority = default;
        return false;
    }

    /// <summary>Scans published tops and removes a best-observed candidate.</summary>
    /// <param name="element">The selected element, or default on failure.</param>
    /// <param name="priority">Its priority, or default on failure.</param>
    /// <returns>True on success; false when empty or when contention prevents observation.</returns>
    /// <remarks>
    /// This is not an exact global-minimum operation. The scan is non-atomic, skips unknown tops,
    /// and locks only the selected stripe. Another stripe may contain a smaller priority throughout
    /// the call. Bounded retries may return false while the queue contains elements. Only quiescent,
    /// uncontended use yields exact minimum order. Equal priorities have no stable FIFO guarantee.
    /// </remarks>
    public bool TryDequeueBestObserved([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
    {
        SubQueue<TElement, TPriority>[] queues = _queues;

        for (int attempt = 0; attempt < DequeueMinRetryLimit; attempt++)
        {
            // Lock-free pass over every published top: the winner (minimum) and the runner-up
            // (second-smallest published top), used to revalidate the winner's live root below.
            if (!TryScanForMinimum(out int winner, out TPriority scannedMin))
            {
                // Every sub-queue scanned empty (or unknown): nothing to dequeue.
                break;
            }

            bool hasRunnerUp = TryScanRunnerUp(winner, out TPriority runnerUp);

            SubQueue<TElement, TPriority> subQueue = queues[winner];

            // At most ONE lock per attempt, on the winner only, never blocking.
            if (!subQueue.SyncLock.TryEnter())
            {
                // Contended winner: do not wait; consume the attempt and rescan.
                continue;
            }

            try
            {
                // Re-read the live root under the lock; a writer may have changed it since the scan.
                // Accept the winner only when it still holds a root that is no worse than the
                // runner-up's scanned top: if the live root is strictly larger than the runner-up
                // (the scanned minimum was popped, a larger element exposed) or the winner is now
                // empty, a better candidate may live in the runner-up's sub-queue, so we rescan.
                // Revalidate the winner's LIVE minimum, not its heap root: on the buffered path the live
                // minimum is D.front() (the published top), while the heap root is a larger key — a
                // heap-only re-read would mis-revalidate (or wrongly report empty when the heap is empty
                // but D is full), so the strict path must peek D.front() here.
                bool revalidated =
                    subQueue.TryPeekLiveMin(out _, out TPriority rootPriority) &&
                    (!hasRunnerUp || CompareEffective(rootPriority, runnerUp) <= 0);

                if (!revalidated)
                {
                    // The winner moved out from under us; rescan for a possibly-better candidate.
                    continue;
                }

                // Pop the revalidated root while STILL holding the lock. Releasing here and re-locking
                // in a separate pop would expose an unlocked window in which another thread could
                // remove the validated root and surface a worse one, so the best-observed path would pop
                // an element it never revalidated. Popping under the held lock makes the revalidated
                // root and the popped root the same element by construction.
                if (subQueue.PopHeldRoot(out element, out priority) == SubQueuePopStatus.Success)
                {
                    // This path pops the sub-queue DIRECTLY, bypassing TryPopFrom, so it must release
                    // the bounded reservation itself (no-op on an unbounded queue), the same single
                    // helper TryPopFrom uses, keeping one decrement site per successful removal.
                    OnElementRemovedFromBounded();
                    return true;
                }

                // Empty in the post-revalidation window — effectively unreachable, since the root was
                // just peeked under this same lock: consume the attempt and rescan.
            }
            finally
            {
                subQueue.SyncLock.Exit();
            }
        }

        element = default;
        priority = default;
        return false;
    }

    /// <summary>
    /// Scans every sub-queue's published top lock-free and reports the index of the sub-queue with
    /// the minimum priority and that minimum, ordered by the queue-level effective comparer.
    /// </summary>
    /// <param name="winner">
    /// When this method returns <see langword="true"/>, the index of the sub-queue whose published
    /// top was the minimum; otherwise <c>-1</c>.
    /// </param>
    /// <param name="minimum">
    /// When this method returns <see langword="true"/>, the minimum published priority; otherwise
    /// the default of <typeparamref name="TPriority"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when at least one sub-queue published a non-empty top during the scan;
    /// <see langword="false"/> when every sub-queue was observed empty or "unknown" (no candidate).
    /// </returns>
    /// <remarks>
    /// This is a pure lock-free scan: it takes no lock and invokes the comparer only between two
    /// already-published priorities, never on the seqlock read path itself. A
    /// <see cref="SubQueue{TElement, TPriority}.TryReadTop"/> result of "unknown" (a returned
    /// <see langword="false"/>, i.e. a read that raced a writer) and an "empty" snapshot are both
    /// skipped. The returned <paramref name="winner"/> is only a hint: a concurrent writer may have
    /// changed that sub-queue by the time a caller locks it, so callers must re-validate under the
    /// lock. Extracted so that <c>TryDequeueBestObserved</c> reuses the identical minimum-scan
    /// before its locked pop.
    /// </remarks>
    private bool TryScanForMinimum(out int winner, out TPriority minimum)
    {
        SubQueue<TElement, TPriority>[] queues = _queues;

        winner = -1;
        minimum = default!;
        bool found = false;

        for (int i = 0; i < queues.Length; i++)
        {
            if (!queues[i].TryReadTop(out TPriority top, out bool empty) || empty)
            {
                // Unknown (raced a writer) or empty: not a candidate this pass.
                continue;
            }

            if (found && CompareEffective(top, minimum) >= 0)
            {
                continue;
            }

            winner = i;
            minimum = top;
            found = true;
        }

        if (!found)
        {
            minimum = default!;
        }

        return found;
    }

    /// <summary>
    /// Scans every sub-queue's published top lock-free <i>except</i> the winner's, reporting the
    /// minimum published top among the rest, the "runner-up" that <see cref="TryDequeueBestObserved"/> uses
    /// to revalidate the winner's live root under the lock.
    /// </summary>
    /// <param name="winner">The winning sub-queue index to exclude from this runner-up scan.</param>
    /// <param name="runnerUp">
    /// When this method returns <see langword="true"/>, the smallest published top among the
    /// non-winner sub-queues; otherwise the default of <typeparamref name="TPriority"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when at least one non-winner sub-queue published a non-empty top during
    /// the scan; <see langword="false"/> when the winner was the only candidate (no runner-up exists,
    /// so the winner's root needs no upper bound; any reachable root is acceptable).
    /// </returns>
    /// <remarks>
    /// Like <see cref="TryScanForMinimum"/> this is a pure lock-free scan over the seqlock-published
    /// tops; it skips the winner and any "unknown" or "empty" snapshot. A returned
    /// <see langword="false"/> (no runner-up) lets <see cref="TryDequeueBestObserved"/> accept whatever live
    /// root the winner still holds; there is nowhere better to look.
    /// </remarks>
    private bool TryScanRunnerUp(int winner, out TPriority runnerUp)
    {
        SubQueue<TElement, TPriority>[] queues = _queues;

        runnerUp = default!;
        bool found = false;

        for (int i = 0; i < queues.Length; i++)
        {
            if (i == winner)
            {
                continue;
            }

            if (!queues[i].TryReadTop(out TPriority top, out bool empty) || empty)
            {
                // Unknown (raced a writer) or empty: not a candidate this pass.
                continue;
            }

            if (found && CompareEffective(top, runnerUp) >= 0)
            {
                continue;
            }

            runnerUp = top;
            found = true;
        }

        if (!found)
        {
            runnerUp = default!;
        }

        return found;
    }

    // CompareEffective (the dual-path queue-level comparer helper) is defined once for the
    // whole partial class in ConcurrentPriorityQueue.Dequeue.cs.
}
