// =============================================================================
// <copyright file="ConcurrentPriorityQueue.Inspect.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

namespace Bifrost.Concurrency;

/// <content>
/// The non-destructive inspection surface: <see cref="TryPeek"/> returns the minimum-priority
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
    /// The bound on scan+retrieve attempts before <see cref="TryPeek"/> reports failure. Each
    /// attempt is a full lock-free scan plus a single <c>TryEnter</c>-guarded element retrieval; an
    /// attempt is consumed only when the winner's lock is contended or its root no longer matches
    /// the scanned minimum (a concurrent writer changed the winner between scan and retrieval).
    /// A small bound keeps the worst case short instead of livelocking against churn, matching the
    /// bounded-retry discipline of the seqlock read path.
    /// </summary>
    private const int PeekRetryLimit = 4;

    /// <summary>
    /// The bound on scan+lock+revalidate attempts before <see cref="TryDequeueMin"/> settles. Each
    /// attempt is a full lock-free scan, a single <c>TryEnter</c>-guarded revalidation of the live
    /// winner against the scanned runner-up, and (when revalidation holds) the pop. An attempt is
    /// consumed when the winner's lock is contended, the winner is now empty, the live root has
    /// become strictly worse than the runner-up (the scanned minimum was popped and a larger element
    /// exposed, so a better candidate may now live elsewhere), or the post-revalidation pop itself
    /// races to empty/contended. After the bound is exhausted the method settles on the best
    /// currently-observable winner rather than livelocking against churn.
    /// </summary>
    private const int DequeueMinRetryLimit = 3;

    /// <summary>
    /// Attempts to return (without removing it) an element whose priority was the minimum among
    /// the sub-queue tops observed during the call.
    /// </summary>
    /// <param name="element">
    /// When this method returns <see langword="true"/>, the minimum-priority element; otherwise the
    /// default of <typeparamref name="TElement"/>.
    /// </param>
    /// <param name="priority">
    /// When this method returns <see langword="true"/>, the priority of <paramref name="element"/>;
    /// otherwise the default of <typeparamref name="TPriority"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> and an element whose priority was the minimum among the tops observed
    /// during the call; <see langword="false"/> when no element could be observed (the queue was
    /// empty, or every scan+retrieve attempt raced a concurrent mutation of the winning sub-queue).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Lock-free scan, single-lock retrieval: the minimum priority is found by
    /// scanning <i>all</i> sub-queues' published tops without taking any lock
    /// (<see cref="SubQueue{TElement, TPriority}.TryReadTop"/>; an "unknown" or "empty" snapshot is
    /// skipped), ordered by the queue-level effective comparer. Because the seqlock publishes only
    /// the <i>priority</i>, retrieving the actual element requires one brief lock: the winning
    /// sub-queue is acquired with <c>TryEnter</c> (never blocking), its heap root is read, and its
    /// priority is validated before the element is returned.
    /// </para>
    /// <para>
    /// Top validation accepts the root when it is no worse than the scanned minimum. A
    /// concurrent writer may mutate the winner between the lock-free scan and the lock acquisition.
    /// Under the winner's lock the live root is re-read; if it compares less-than-or-equal to the
    /// scanned minimum it is accepted (it is still a valid "minimum among the tops observed"
    /// answer, at worst an even smaller element that was just published). If the live root is
    /// strictly greater than the scanned minimum (the scanned minimum was popped and a larger
    /// element exposed), or the heap is now empty, the attempt is abandoned and the queue is
    /// rescanned, because a better candidate may now live elsewhere.
    /// </para>
    /// <para>
    /// Never blocks, with at most one lock per attempt: only the winning sub-queue is ever locked,
    /// and only with <c>TryEnter</c>; a contended winner consumes an attempt and triggers a rescan.
    /// After <see cref="PeekRetryLimit"/> attempts the method returns <see langword="false"/> rather
    /// than waiting.
    /// </para>
    /// <para>
    /// <b>Thread Safety:</b> This method is thread-safe and may be called concurrently with enqueues,
    /// dequeues, and other peeks.
    /// </para>
    /// </remarks>
    public bool TryPeek([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
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
                if (subQueue.TryHeapPeekRoot(out TElement rootElement, out TPriority rootPriority) &&
                    CompareEffective(rootPriority, scannedMin) <= 0)
                {
                    // The live root is no worse than the minimum we scanned: accept it.
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

    /// <summary>
    /// Attempts to remove and return the element whose priority was the minimum among the sub-queue
    /// tops observed during the call, the STRICT best-effort dequeue.
    /// </summary>
    /// <param name="element">
    /// When this method returns <see langword="true"/>, the removed minimum-priority element;
    /// otherwise the default of <typeparamref name="TElement"/>.
    /// </param>
    /// <param name="priority">
    /// When this method returns <see langword="true"/>, the priority of <paramref name="element"/>;
    /// otherwise the default of <typeparamref name="TPriority"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> and the removed element whose priority was the minimum among the tops
    /// observed during the call; <see langword="false"/> when no element could be observed (the queue
    /// was empty, or every scan+lock attempt raced a concurrent mutation of the winning sub-queue).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Removes the element whose priority was the minimum among elements observed during the call; concurrent enqueues may be missed.
    /// </para>
    /// <para>
    /// Strict and best-effort, an O(n) scan with no scalability claim. Unlike the relaxed
    /// two-choice <c>TryDequeue</c>, this path scans <i>every</i> sub-queue's published top on each
    /// attempt (reusing the identical lock-free minimum scan as <see cref="TryPeek"/>) to find the
    /// global minimum exactly. That full O(n) scan is slower and carries no scalability claim: it is
    /// the strict-semantics path callers reach for when the relaxed rank error is unacceptable, not
    /// the throughput path.
    /// </para>
    /// <para>
    /// Each attempt scans the tops for the winner (minimum) and the runner-up
    /// (second-smallest published top), then acquires the winner with <c>TryEnter</c>, never
    /// blocking. Under the lock the live root is re-read
    /// (<see cref="SubQueue{TElement, TPriority}.TryHeapPeekRoot"/>): a concurrent writer may have
    /// changed the root since the scan. If the winner is now empty, or its live root is strictly
    /// greater than the runner-up's scanned top (the scanned minimum was popped and a larger element
    /// exposed), the lock is released and the queue is rescanned, because a better candidate may now
    /// live in the runner-up's sub-queue. A contended winner likewise consumes an attempt and
    /// rescans.
    /// </para>
    /// <para>
    /// Pop semantics: the observation window includes the pop. Once revalidation holds, the lock
    /// is released and the winner is popped through its locked-pop entry point
    /// (<see cref="SubQueue{TElement, TPriority}.TryLockedPop"/>, which re-acquires the lock itself).
    /// Whatever that pop's <see cref="SubQueuePopStatus.Success"/> yields <i>is</i> the result: the
    /// contract is "the minimum among elements observed during the call", and the observation window
    /// includes the pop, so a smaller element that was published into the winner between revalidation
    /// and the pop is an even-better, in-contract answer. Conservation stays exact; nothing is ever
    /// reinserted. If the pop instead observes <see cref="SubQueuePopStatus.Empty"/> or
    /// <see cref="SubQueuePopStatus.Contended"/> (the root was taken or the lock re-contended in that
    /// tiny window), the attempt is consumed and the queue is rescanned.
    /// </para>
    /// <para>
    /// Never blocks; bounded retries, then settle. Only <c>TryEnter</c> is ever used. After
    /// <see cref="DequeueMinRetryLimit"/> attempts the method stops rescanning and returns the best
    /// element it could pop (or <see langword="false"/> if it could pop none), honoring the
    /// best-effort contract rather than livelocking against churn.
    /// </para>
    /// <para>
    /// <b>Thread Safety:</b> This method is thread-safe and may be called concurrently with enqueues,
    /// dequeues, and peeks.
    /// </para>
    /// </remarks>
    public bool TryDequeueMin([MaybeNullWhen(false)] out TElement element, [MaybeNullWhen(false)] out TPriority priority)
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
                bool revalidated =
                    subQueue.TryHeapPeekRoot(out _, out TPriority rootPriority) &&
                    (!hasRunnerUp || CompareEffective(rootPriority, runnerUp) <= 0);

                if (!revalidated)
                {
                    // The winner moved out from under us; rescan for a possibly-better candidate.
                    continue;
                }

                // Pop the revalidated root while STILL holding the lock. Releasing here and re-locking
                // in a separate pop would expose an unlocked window in which another thread could
                // remove the validated root and surface a worse one, so the strict-min path would pop
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
    /// lock. Extracted so that <c>TryDequeueMin</c> reuses the identical minimum-scan
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
    /// minimum published top among the rest, the "runner-up" that <see cref="TryDequeueMin"/> uses
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
    /// <see langword="false"/> (no runner-up) lets <see cref="TryDequeueMin"/> accept whatever live
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
