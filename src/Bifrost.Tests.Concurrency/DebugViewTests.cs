// =============================================================================
// <copyright file="DebugViewTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Coverage for <see cref="ConcurrentPriorityQueueDebugView{TElement, TPriority}"/> (DR-4): the
/// debugger type-proxy that surfaces a <c>ToArray</c> snapshot as the debugger root expansion, and
/// its null-argument guard.
/// </summary>
/// <remarks>
/// The debug view is internal and visible to this test project. These tests assert it projects the
/// exact queue snapshot (not an empty or stale view) and rejects a null queue — a debug view that
/// silently returned an empty array would mislead anyone inspecting the queue in the debugger.
/// </remarks>
public class DebugViewTests
{
    /// <summary>
    /// The debug view's <c>Items</c> projects the queue's full unordered snapshot: every enqueued
    /// element appears exactly once, matching <see cref="ConcurrentPriorityQueue{TElement, TPriority}.ToArray"/>.
    /// </summary>
    [Test]
    public async Task DebugView_Items_ProjectsFullSnapshot()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 8, boundedCapacity: -1, comparer: null);
        const int n = 100;
        for (int i = 0; i < n; i++)
        {
            queue.Enqueue(i, i);
        }

        var view = new ConcurrentPriorityQueueDebugView<int, int>(queue);
        (int Element, int Priority)[] items = view.Items;

        await Assert.That(items.Length).IsEqualTo(n).Because("the debug view surfaces every element in the queue");

        int[] sorted = items.Select(t => t.Priority).OrderBy(p => p).ToArray();
        for (int i = 0; i < n; i++)
        {
            await Assert.That(sorted[i]).IsEqualTo(i).Because($"the debug snapshot contains priority {i} exactly once");
        }

        bool elementsMatch = items.All(t => t.Element == t.Priority);
        await Assert.That(elementsMatch).IsTrue().Because("each debug entry carries its own element == priority pair");
    }

    /// <summary>
    /// The debug view of an empty queue projects an empty array (not null), so the debugger shows a
    /// correct empty expansion.
    /// </summary>
    [Test]
    public async Task DebugView_EmptyQueue_ProjectsEmptyArray()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 4, boundedCapacity: -1, comparer: null);

        var view = new ConcurrentPriorityQueueDebugView<int, int>(queue);

        await Assert.That(view.Items).IsNotNull().Because("the debug view never surfaces null Items");
        await Assert.That(view.Items.Length).IsEqualTo(0).Because("an empty queue's debug view is an empty array");
    }

    /// <summary>
    /// The debug view rejects a null queue with <see cref="ArgumentNullException"/> (the constructor
    /// guard), so a misuse fails loudly rather than NRE-ing later in the debugger.
    /// </summary>
    [Test]
    public async Task DebugView_NullQueue_Throws()
    {
        await Assert.That(() => new ConcurrentPriorityQueueDebugView<int, int>(null!))
            .Throws<ArgumentNullException>().Because("the debug view constructor guards against a null queue");
    }
}
