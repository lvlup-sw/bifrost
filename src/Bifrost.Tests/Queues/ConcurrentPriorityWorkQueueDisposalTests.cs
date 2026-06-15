// =============================================================================
// <copyright file="ConcurrentPriorityWorkQueueDisposalTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Disposal tests for <c>ConcurrentPriorityWorkQueue&lt;TWork&gt;</c> (DR-3, issue #21):
/// the binding owns an item-counting <see cref="SemaphoreSlim"/> wake-up, which is an
/// <see cref="IDisposable"/> resource that must be released. Before DR-3 the type did
/// not implement <see cref="IDisposable"/> at all — leaking the semaphore (a
/// CA2213-class undisposed-field defect) — so these tests pin the new
/// <see cref="IDisposable"/> contract: <c>Dispose</c> disposes the semaphore (observed
/// via a subsequent wait surfacing <see cref="ObjectDisposedException"/>) and is
/// idempotent.
/// </summary>
public sealed class ConcurrentPriorityWorkQueueDisposalTests
{
    /// <summary>
    /// The timestamp frequency used by these tests: 1 000 units per second.
    /// </summary>
    private const long TestTimestampFrequency = 1_000;

    /// <summary>
    /// A small bounded capacity sufficient for the single-waiter disposal scenarios.
    /// </summary>
    private const int SmallCapacity = 8;

    /// <summary>
    /// Verifies that after <see cref="IDisposable.Dispose"/> the owned
    /// <see cref="SemaphoreSlim"/> is released: a fresh
    /// <c>WaitToDequeueAsync</c> on a non-completed, empty queue reaches
    /// <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/> and surfaces
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ConcurrentPriorityWorkQueue_Dispose_ThenWait_ThrowsObjectDisposedException()
    {
        // Arrange — a non-completed, empty queue: a wait would park on the semaphore.
        var queue = new ConcurrentPriorityWorkQueue<int>(
            SmallCapacity, new PriorityDispatchOptions(), TestTimestampFrequency);

        // Act — dispose, releasing the owned semaphore.
        queue.Dispose();

        // Assert — the parking wait now hits a disposed semaphore.
        await Assert.That(async () => await queue.WaitToDequeueAsync(default).ConfigureAwait(false))
            .Throws<ObjectDisposedException>();
    }

    /// <summary>
    /// Verifies that <see cref="IDisposable.Dispose"/> is idempotent: a second call
    /// after the first does not throw (the disposal is guarded so double-dispose of the
    /// underlying semaphore never reaches <see cref="SemaphoreSlim.Dispose()"/> twice).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ConcurrentPriorityWorkQueue_DisposeTwice_DoesNotThrow()
    {
        // Arrange
        var queue = new ConcurrentPriorityWorkQueue<int>(
            SmallCapacity, new PriorityDispatchOptions(), TestTimestampFrequency);

        // Act + Assert — both calls complete without throwing.
        await Assert.That(() =>
            {
                queue.Dispose();
                queue.Dispose();
            })
            .ThrowsNothing();
    }
}
