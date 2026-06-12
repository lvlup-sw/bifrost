// =============================================================================
// <copyright file="WorkQueueContractTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Queues;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Abstract contract-test suite for <see cref="IWorkQueue{T}"/> bindings.
/// </summary>
/// <remarks>
/// <para>
/// Each concrete binding (FIFO channel, concurrent priority, locking priority) derives
/// from this class and implements <see cref="CreateQueue(int)"/>; the derived class then
/// inherits and executes the entire suite, guaranteeing every binding satisfies the same
/// observable contract.
/// </para>
/// <para>
/// The suite encodes the canonical consumer pattern via <see cref="ConsumeOneAsync{T}"/>:
/// await <see cref="IWorkQueue{T}.WaitToDequeueAsync(CancellationToken)"/>, then
/// <see cref="IWorkQueue{T}.TryDequeue(out T)"/> — which may legitimately miss under
/// relaxed bindings — and on a miss loop back to the wait.
/// </para>
/// </remarks>
public abstract class WorkQueueContractTests
{
    /// <summary>
    /// Default capacity used by single-threaded contract tests.
    /// </summary>
    protected const int SmallCapacity = 4;

    /// <summary>
    /// Per-test ceiling for waits that are expected to complete promptly.
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Generous ceiling for the multi-producer/multi-consumer conservation test.
    /// </summary>
    private static readonly TimeSpan ConservationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Verifies that <see cref="IWorkQueue{T}.TryEnqueue(in T)"/> accepts an item while
    /// the queue is below capacity.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TryEnqueue_WhenBelowCapacity_ReturnsTrue()
    {
        // Arrange
        var queue = CreateQueue(SmallCapacity);

        // Act
        var accepted = queue.TryEnqueue(1);

        // Assert
        await Assert.That(accepted).IsTrue();
    }

    /// <summary>
    /// Verifies that <see cref="IWorkQueue{T}.TryEnqueue(in T)"/> rejects an item once
    /// the queue has been filled to capacity.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TryEnqueue_WhenAtCapacity_ReturnsFalse()
    {
        // Arrange
        var queue = CreateQueue(SmallCapacity);
        for (var i = 0; i < SmallCapacity; i++)
        {
            var filled = queue.TryEnqueue(i);
            await Assert.That(filled).IsTrue();
        }

        // Act
        var accepted = queue.TryEnqueue(SmallCapacity);

        // Assert
        await Assert.That(accepted).IsFalse();
    }

    /// <summary>
    /// Verifies that <see cref="IWorkQueue{T}.WaitToDequeueAsync(CancellationToken)"/>
    /// completes with <c>true</c> after an item has been enqueued.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WaitToDequeueAsync_AfterEnqueue_CompletesTrue()
    {
        // Arrange
        var queue = CreateQueue(SmallCapacity);
        queue.TryEnqueue(42);
        using var timeoutCts = new CancellationTokenSource(WaitTimeout);

        // Act
        var signaled = await queue.WaitToDequeueAsync(timeoutCts.Token).ConfigureAwait(false);

        // Assert
        await Assert.That(signaled).IsTrue();
    }

    /// <summary>
    /// Verifies the contract's shutdown semantic: cancelling the token passed to
    /// <see cref="IWorkQueue{T}.WaitToDequeueAsync(CancellationToken)"/> completes the
    /// pending wait with <c>false</c> — it must NOT throw
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WaitToDequeueAsync_OnShutdown_CompletesFalse()
    {
        // Arrange — empty queue, so the wait cannot be satisfied by an item.
        var queue = CreateQueue(SmallCapacity);
        using var shutdownCts = new CancellationTokenSource();
        var waitTask = queue.WaitToDequeueAsync(shutdownCts.Token).AsTask();

        // Act — signal shutdown via the cancellation pathway.
        await shutdownCts.CancelAsync().ConfigureAwait(false);
        var signaled = await waitTask.WaitAsync(WaitTimeout).ConfigureAwait(false);

        // Assert — the chosen semantic is completes-false, never OperationCanceledException.
        await Assert.That(signaled).IsFalse();
    }

    /// <summary>
    /// Verifies the canonical consume loop: after a successful wait,
    /// <see cref="IWorkQueue{T}.TryDequeue(out T)"/> may spuriously miss under relaxed
    /// bindings; the consumer loops back to the wait and eventually receives the item.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TryDequeue_AfterWait_YieldsItem_ToleratesSpuriousMissRetry()
    {
        // Arrange
        var queue = CreateQueue(SmallCapacity);
        queue.TryEnqueue(99);
        using var timeoutCts = new CancellationTokenSource(WaitTimeout);

        // Act — the canonical loop tolerates spurious TryDequeue misses by re-waiting.
        var (received, item) = await ConsumeOneAsync(queue, timeoutCts.Token).ConfigureAwait(false);

        // Assert
        await Assert.That(received).IsTrue();
        await Assert.That(item).IsEqualTo(99);
    }

