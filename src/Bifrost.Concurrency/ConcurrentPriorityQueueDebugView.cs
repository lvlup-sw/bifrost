// =============================================================================
// <copyright file="ConcurrentPriorityQueueDebugView.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics;

namespace Bifrost.Concurrency;

/// <summary>
/// The debugger type proxy for <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/>,
/// following the <c>PriorityQueueDebugView</c> / <c>IProducerConsumerCollectionDebugView</c>
/// precedent: the debugger shows a <see cref="ConcurrentPriorityQueue{TElement, TPriority}.ToArray"/>
/// snapshot as the root expansion. The entries are unordered, matching the collection surface.
/// </summary>
/// <typeparam name="TElement">The element type stored alongside each priority.</typeparam>
/// <typeparam name="TPriority">The priority type ordered by the queue's comparer.</typeparam>
internal sealed class ConcurrentPriorityQueueDebugView<TElement, TPriority>
{
    private readonly ConcurrentPriorityQueue<TElement, TPriority> _queue;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentPriorityQueueDebugView{TElement, TPriority}"/> class.
    /// </summary>
    /// <param name="queue">The queue to expose to the debugger.</param>
    public ConcurrentPriorityQueueDebugView(ConcurrentPriorityQueue<TElement, TPriority> queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
    }

    /// <summary>Gets an unordered snapshot of the queue's entries for debugger display.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public (TElement Element, TPriority Priority)[] Items => _queue.ToArray();
}
