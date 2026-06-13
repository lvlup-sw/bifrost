// =============================================================================
// <copyright file="LockingPriorityQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry/Concurrency/NaiveConcurrentPriorityQueue.cs), renamed on port.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Bifrost.Concurrency;

/// <summary>
/// A thread-safe priority queue with exact (non-relaxed) ordering that wraps
/// <see cref="PriorityQueue{TElement, TPriority}"/> with a single global lock.
/// </summary>
/// <typeparam name="TElement">The type of the elements stored in the queue.</typeparam>
/// <typeparam name="TPriority">The type used for priority values.</typeparam>
/// <remarks>
/// <para>
/// This is a supported binding, not a baseline strawman: every dequeue returns the exact
/// minimum-priority element, because all operations serialize on one global lock around a
/// standard binary heap. The trade-off is that the lock eliminates intra-queue parallelism,
/// so throughput degrades as contention rises.
/// </para>
/// <para>
/// <b>When to prefer this queue:</b>
/// </para>
/// <list type="bullet">
/// <item>Low contention: roughly 1-8 workers, especially with seconds-long work items where
/// queue operations are a negligible fraction of total work. In this regime the lock-based
/// queue is competitive with or better than the MultiQueue-based alternative.</item>
/// <item>Exact ordering is required: consumers must always receive the true minimum-priority
/// element, with no rank error tolerated.</item>
/// </list>
/// <para>
/// <b>When to prefer the MultiQueue-based <c>ConcurrentPriorityQueue&lt;TElement, TPriority&gt;</c>:</b>
/// high contention (many workers hammering the queue) where relaxed ordering (a bounded rank
/// error on dequeue) is acceptable in exchange for scalable throughput. Pick per measurement:
/// benchmark both bindings under your workload's contention profile.
/// </para>
/// <para>
/// <b>Thread Safety:</b> All operations are protected by a single <c>lock</c> statement,
/// ensuring thread safety but eliminating any parallelism.
/// </para>
/// <para>
/// <b>Performance Characteristics:</b>
/// </para>
/// <list type="bullet">
/// <item>Single-threaded: Comparable to <see cref="PriorityQueue{TElement, TPriority}"/> (O(log n) operations)</item>
/// <item>Multi-threaded: Throughput degrades as thread count increases due to lock contention</item>
/// </list>
/// </remarks>
[DebuggerDisplay("Count = {Count}")]
public sealed class LockingPriorityQueue<TElement, TPriority>
{
    private readonly PriorityQueue<TElement, TPriority> _innerQueue;
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LockingPriorityQueue{TElement, TPriority}"/> class
    /// with default priority comparison.
    /// </summary>
    public LockingPriorityQueue()
        : this(0, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LockingPriorityQueue{TElement, TPriority}"/> class
    /// with the specified priority comparer.
    /// </summary>
    /// <param name="comparer">
    /// The <see cref="IComparer{TPriority}"/> to use for comparing priorities.
    /// If <c>null</c>, the default comparer for <typeparamref name="TPriority"/> is used.
    /// </param>
    public LockingPriorityQueue(IComparer<TPriority>? comparer)
        : this(0, comparer)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LockingPriorityQueue{TElement, TPriority}"/> class
    /// with the specified initial capacity.
    /// </summary>
    /// <param name="initialCapacity">The initial capacity of the underlying heap.</param>
    public LockingPriorityQueue(int initialCapacity)
        : this(initialCapacity, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LockingPriorityQueue{TElement, TPriority}"/> class
    /// with the specified initial capacity and priority comparer. This is the core constructor
    /// that the other overloads chain to.
    /// </summary>
    /// <param name="initialCapacity">The initial capacity of the underlying heap.</param>
    /// <param name="comparer">
    /// The <see cref="IComparer{TPriority}"/> to use for comparing priorities.
    /// If <c>null</c>, the default comparer for <typeparamref name="TPriority"/> is used.
    /// </param>
    public LockingPriorityQueue(int initialCapacity, IComparer<TPriority>? comparer)
    {
        _innerQueue = new PriorityQueue<TElement, TPriority>(initialCapacity, comparer);
    }

    /// <summary>
    /// Gets the number of elements in the queue.
    /// </summary>
    /// <remarks>
    /// This property acquires the lock to ensure a consistent count.
    /// The count may change immediately after being read in concurrent scenarios.
    /// </remarks>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _innerQueue.Count;
            }
        }
    }

    /// <summary>
    /// Adds an element with the specified priority to the queue.
    /// </summary>
    /// <param name="element">The element to add.</param>
    /// <param name="priority">The priority of the element.</param>
    /// <remarks>
    /// This operation acquires the global lock, blocking all other operations
    /// until complete. Time complexity is O(log n) for the heap operation,
    /// but throughput is limited by lock contention under concurrency.
    /// </remarks>
    public void Enqueue(TElement element, TPriority priority)
    {
        lock (_lock)
        {
            _innerQueue.Enqueue(element, priority);
        }
    }

    /// <summary>
    /// Attempts to remove and return the element with the minimum priority.
    /// </summary>
    /// <param name="element">
    /// When this method returns <c>true</c>, contains the dequeued element;
    /// otherwise, the default value of <typeparamref name="TElement"/>.
    /// </param>
    /// <param name="priority">
    /// When this method returns <c>true</c>, contains the priority of the dequeued element;
    /// otherwise, the default value of <typeparamref name="TPriority"/>.
    /// </param>
    /// <returns>
    /// <c>true</c> if an element was successfully dequeued; <c>false</c> if the queue was empty.
    /// </returns>
    /// <remarks>
    /// This operation acquires the global lock, blocking all other operations
    /// until complete. Time complexity is O(log n) for the heap operation,
    /// but throughput is limited by lock contention under concurrency.
    /// </remarks>
    public bool TryDequeue(
        [MaybeNullWhen(false)] out TElement element,
        [MaybeNullWhen(false)] out TPriority priority)
    {
        lock (_lock)
        {
            return _innerQueue.TryDequeue(out element, out priority);
        }
    }

    /// <summary>
    /// Attempts to return the element with the minimum priority without removing it.
    /// </summary>
    /// <param name="element">
    /// When this method returns <c>true</c>, contains the element at the front of the queue;
    /// otherwise, the default value of <typeparamref name="TElement"/>.
    /// </param>
    /// <param name="priority">
    /// When this method returns <c>true</c>, contains the priority of the element at the front;
    /// otherwise, the default value of <typeparamref name="TPriority"/>.
    /// </param>
    /// <returns>
    /// <c>true</c> if an element was found; <c>false</c> if the queue was empty.
    /// </returns>
    /// <remarks>
    /// This operation acquires the global lock, blocking all other operations
    /// until complete. Unlike the MultiQueue-based implementation, this peek
    /// operation is NOT lock-free.
    /// </remarks>
    public bool TryPeek(
        [MaybeNullWhen(false)] out TElement element,
        [MaybeNullWhen(false)] out TPriority priority)
    {
        lock (_lock)
        {
            return _innerQueue.TryPeek(out element, out priority);
        }
    }

    /// <summary>
    /// Removes all elements from the queue.
    /// </summary>
    /// <remarks>
    /// This operation acquires the global lock, blocking all other operations
    /// until complete.
    /// </remarks>
    public void Clear()
    {
        lock (_lock)
        {
            _innerQueue.Clear();
        }
    }
}
