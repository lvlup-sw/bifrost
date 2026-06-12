// =============================================================================
// <copyright file="LockingPriorityQueueTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry.Tests/Unit/NaiveConcurrentPriorityQueueTests.cs), renamed per design DR-1.

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Unit tests for <see cref="LockingPriorityQueue{TElement, TPriority}"/>.
/// </summary>
public class LockingPriorityQueueTests
{
    #region Constructor Tests

    /// <summary>
    /// Verifies the default constructor creates an empty queue.
    /// </summary>
    [Test]
    public async Task Constructor_Default_CreatesEmptyQueue()
    {
        // Arrange & Act
        var queue = new LockingPriorityQueue<string, int>();

        // Assert
        await Assert.That(queue.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies a custom comparer controls dequeue ordering.
    /// </summary>
    [Test]
    public async Task Constructor_WithComparer_UsesComparer()
    {
        // Arrange - Use reverse comparer (max-heap behavior)
        var reverseComparer = Comparer<int>.Create((a, b) => b.CompareTo(a));
        var queue = new LockingPriorityQueue<string, int>(reverseComparer);

        // Act
        queue.Enqueue("low", 1);
        queue.Enqueue("high", 100);
        queue.TryDequeue(out var element, out var priority);

        // Assert - With reverse comparer, highest priority value comes first
        await Assert.That(element).IsEqualTo("high");
        await Assert.That(priority).IsEqualTo(100);
    }

    /// <summary>
    /// Verifies the initial-capacity constructor creates an empty queue.
    /// </summary>
    [Test]
    public async Task Constructor_WithInitialCapacity_CreatesEmptyQueue()
    {
        // Arrange & Act
        var queue = new LockingPriorityQueue<string, int>(100);

        // Assert
        await Assert.That(queue.Count).IsEqualTo(0);
    }

    #endregion

    #region Enqueue Tests

    /// <summary>
    /// Verifies enqueuing a single element increases the count.
    /// </summary>
    [Test]
    public async Task Enqueue_SingleElement_IncreasesCount()
    {
        // Arrange
        var queue = new LockingPriorityQueue<string, int>();

        // Act
        queue.Enqueue("item", 1);

        // Assert
        await Assert.That(queue.Count).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies enqueuing multiple elements increases the count accordingly.
    /// </summary>
    [Test]
    public async Task Enqueue_MultipleElements_IncreasesCount()
    {
        // Arrange
        var queue = new LockingPriorityQueue<string, int>();

        // Act
        queue.Enqueue("first", 1);
        queue.Enqueue("second", 2);
        queue.Enqueue("third", 3);

        // Assert
        await Assert.That(queue.Count).IsEqualTo(3);
    }

    #endregion

    #region TryDequeue Tests

    /// <summary>
    /// Verifies TryDequeue on an empty queue returns false with default outputs.
    /// </summary>
    [Test]
    public async Task TryDequeue_EmptyQueue_ReturnsFalse()
    {
        // Arrange
        var queue = new LockingPriorityQueue<string, int>();

        // Act
        var result = queue.TryDequeue(out var element, out var priority);

        // Assert
        await Assert.That(result).IsFalse();
        await Assert.That(element).IsNull();
        await Assert.That(priority).IsEqualTo(default(int));
    }

    /// <summary>
    /// Verifies TryDequeue returns the sole element and empties the queue.
    /// </summary>
    [Test]
    public async Task TryDequeue_SingleElement_ReturnsElement()
    {
        // Arrange
        var queue = new LockingPriorityQueue<string, int>();
        queue.Enqueue("only", 42);

        // Act
        var result = queue.TryDequeue(out var element, out var priority);

        // Assert
        await Assert.That(result).IsTrue();
        await Assert.That(element).IsEqualTo("only");
        await Assert.That(priority).IsEqualTo(42);
        await Assert.That(queue.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies TryDequeue returns the minimum-priority element first.
    /// </summary>
    [Test]
    public async Task TryDequeue_MultipleElements_ReturnsMinimumPriority()
    {
        // Arrange
        var queue = new LockingPriorityQueue<string, int>();
        queue.Enqueue("high", 100);
        queue.Enqueue("low", 1);
        queue.Enqueue("medium", 50);

        // Act
        var result = queue.TryDequeue(out var element, out var priority);

        // Assert
        await Assert.That(result).IsTrue();
        await Assert.That(element).IsEqualTo("low");
        await Assert.That(priority).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies repeated TryDequeue drains the queue in exact priority order.
    /// </summary>
    [Test]
    public async Task TryDequeue_AllElements_ReturnsInPriorityOrder()
    {
        // Arrange
        var queue = new LockingPriorityQueue<string, int>();
        queue.Enqueue("c", 3);
        queue.Enqueue("a", 1);
        queue.Enqueue("b", 2);

        // Act & Assert
        queue.TryDequeue(out var e1, out var p1);
        await Assert.That(e1).IsEqualTo("a");
        await Assert.That(p1).IsEqualTo(1);

        queue.TryDequeue(out var e2, out var p2);
        await Assert.That(e2).IsEqualTo("b");
        await Assert.That(p2).IsEqualTo(2);

        queue.TryDequeue(out var e3, out var p3);
        await Assert.That(e3).IsEqualTo("c");
        await Assert.That(p3).IsEqualTo(3);

        await Assert.That(queue.Count).IsEqualTo(0);
    }

    #endregion

    #region TryPeek Tests

    /// <summary>
    /// Verifies TryPeek on an empty queue returns false with default outputs.
    /// </summary>
    [Test]
    public async Task TryPeek_EmptyQueue_ReturnsFalse()
    {
        // Arrange
        var queue = new LockingPriorityQueue<string, int>();

        // Act
        var result = queue.TryPeek(out var element, out var priority);

        // Assert
        await Assert.That(result).IsFalse();
        await Assert.That(element).IsNull();
        await Assert.That(priority).IsEqualTo(default(int));
    }

    /// <summary>
    /// Verifies TryPeek returns the minimum-priority element without removing it.
    /// </summary>
    [Test]
    public async Task TryPeek_WithElements_ReturnsMinimumWithoutRemoving()
    {
        // Arrange
        var queue = new LockingPriorityQueue<string, int>();
        queue.Enqueue("high", 100);
        queue.Enqueue("low", 1);

        // Act
        var result = queue.TryPeek(out var element, out var priority);

        // Assert
        await Assert.That(result).IsTrue();
        await Assert.That(element).IsEqualTo("low");
        await Assert.That(priority).IsEqualTo(1);
        await Assert.That(queue.Count).IsEqualTo(2); // Not removed
    }

    /// <summary>
    /// Verifies repeated TryPeek calls return the same element without removal.
    /// </summary>
    [Test]
    public async Task TryPeek_MultipleCalls_ReturnsSameElement()
    {
        // Arrange
        var queue = new LockingPriorityQueue<string, int>();
        queue.Enqueue("item", 1);

        // Act
        queue.TryPeek(out var e1, out _);
        queue.TryPeek(out var e2, out _);
        queue.TryPeek(out var e3, out _);

        // Assert
        await Assert.That(e1).IsEqualTo("item");
        await Assert.That(e2).IsEqualTo("item");
        await Assert.That(e3).IsEqualTo("item");
        await Assert.That(queue.Count).IsEqualTo(1);
    }

    #endregion

    #region Clear Tests

    /// <summary>
    /// Verifies Clear on an empty queue leaves it empty.
    /// </summary>
    [Test]
    public async Task Clear_EmptyQueue_RemainsEmpty()
    {
        // Arrange
        var queue = new LockingPriorityQueue<string, int>();

        // Act
        queue.Clear();

        // Assert
        await Assert.That(queue.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies Clear removes all elements from a populated queue.
    /// </summary>
    [Test]
    public async Task Clear_WithElements_RemovesAllElements()
    {
        // Arrange
        var queue = new LockingPriorityQueue<string, int>();
        queue.Enqueue("a", 1);
        queue.Enqueue("b", 2);
        queue.Enqueue("c", 3);

        // Act
        queue.Clear();

        // Assert
        await Assert.That(queue.Count).IsEqualTo(0);
        await Assert.That(queue.TryDequeue(out _, out _)).IsFalse();
    }

    #endregion

    #region Thread Safety Tests

    /// <summary>
    /// Verifies concurrent enqueues from multiple threads add all elements.
    /// </summary>
    [Test]
    public async Task ConcurrentEnqueue_MultipleThreads_AllElementsAdded()
    {
        // Arrange
        var queue = new LockingPriorityQueue<int, int>();
        const int threadCount = 10;
        const int itemsPerThread = 100;

        // Act
        Parallel.For(0, threadCount, threadId =>
        {
            for (int i = 0; i < itemsPerThread; i++)
            {
                int value = (threadId * itemsPerThread) + i;
                queue.Enqueue(value, value);
            }
        });

        // Assert
        await Assert.That(queue.Count).IsEqualTo(threadCount * itemsPerThread);
    }

    /// <summary>
    /// Verifies concurrent dequeues from multiple threads remove all elements exactly once.
    /// </summary>
    [Test]
    public async Task ConcurrentDequeue_MultipleThreads_AllElementsRemoved()
    {
        // Arrange
        var queue = new LockingPriorityQueue<int, int>();
        const int itemCount = 1000;

        for (int i = 0; i < itemCount; i++)
        {
            queue.Enqueue(i, i);
        }

        var dequeued = new System.Collections.Concurrent.ConcurrentBag<int>();

        // Act
        Parallel.For(0, 10, _ =>
        {
            while (queue.TryDequeue(out var element, out _))
            {
                dequeued.Add(element);
            }
        });

        // Assert
        await Assert.That(dequeued.Count).IsEqualTo(itemCount);
        await Assert.That(queue.Count).IsEqualTo(0);
    }

    #endregion
}
