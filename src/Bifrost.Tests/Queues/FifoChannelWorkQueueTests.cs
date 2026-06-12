// =============================================================================
// <copyright file="FifoChannelWorkQueueTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;

using Bifrost.Queues;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Contract and binding-specific tests for <see cref="FifoChannelWorkQueue{T}"/>, the
/// default <see cref="IWorkQueue{T}"/> binding wrapping a bounded channel.
/// </summary>
/// <remarks>
/// <para>
/// Deriving from <see cref="WorkQueueContractTests"/> activates the full inherited
/// contract suite against this binding. The tests declared here cover behavior specific
/// to the FIFO channel binding: strict FIFO ordering (a property the priority bindings
/// deliberately do not have) and the concrete-only producer-wait members
/// <see cref="FifoChannelWorkQueue{T}.WriteAsync(T, CancellationToken)"/> and
/// <see cref="FifoChannelWorkQueue{T}.Complete"/>. Per DR-7 the asynchronous members
/// forward the channel's own <see cref="ValueTask"/>s, so the channel's native fault
/// semantics surface at this layer; the orchestrator owns the mapping to its
/// never-throwing enqueue surface (covered by the orchestrator enqueue-surface tests).
/// </para>
/// </remarks>
[InheritsTests]
public sealed class FifoChannelWorkQueueTests : WorkQueueContractTests
{
    /// <summary>
    /// Per-test ceiling for waits that are expected to complete promptly.
    /// </summary>
    private static readonly TimeSpan BindingWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Verifies the binding-specific FIFO guarantee: items dequeue in exactly the order
    /// they were enqueued. Priority bindings deliberately do NOT have this property.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FifoOrdering_DequeueOrder_MatchesEnqueueOrder()
    {
        // Arrange
        const int itemCount = SmallCapacity;
        var queue = new FifoChannelWorkQueue<int>(itemCount);
        for (var i = 0; i < itemCount; i++)
        {
            var enqueued = queue.TryEnqueue(i);
            await Assert.That(enqueued).IsTrue();
        }

        // Act + Assert — every dequeue yields the next item in enqueue order.
        for (var i = 0; i < itemCount; i++)
        {
            var dequeued = queue.TryDequeue(out var item);
            await Assert.That(dequeued).IsTrue();
            await Assert.That(item).IsEqualTo(i);
        }
    }

    /// <summary>
    /// Verifies the concrete-only asynchronous accept path: with the queue at capacity,
    /// <see cref="FifoChannelWorkQueue{T}.WriteAsync(T, CancellationToken)"/> waits for
    /// space (producer-wait semantics) and completes once a slot frees.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WriteAsync_WaitsForSpace_ThenCompletes()
    {
        // Arrange — fill to capacity so the async write must wait.
        var queue = new FifoChannelWorkQueue<int>(SmallCapacity);
        for (var i = 0; i < SmallCapacity; i++)
        {
            var filled = queue.TryEnqueue(i);
            await Assert.That(filled).IsTrue();
        }

        // Act — start the async write; it must remain pending while the queue is full.
        var pending = queue.WriteAsync(SmallCapacity, CancellationToken.None).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        await Assert.That(pending.IsCompleted).IsFalse();

        // Free one slot; the pending write must now complete successfully.
        var dequeued = queue.TryDequeue(out _);
        await Assert.That(dequeued).IsTrue();
        await pending.WaitAsync(BindingWaitTimeout).ConfigureAwait(false);

        // Assert
        await Assert.That(pending.IsCompletedSuccessfully).IsTrue();
    }

    /// <summary>
    /// Verifies that <see cref="FifoChannelWorkQueue{T}.WriteAsync(T, CancellationToken)"/>
    /// after <see cref="FifoChannelWorkQueue{T}.Complete"/> faults with the channel's
    /// native <see cref="ChannelClosedException"/> (DR-7 direct forward, no wrapper);
    /// the orchestrator's single producer-wait async layer owns the mapping to
    /// <c>Rejected(Shutdown)</c>, covered by the orchestrator enqueue-surface tests.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WriteAsync_AfterComplete_ThrowsChannelClosed()
    {
        // Arrange
        var queue = new FifoChannelWorkQueue<int>(SmallCapacity);
        queue.Complete();

        // Act + Assert — channel-native fault surfaces at the binding layer.
        await Assert.That(() => queue.WriteAsync(1, CancellationToken.None).AsTask())
            .Throws<ChannelClosedException>();
    }

    /// <summary>
    /// Verifies graceful drain after <see cref="FifoChannelWorkQueue{T}.Complete"/>:
    /// residual items remain dequeueable via <see cref="IWorkQueue{T}.TryDequeue(out T)"/>,
    /// and once the queue is empty,
    /// <see cref="IWorkQueue{T}.WaitToDequeueAsync(CancellationToken)"/> completes
    /// <c>false</c> without throwing.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Complete_DrainsRemainingItems_ThenWaitReturnsFalse()
    {
        // Arrange — two residual items, then complete the queue.
        var queue = new FifoChannelWorkQueue<int>(SmallCapacity);
        await Assert.That(queue.TryEnqueue(1)).IsTrue();
        await Assert.That(queue.TryEnqueue(2)).IsTrue();
        queue.Complete();

        // Act + Assert — residue drains in FIFO order.
        var firstDequeued = queue.TryDequeue(out var first);
        await Assert.That(firstDequeued).IsTrue();
        await Assert.That(first).IsEqualTo(1);
        var secondDequeued = queue.TryDequeue(out var second);
        await Assert.That(secondDequeued).IsTrue();
        await Assert.That(second).IsEqualTo(2);

        // Once empty and completed, the wait reports shutdown via false — never throws.
        var signaled = await queue.WaitToDequeueAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(BindingWaitTimeout)
            .ConfigureAwait(false);
        await Assert.That(signaled).IsFalse();
    }

    /// <inheritdoc/>
    protected override IWorkQueue<int> CreateQueue(int capacity) =>
        new FifoChannelWorkQueue<int>(capacity);
}
