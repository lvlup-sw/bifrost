// =============================================================================
// <copyright file="KeyedWorkQueueLifecycleTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Single-threaded binding tests for the lifecycle half of <see cref="KeyedWorkQueue{TWork, TKey}"/>:
/// the abandonment sweep (<see cref="KeyedWorkQueue{TWork, TKey}.DiscardQueued"/>), the backlog
/// snapshot, and surplus wake permits.
/// </summary>
/// <remarks>
/// Work items are strings of the form <c>key:n</c>, and the selector returns the part before the
/// colon. Every test runs on one thread, so the counts are exact.
/// </remarks>
public sealed class KeyedWorkQueueLifecycleTests
{
    /// <summary>The sweep runs only after completion, so it cannot race new admissions.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DiscardQueued_BeforeComplete_Throws()
    {
        using var queue = Create(maxConcurrencyPerKey: 1);
        Admit(queue, "A:1");

        await Assert.That(() => queue.DiscardQueued(new List<WorkEnvelope<string>>())).Throws<InvalidOperationException>();
        await Assert.That(queue.Count).IsEqualTo(1);
    }

    /// <summary>
    /// The sweep removes queued items, blocked items included, in per-key FIFO order. A key that
    /// still runs an item keeps its slot until that item's own release, which then removes the key.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DiscardQueued_RemovesQueuedAndBlockedItems_KeepsActiveSlots()
    {
        using var queue = Create(maxConcurrencyPerKey: 1);
        Admit(queue, "A:1", "A:2", "A:3", "B:1");
        Dequeue(queue, out var leaseA);
        queue.Complete();

        var discarded = new List<WorkEnvelope<string>>();
        var removed = queue.DiscardQueued(discarded);

        await Assert.That(removed).IsEqualTo(3);
        var keyA = discarded.Select(e => e.Work).Where(w => w.StartsWith("A:", StringComparison.Ordinal));
        await Assert.That(string.Join(",", keyA)).IsEqualTo("A:2,A:3");
        await Assert.That(discarded.Select(e => e.Work)).Contains("B:1");
        await Assert.That(queue.Count).IsEqualTo(0);
        await Assert.That(queue.DispatchableCount).IsEqualTo(0);
        await Assert.That(queue.ReadyKeyCountForTest).IsEqualTo(0);
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(1);
        await Assert.That(queue.ActiveCountForTest("A")).IsEqualTo(1);

        queue.Release(leaseA!);

        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(0);
        await Assert.That(() => queue.Release(leaseA!)).Throws<InvalidOperationException>();
    }

    /// <summary>A second sweep finds nothing, and a sweep of an empty queue adds nothing.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DiscardQueued_IsIdempotent()
    {
        using var queue = Create(maxConcurrencyPerKey: 2);
        Admit(queue, "A:1", "B:1");
        queue.Complete();
        var discarded = new List<WorkEnvelope<string>>();

        await Assert.That(queue.DiscardQueued(discarded)).IsEqualTo(2);
        await Assert.That(queue.DiscardQueued(discarded)).IsEqualTo(0);
        await Assert.That(discarded.Count).IsEqualTo(2);
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(0);
    }

