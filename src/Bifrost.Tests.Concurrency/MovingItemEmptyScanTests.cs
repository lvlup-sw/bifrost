// =============================================================================
// <copyright file="MovingItemEmptyScanTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// A dequeue scan must not report empty when an item moves from an unscanned stripe
/// into a stripe the scan already passed.
/// </summary>
public sealed class MovingItemEmptyScanTests
{
    /// <summary>
    /// Moves the only item behind the scan and requires the dequeue to return it.
    /// </summary>
    [Test]
    public async Task TryDequeue_ItemMovesBehindScan_DoesNotReportEmpty()
    {
        var queue = ConcurrentPriorityQueue<int, int>.Create(new() { SubQueueCount = 2, BufferCapacity = 0 });
        await Assert.That(queue.SubQueuesForTest[1].TryLockedPush(41, 1)).IsTrue();

        using var atLaterStripe = new ManualResetEventSlim(false);
        using var lockReleased = new ManualResetEventSlim(false);
        var moved = 0;
        queue.DebugBeforeVerificationIndex = index =>
        {
            if (index != 1 || Interlocked.Exchange(ref moved, 1) != 0)
            {
                return;
            }

            atLaterStripe.Set();
            if (!lockReleased.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The stripe lock was not released.");
            }

            var status = queue.SubQueuesForTest[1].TryLockedPop(out var element, out var priority);
            if (status != SubQueuePopStatus.Success || element != 41)
            {
                throw new InvalidOperationException($"Expected to move item 41, got {status} {element}.");
            }

            if (!queue.SubQueuesForTest[0].TryLockedPush(element, priority))
            {
                throw new InvalidOperationException("The item could not be pushed behind the scan.");
            }
        };

        using var held = new ManualResetEventSlim(false);
        var holder = Task.Factory.StartNew(
            () =>
            {
                lock (queue.SubQueuesForTest[1].SyncLock)
                {
                    held.Set();
                    if (!atLaterStripe.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("The scan did not reach the later stripe.");
                    }
                }

                lockReleased.Set();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            if (!held.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The populated stripe was not locked.");
            }

            var consumer = Task.Factory.StartNew(
                () =>
                {
                    var found = queue.TryDequeue(out var id, out var priority);
                    return (found, id, priority);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            var result = await consumer.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Assert.That(result.found).IsTrue();
            await Assert.That(result.id).IsEqualTo(41);
            await Assert.That(result.priority).IsEqualTo(1);
        }
        finally
        {
            atLaterStripe.Set();
            lockReleased.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }
}
