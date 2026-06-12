// =============================================================================
// <copyright file="IThroughputQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry.Benchmarks/Throughput/IThroughputQueue.cs);
// NaiveQueueAdapter renamed to LockingQueueAdapter per design DR-1 (NaiveConcurrentPriorityQueue -> LockingPriorityQueue).

using Bifrost.Concurrency;

namespace Bifrost.Benchmarks.Concurrency;

/// <summary>
/// A tiny uniform façade over the three throughput targets so the workload loops stay
/// target-agnostic: a workload calls <see cref="Enqueue"/> / <see cref="TryDequeue"/> and the
/// adapter dispatches to the relaxed <c>TryDequeue</c>, the strict <c>TryDequeueMin</c>, or the
/// locking global-lock queue. The element type is fixed to <see cref="long"/> and the priority to
/// <see cref="long"/> — the harness measures contention, not generic-shape variation.
/// </summary>
/// <remarks>
/// The shared queue instance is created once per run and used by every worker thread; the adapters
/// hold no per-call state, so a single adapter is safe to share across all threads.
/// </remarks>
internal interface IThroughputQueue
{
    /// <summary>Enqueues an element with the given priority on the shared queue.</summary>
    /// <param name="priority">The priority value (also used as the element).</param>
    void Enqueue(long priority);

    /// <summary>
    /// Attempts to remove one element, using the target's removal semantics (relaxed two-choice,
    /// strict minimum, or locking global-lock dequeue).
    /// </summary>
    /// <returns><see langword="true"/> when an element was removed; otherwise <see langword="false"/>.</returns>
    bool TryDequeue();
}

/// <summary>The MultiQueue queue driven through the relaxed two-choice <c>TryDequeue</c>.</summary>
internal sealed class RelaxedQueueAdapter : IThroughputQueue
{
    private readonly ConcurrentPriorityQueue<long, long> _queue;

    /// <summary>Initializes a new adapter over a fresh relaxed MultiQueue queue with the given stickiness factor.</summary>
    /// <param name="stickiness">The stickiness factor <c>s</c> the queue samples sub-queues with.</param>
    public RelaxedQueueAdapter(int stickiness) => _queue = new ConcurrentPriorityQueue<long, long>(boundedCapacity: -1, stickiness: stickiness);

    /// <inheritdoc/>
    public void Enqueue(long priority) => _queue.Enqueue(priority, priority);

    /// <inheritdoc/>
    public bool TryDequeue() => _queue.TryDequeue(out _, out _);
}

/// <summary>The MultiQueue queue driven through the strict O(n) <c>TryDequeueMin</c>.</summary>
internal sealed class DequeueMinQueueAdapter : IThroughputQueue
{
    private readonly ConcurrentPriorityQueue<long, long> _queue;

    /// <summary>Initializes a new adapter over a fresh strict-semantics MultiQueue queue with the given stickiness factor.</summary>
    /// <param name="stickiness">
    /// The stickiness factor <c>s</c>; threaded through for a uniform construction surface, though the
    /// strict <c>TryDequeueMin</c> path scans all sub-queues and is unaffected by sampling stickiness.
    /// </param>
    public DequeueMinQueueAdapter(int stickiness) => _queue = new ConcurrentPriorityQueue<long, long>(boundedCapacity: -1, stickiness: stickiness);

    /// <inheritdoc/>
    public void Enqueue(long priority) => _queue.Enqueue(priority, priority);

    /// <inheritdoc/>
    public bool TryDequeue() => _queue.TryDequeueMin(out _, out _);
}

/// <summary>The <c>LockingPriorityQueue</c> global-lock baseline.</summary>
internal sealed class LockingQueueAdapter : IThroughputQueue
{
    private readonly LockingPriorityQueue<long, long> _queue;

    /// <summary>Initializes a new adapter over a fresh locking global-lock queue.</summary>
    public LockingQueueAdapter() => _queue = new LockingPriorityQueue<long, long>();

    /// <inheritdoc/>
    public void Enqueue(long priority) => _queue.Enqueue(priority, priority);

    /// <inheritdoc/>
    public bool TryDequeue() => _queue.TryDequeue(out _, out _);
}
