// =============================================================================
// <copyright file="BestObservedContractTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>Documents observable relaxed inspection behavior under contention.</summary>
public class BestObservedContractTests
{
    /// <summary>An unknown published top may hide the true minimum for the entire call.</summary>
    [Test]
    public async Task UnknownStripeIsSkippedWithoutClaimingExactMinimum()
    {
        var queue = ConcurrentPriorityQueue<int, int>.Create(new() { SubQueueCount = 2 });
        queue.SubQueuesForTest[0].TryLockedPush(1, 1);
        queue.SubQueuesForTest[1].TryLockedPush(10, 10);
        queue.SubQueuesForTest[0].DebugForceOddVersionForTest();
        await Assert.That(queue.TryPeekBestObserved(out _, out int peek)).IsTrue();
        await Assert.That(peek).IsEqualTo(10);
        await Assert.That(queue.TryDequeueBestObserved(out _, out int removed)).IsTrue();
        await Assert.That(removed).IsEqualTo(10);
        await Assert.That(queue.ToArray().Single().Priority).IsEqualTo(1);
    }

    /// <summary>Bounded inspection retries can fail while a populated stripe is locked.</summary>
    [Test]
    public async Task ContendedInspectionCanReturnFalseOnNonemptyQueue()
    {
        var queue = ConcurrentPriorityQueue<int, int>.Create(new() { SubQueueCount = 1 });
        queue.Enqueue(1, 1);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            lock (queue.SubQueuesForTest[0].SyncLock)
            {
                held.Set();
                release.Wait();
            }
        });
        try
        {
            if (!held.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            await Assert.That(queue.Count).IsEqualTo(1);
            await Assert.That(queue.TryPeekBestObserved(out _, out _)).IsFalse();
            await Assert.That(queue.TryDequeueBestObserved(out _, out _)).IsFalse();
        }
        finally
        {
            release.Set();
            await writer.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }
}