    /// <summary>
    /// Verifies the documented <see cref="IWorkQueue{T}.Count"/> bounds: approximate under
    /// concurrency, exact at quiescence. With no concurrent producers or consumers, the
    /// count must be exact at every quiescent point.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Count_Approximate_WithinDocumentedBounds()
    {
        // Arrange
        const int enqueueCount = 3;
        var queue = CreateQueue(SmallCapacity);
        await Assert.That(queue.Count).IsEqualTo(0);

        // Act — N uncontended enqueues; the queue is quiescent after each operation.
        for (var i = 0; i < enqueueCount; i++)
        {
            queue.TryEnqueue(i);
        }

        // Assert — exact at quiescence.
        await Assert.That(queue.Count).IsEqualTo(enqueueCount);

        // Drain back to empty; count must return to exactly zero at quiescence.
        using var timeoutCts = new CancellationTokenSource(WaitTimeout);
        for (var i = 0; i < enqueueCount; i++)
        {
            var (received, _) = await ConsumeOneAsync(queue, timeoutCts.Token).ConfigureAwait(false);
            await Assert.That(received).IsTrue();
        }

        await Assert.That(queue.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies conservation under contention: with 4 producers each enqueueing 250 items
    /// and 4 consumers running the canonical consume loop, every item is received exactly
    /// once — no loss, no duplication — within a generous timeout.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Conservation_NItemsIn_NItemsOut_MultiProducerConsumer()
    {
        // Arrange
        const int producerCount = 4;
        const int consumerCount = 4;
        const int itemsPerProducer = 250;
        const int totalItems = producerCount * itemsPerProducer;

        var queue = CreateQueue(totalItems);
        using var timeoutCts = new CancellationTokenSource(ConservationTimeout);
        using var doneCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);

        var received = new ConcurrentQueue<int>();
        var receivedCount = 0;

        var producers = new Task[producerCount];
        for (var p = 0; p < producerCount; p++)
        {
            var producerIndex = p;
            producers[p] = Task.Run(async () =>
            {
                for (var i = 0; i < itemsPerProducer; i++)
                {
                    var item = (producerIndex * itemsPerProducer) + i;
                    while (!queue.TryEnqueue(item))
                    {
                        if (timeoutCts.IsCancellationRequested)
                        {
                            return;
                        }

                        await Task.Yield();
                    }
                }
            });
        }

        var consumers = new Task[consumerCount];
        for (var c = 0; c < consumerCount; c++)
        {
            consumers[c] = Task.Run(async () =>
            {
                while (true)
                {
                    var (gotItem, item) = await ConsumeOneAsync(queue, doneCts.Token).ConfigureAwait(false);
                    if (!gotItem)
                    {
                        return; // Shutdown (all items received) or timeout.
                    }

                    received.Enqueue(item);
                    if (Interlocked.Increment(ref receivedCount) == totalItems)
                    {
                        await doneCts.CancelAsync().ConfigureAwait(false);
                    }
                }
            });
        }

        // Act
        await Task.WhenAll(producers).ConfigureAwait(false);
        await Task.WhenAll(consumers).ConfigureAwait(false);

        // Assert — exactly-once: count matches and the distinct set is the full range.
        await Assert.That(receivedCount).IsEqualTo(totalItems);
        var distinct = new HashSet<int>(received);
        await Assert.That(distinct.Count).IsEqualTo(totalItems);
        var expected = new HashSet<int>(Enumerable.Range(0, totalItems));
        await Assert.That(distinct.SetEquals(expected)).IsTrue();
    }

    /// <summary>
    /// Creates the queue binding under test.
    /// </summary>
    /// <param name="capacity">The bounded capacity of the queue.</param>
    /// <returns>A fresh, empty queue instance.</returns>
    protected abstract IWorkQueue<int> CreateQueue(int capacity);

    /// <summary>
    /// The canonical consume loop shared by all contract tests: await
    /// <see cref="IWorkQueue{T}.WaitToDequeueAsync(CancellationToken)"/>; on a successful
    /// wait attempt <see cref="IWorkQueue{T}.TryDequeue(out T)"/>; if the dequeue
    /// spuriously misses (permitted for relaxed bindings), loop back to the wait.
    /// </summary>
    /// <typeparam name="T">The item type of the queue.</typeparam>
    /// <param name="queue">The queue to consume from.</param>
    /// <param name="cancellationToken">Token whose cancellation signals shutdown.</param>
    /// <returns>
    /// <c>(true, item)</c> when an item was received; <c>(false, default)</c> when the
    /// wait completed false (shutdown via cancellation).
    /// </returns>
    protected static async Task<(bool Received, T Item)> ConsumeOneAsync<T>(
        IWorkQueue<T> queue,
        CancellationToken cancellationToken)
    {
        while (await queue.WaitToDequeueAsync(cancellationToken).ConfigureAwait(false))
        {
            if (queue.TryDequeue(out var item))
            {
                return (true, item);
            }

            // Spurious miss under a relaxed binding — loop back to the wait.
        }

        return (false, default!);
    }
}