    /// <summary>
    /// A waiter parked after completion behind a blocked-only backlog wakes and sees the end of the
    /// queue once the sweep empties it.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DiscardQueued_EmptiesCompletedQueue_WakesParkedWaiter()
    {
        using var queue = Create(maxConcurrencyPerKey: 1);
        Admit(queue, "A:1", "A:2");
        WaitAndDequeue(queue, "A:1", out var leaseA);
        queue.Complete();

        var wait = queue.WaitToDequeueAsync(CancellationToken.None).AsTask();
        await Assert.That(wait.IsCompleted).IsFalse();

        queue.DiscardQueued(new List<WorkEnvelope<string>>());

        await Assert.That(await wait.WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        queue.Release(leaseA);
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(0);
    }

    /// <summary>
    /// After the sweep, a release of a running item funds no wake and the matched-wake take is a
    /// no-op, so nothing can wake for a removed item.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ReleaseAfterSweep_FundsNoWake()
    {
        using var queue = Create(maxConcurrencyPerKey: 1);
        Admit(queue, "A:1", "A:2");
        WaitAndDequeue(queue, "A:1", out var leaseA);
        queue.Complete();
        queue.DiscardQueued(new List<WorkEnvelope<string>>());
        var permitsBefore = queue.PendingWakeCountForTest;

        queue.Release(leaseA);
        queue.ConsumeMatchedWake();

        await Assert.That(queue.PendingWakeCountForTest).IsEqualTo(permitsBefore);
        await Assert.That(await queue.WaitToDequeueAsync(CancellationToken.None)).IsFalse();
    }

    /// <summary>
    /// The backlog snapshot splits pending items into ready and blocked items, and it tracks the
    /// dispatchable count through dequeue, release, and sweep.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Backlog_SplitsPendingIntoReadyAndBlocked()
    {
        using var queue = Create(maxConcurrencyPerKey: 2);
        Admit(queue, "A:1", "A:2", "A:3", "A:4", "B:1");

        await Assert.That(queue.Backlog).IsEqualTo(new DispatchBacklog(5, 3));
        await Assert.That(queue.Backlog.Blocked).IsEqualTo(2);

        Dequeue(queue, out var leaseA);
        Dequeue(queue, out _);
        Dequeue(queue, out _);

        await Assert.That(queue.Backlog).IsEqualTo(new DispatchBacklog(2, 0));
        await Assert.That(queue.Backlog.Blocked).IsEqualTo(2);

        queue.Release(leaseA!);

        await Assert.That(queue.Backlog).IsEqualTo(new DispatchBacklog(2, 1));

        queue.Complete();
        queue.DiscardQueued(new List<WorkEnvelope<string>>());

        await Assert.That(queue.Backlog).IsEqualTo(new DispatchBacklog(0, 0));
    }

    /// <summary>
    /// Surplus permits from repeated retirement hand-offs end in dequeue misses. They never take a
    /// blocked item or reserve a slot, and the next real dispatch still works.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SurplusHandOffPermits_EndInMisses_WithoutDispatchingBlockedItems()
    {
        using var queue = Create(maxConcurrencyPerKey: 1);
        Admit(queue, "A:1", "A:2");
        WaitAndDequeue(queue, "A:1", out var leaseA);
        for (var i = 0; i < 3; i++) queue.WakeConsumer();

        for (var i = 0; i < 3; i++)
        {
            await Assert.That(await queue.WaitToDequeueAsync(CancellationToken.None)).IsTrue();
            await Assert.That(queue.TryDequeue(out _, out var lease)).IsFalse();
            await Assert.That(lease).IsNull();
        }

        await Assert.That(queue.ActiveCountForTest("A")).IsEqualTo(1);
        await Assert.That(queue.Backlog).IsEqualTo(new DispatchBacklog(1, 0));

        queue.Release(leaseA);
        WaitAndDequeue(queue, "A:2", out var leaseA2);
        queue.Release(leaseA2);

        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(0);
    }

    private static string KeyOf(string work) => work.Split(':')[0];

    private static KeyedWorkQueue<string, string> Create(int maxConcurrencyPerKey)
        => KeyedWorkQueue.Create<string, string>(KeyOf, 100, 100, maxConcurrencyPerKey);

    private static void Admit(KeyedWorkQueue<string, string> queue, params string[] work)
    {
        foreach (var item in work)
        {
            if (!queue.Enqueue(new WorkEnvelope<string>(item, WorkClass.Default, 0)).IsAccepted)
                throw new InvalidOperationException($"Setup admission of {item} was rejected.");
        }
    }

    private static string Dequeue(KeyedWorkQueue<string, string> queue, out object? lease)
    {
        if (!queue.TryDequeue(out var item, out lease))
            throw new InvalidOperationException("Expected a dispatchable item.");
        return item.Work;
    }

    private static void WaitAndDequeue(KeyedWorkQueue<string, string> queue, string expected, out object lease)
    {
        if (queue.WaitToDequeueAsync(CancellationToken.None).AsTask() is not { IsCompletedSuccessfully: true, Result: true })
            throw new InvalidOperationException("Expected a funded wait.");
        var work = Dequeue(queue, out var taken);
        if (work != expected) throw new InvalidOperationException($"Expected {expected} but dequeued {work}.");
        lease = taken!;
    }
}
