// =============================================================================
// <copyright file="KeyedWorkQueueTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Single-threaded binding tests for <see cref="KeyedWorkQueue{TWork, TKey}"/>: per-key FIFO,
/// round robin, reservation at dequeue, admission order, wake accounting, release, and completion.
/// </summary>
/// <remarks>
/// Work items are strings of the form <c>key:n</c>, and the selector returns the part before the
/// colon. Every test runs on one thread, so the permit counts are exact.
/// </remarks>
public sealed class KeyedWorkQueueTests
{
    /// <summary>The factory rejects a null selector and limits below one.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Create_InvalidArguments_Throw()
    {
        await Assert.That(() => KeyedWorkQueue.Create<string, string>(null!, 1, 1, 1)).Throws<ArgumentNullException>();
        await Assert.That(() => KeyedWorkQueue.Create<string, string>(KeyOf, 0, 1, 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => KeyedWorkQueue.Create<string, string>(KeyOf, 1, 0, 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => KeyedWorkQueue.Create<string, string>(KeyOf, 1, 1, 0)).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>With one slot per key, a key's second item waits for the release of its first.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ConcurrencyOne_SecondItemOfKey_WaitsForRelease()
    {
        using var queue = Create(maxConcurrencyPerKey: 1);
        Admit(queue, "A:1", "A:2", "B:1");

        await Assert.That(Dequeue(queue, out var leaseA1)).IsEqualTo("A:1");
        await Assert.That(Dequeue(queue, out _)).IsEqualTo("B:1");
        await Assert.That(queue.TryDequeue(out _, out var missLease)).IsFalse();
        await Assert.That(missLease).IsNull();

        queue.Release(leaseA1!);

        await Assert.That(Dequeue(queue, out _)).IsEqualTo("A:2");
        await Assert.That(queue.Count).IsEqualTo(0);
    }

    /// <summary>Ready keys take turns, and each key keeps its own FIFO order.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ReadyKeys_RotateRoundRobin_WithPerKeyFifo()
    {
        using var queue = Create(maxConcurrencyPerKey: 3);
        Admit(queue, "A:1", "A:2", "A:3", "B:1", "B:2", "C:1");

        var order = new List<string>();
        while (queue.TryDequeue(out var item, out _)) order.Add(item.Work);

        await Assert.That(string.Join(",", order)).IsEqualTo("A:1,B:1,C:1,A:2,B:2,A:3");
    }

    /// <summary>The lease of a dequeued item is the key's state, and the same key always returns the same lease.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Lease_IsTheKeyState_SharedByItemsOfOneKey()
    {
        using var queue = Create(maxConcurrencyPerKey: 2);
        Admit(queue, "A:1", "A:2");

        Dequeue(queue, out var first);
        Dequeue(queue, out var second);

        await Assert.That(first).IsNotNull();
        await Assert.That(first).IsSameReferenceAs(second);
        await Assert.That(queue.ActiveCountForTest("A")).IsEqualTo(2);
    }

    /// <summary>Running items do not count as pending against the per-key limit.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task PerKeyLimit_CountsQueuedItemsOnly()
    {
        using var queue = Create(capacity: 10, maxPendingPerKey: 1, maxConcurrencyPerKey: 1);
        await Assert.That(Enqueue(queue, "A:1")).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(Enqueue(queue, "A:2").Reason).IsEqualTo(RejectionReason.KeyLimitExceeded);

        Dequeue(queue, out _);

        await Assert.That(Enqueue(queue, "A:2")).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(Enqueue(queue, "A:3").Reason).IsEqualTo(RejectionReason.KeyLimitExceeded);
        await Assert.That(Enqueue(queue, "B:1")).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(queue.Count).IsEqualTo(2);
    }

    /// <summary>The global limit counts queued items only and wins over the per-key limit.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GlobalLimit_TakesPrecedenceOverPerKeyLimit()
    {
        using var queue = Create(capacity: 2, maxPendingPerKey: 1, maxConcurrencyPerKey: 1);
        Admit(queue, "A:1", "B:1");

        await Assert.That(Enqueue(queue, "A:2").Reason).IsEqualTo(RejectionReason.CapacityExceeded);
        await Assert.That(Enqueue(queue, "C:1").Reason).IsEqualTo(RejectionReason.CapacityExceeded);

        Dequeue(queue, out _);

        await Assert.That(Enqueue(queue, "C:1")).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(queue.Count).IsEqualTo(2);
    }

    /// <summary>Shutdown is checked before both limits.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Shutdown_TakesPrecedenceOverLimits()
    {
        using var queue = Create(capacity: 1, maxPendingPerKey: 1);
        Admit(queue, "A:1");
        queue.Complete();

        await Assert.That(Enqueue(queue, "A:2").Reason).IsEqualTo(RejectionReason.Shutdown);
        await Assert.That(Enqueue(queue, "B:1").Reason).IsEqualTo(RejectionReason.Shutdown);
        await Assert.That(queue.TryEnqueue(Envelope("B:1"))).IsFalse();
    }

    /// <summary>A rejected admission leaves no state and funds no wake.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Rejection_LeavesNoStateAndNoWake()
    {
        using var queue = Create(capacity: 1, maxPendingPerKey: 1);
        Admit(queue, "A:1");
        var permits = queue.PendingWakeCountForTest;

        Enqueue(queue, "B:1");
        Enqueue(queue, "A:2");

        await Assert.That(queue.PendingWakeCountForTest).IsEqualTo(permits);
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(1);
        await Assert.That(queue.Count).IsEqualTo(1);
    }

    /// <summary>The selector runs once per admission attempt, including rejected attempts.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Selector_RunsOncePerAdmission()
    {
        var calls = 0;
        using var queue = KeyedWorkQueue.Create<string, string>(
            work =>
            {
                calls++;
                return KeyOf(work);
            },
            capacity: 10,
            maxPendingPerKey: 1,
            maxConcurrencyPerKey: 1);

        Enqueue(queue, "A:1");
        Enqueue(queue, "A:2");
        Dequeue(queue, out var lease);
        queue.Release(lease!);

        await Assert.That(calls).IsEqualTo(2);
    }

    /// <summary>A null key throws <see cref="ArgumentException"/> and changes nothing.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task NullKey_ThrowsArgumentException()
    {
        using var queue = KeyedWorkQueue.Create<string, string>(static _ => null!, 10, 10, 1);

        await Assert.That(() => queue.Enqueue(Envelope("x"))).Throws<ArgumentException>();
        await Assert.That(queue.Count).IsEqualTo(0);
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(0);
    }

    /// <summary>A selector exception propagates to the caller and changes nothing.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SelectorException_Propagates()
    {
        using var queue = KeyedWorkQueue.Create<string, string>(
            static _ => throw new FormatException("bad key"), 10, 10, 1);

        await Assert.That(() => queue.Enqueue(Envelope("x"))).Throws<FormatException>();
        await Assert.That(queue.Count).IsEqualTo(0);
        await Assert.That(queue.PendingWakeCountForTest).IsEqualTo(0);
    }

    /// <summary>The key comparer decides which items share a key.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Comparer_GroupsKeys()
    {
        using var queue = KeyedWorkQueue.Create<string, string>(
            KeyOf, 10, 10, 1, StringComparer.OrdinalIgnoreCase);
        Admit(queue, "a:1", "A:2");

        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(1);
        await Assert.That(Dequeue(queue, out _)).IsEqualTo("a:1");
        await Assert.That(queue.TryDequeue(out _, out _)).IsFalse();
    }

    /// <summary>Admission funds a wake only when the item can run at once.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Admission_FundsWakeOnlyForDispatchableItems()
    {
        using var queue = Create(maxConcurrencyPerKey: 2);

        Admit(queue, "A:1", "A:2");
        await Assert.That(queue.PendingWakeCountForTest).IsEqualTo(2);

        Admit(queue, "A:3");
        await Assert.That(queue.PendingWakeCountForTest).IsEqualTo(2);
        await Assert.That(queue.DispatchableCount).IsEqualTo(2);

        Admit(queue, "B:1");
        await Assert.That(queue.PendingWakeCountForTest).IsEqualTo(3);
        await Assert.That(queue.DispatchableCount).IsEqualTo(3);
        await Assert.That(queue.Count).IsEqualTo(4);
    }

    /// <summary>A release funds a wake only when it frees a slot that a queued item waits for.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Release_FundsWakeOnlyWhenItUnblocksAnItem()
    {
        using var queue = Create(maxConcurrencyPerKey: 1);
        Admit(queue, "A:1", "A:2");
        WaitAndDequeue(queue, "A:1", out var lease);
        await Assert.That(queue.PendingWakeCountForTest).IsEqualTo(0);
        await Assert.That(queue.ReadyKeyCountForTest).IsEqualTo(0);

        queue.Release(lease);

        await Assert.That(queue.PendingWakeCountForTest).IsEqualTo(1);
        await Assert.That(queue.ReadyKeyCountForTest).IsEqualTo(1);
        WaitAndDequeue(queue, "A:2", out lease);

        queue.Release(lease);

        await Assert.That(queue.PendingWakeCountForTest).IsEqualTo(0);
        await Assert.That(queue.DispatchableCount).IsEqualTo(0);
    }

    /// <summary>A key with no queued and no running items is removed.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task IdleKey_IsRemoved()
    {
        using var queue = Create(maxConcurrencyPerKey: 2);
        Admit(queue, "A:1", "A:2", "B:1");
        Dequeue(queue, out var a1);
        Dequeue(queue, out var b1);
        Dequeue(queue, out var a2);
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(2);

        queue.Release(b1!);
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(1);

        queue.Release(a1!);
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(1);

        queue.Release(a2!);
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(0);
        await Assert.That(queue.ReadyKeyCountForTest).IsEqualTo(0);
    }

    /// <summary>A released key that was removed starts fresh on its next admission.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RemovedKey_StartsFreshOnReadmission()
    {
        using var queue = Create(maxConcurrencyPerKey: 1);
        Admit(queue, "A:1");
        Dequeue(queue, out var old);
        queue.Release(old!);

        Admit(queue, "A:2");
        Dequeue(queue, out var fresh);

        await Assert.That(fresh).IsNotSameReferenceAs(old);
        await Assert.That(() => queue.Release(old!)).Throws<InvalidOperationException>();
        queue.Release(fresh!);
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(0);
    }

    /// <summary>A second release of the same lease throws.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DoubleRelease_Throws()
    {
        using var queue = Create(maxConcurrencyPerKey: 1);
        Admit(queue, "A:1", "A:2");
        Dequeue(queue, out var lease);
        queue.Release(lease!);

        await Assert.That(() => queue.Release(lease!)).Throws<InvalidOperationException>();
        await Assert.That(queue.ActiveCountForTest("A")).IsEqualTo(0);
        await Assert.That(queue.PendingWakeCountForTest).IsEqualTo(2);
    }

    /// <summary>
    /// With more than one slot per key, a duplicate release of one dispatch while another item of
    /// the key still runs is not detected and frees the other item's slot.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// This pins the current shared-lease behavior. A per-dispatch lease (#131) makes the second
    /// release throw, and this test then changes.
    /// </remarks>
    [Test]
    public async Task DuplicateRelease_WithSharedLease_IsNotDetected()
    {
        using var queue = Create(maxConcurrencyPerKey: 2);
        Admit(queue, "A:1", "A:2");
        Dequeue(queue, out var first);
        Dequeue(queue, out _);
        queue.Release(first!);

        queue.Release(first!);

        await Assert.That(queue.ActiveCountForTest("A")).IsEqualTo(0);
        await Assert.That(() => queue.Release(first!)).Throws<InvalidOperationException>();
    }

    /// <summary>A lease from another queue, or an object that is not a lease, is refused.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ForeignLease_Throws()
    {
        using var queue = Create();
        using var other = Create();
        Admit(queue, "A:1");
        Admit(other, "A:1");
        Dequeue(other, out var foreign);
        Dequeue(queue, out var own);

        await Assert.That(() => queue.Release(foreign!)).Throws<InvalidOperationException>();
        await Assert.That(() => queue.Release(new object())).Throws<ArgumentException>();
        queue.Release(own!);
        other.Release(foreign!);
    }

    /// <summary>The plain dequeue throws, because it would reserve a slot without a lease.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task PlainTryDequeue_Throws()
    {
        using var queue = Create();
        Admit(queue, "A:1");
        IWorkQueue<WorkEnvelope<string>> plain = queue;

        await Assert.That(() => plain.TryDequeue(out _)).Throws<InvalidOperationException>();
        await Assert.That(queue.Count).IsEqualTo(1);
    }

    /// <summary>Completion of an empty queue ends a wait at once and wakes a parked one.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Complete_EmptyQueue_EndsWaits()
    {
        using var queue = Create();
        var parked = queue.WaitToDequeueAsync(CancellationToken.None).AsTask();
        await Assert.That(parked.IsCompleted).IsFalse();

        queue.Complete();
        queue.Complete();

        await Assert.That(await parked.WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await Assert.That(await queue.WaitToDequeueAsync(CancellationToken.None)).IsFalse();
    }

    /// <summary>
    /// After completion a wait parks while only blocked items remain, a release wakes it, and the
    /// dequeue of the last item ends the remaining waits.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Complete_WithBlockedBacklog_ParksUntilReleaseThenEnds()
    {
        using var queue = Create(maxConcurrencyPerKey: 1);
        Admit(queue, "A:1", "A:2");
        WaitAndDequeue(queue, "A:1", out var lease);
        queue.Complete();

        var first = queue.WaitToDequeueAsync(CancellationToken.None).AsTask();
        await Assert.That(first.IsCompleted).IsFalse();

        queue.Release(lease);
        await Assert.That(await first.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();

        var second = queue.WaitToDequeueAsync(CancellationToken.None).AsTask();
        await Assert.That(second.IsCompleted).IsFalse();

        await Assert.That(Dequeue(queue, out lease)).IsEqualTo("A:2");

        await Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await Assert.That(await queue.WaitToDequeueAsync(CancellationToken.None)).IsFalse();
        queue.Release(lease!);
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(0);
    }

    /// <summary>The matched-wake take consumes a permit before completion and none once completed and empty.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ConsumeMatchedWake_SkipsOnceCompletedAndEmpty()
    {
        using var queue = Create(maxConcurrencyPerKey: 1);
        Admit(queue, "A:1", "B:1");
        Dequeue(queue, out _);
        queue.ConsumeMatchedWake();
        await Assert.That(queue.PendingWakeCountForTest).IsEqualTo(1);

        queue.Complete();
        Dequeue(queue, out _);
        queue.ConsumeMatchedWake();

        await Assert.That(queue.PendingWakeCountForTest).IsEqualTo(1);
    }

    /// <summary>A surplus wake from the retirement hand-off ends in a dequeue miss.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WakeConsumer_SurplusPermit_EndsInMiss()
    {
        using var queue = Create();
        queue.WakeConsumer();

        await Assert.That(await queue.WaitToDequeueAsync(CancellationToken.None)).IsTrue();
        await Assert.That(queue.TryDequeue(out _, out _)).IsFalse();
        await Assert.That(queue.PendingWakeCountForTest).IsEqualTo(0);
    }

    /// <summary>A cancelled wait throws <see cref="OperationCanceledException"/>.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Wait_Cancelled_Throws()
    {
        using var queue = Create();
        using var cts = new CancellationTokenSource();
        var parked = queue.WaitToDequeueAsync(cts.Token).AsTask();
        await cts.CancelAsync();

        await Assert.That(await SurfacesCancellationAsync(parked)).IsTrue();
        await Assert.That(await SurfacesCancellationAsync(queue.WaitToDequeueAsync(cts.Token).AsTask())).IsTrue();
    }

    /// <summary>Dispose is idempotent.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Dispose_IsIdempotent()
    {
        var queue = Create();
        queue.Dispose();
        queue.Dispose();

        await Assert.That(queue.IsDisposedForTest).IsTrue();
    }

    private static string KeyOf(string work) => work.Split(':')[0];

    private static async Task<bool> SurfacesCancellationAsync(Task<bool> wait)
    {
        try
        {
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private static KeyedWorkQueue<string, string> Create(int capacity = 100, int maxPendingPerKey = 100, int maxConcurrencyPerKey = 1)
        => KeyedWorkQueue.Create<string, string>(KeyOf, capacity, maxPendingPerKey, maxConcurrencyPerKey);

    private static WorkEnvelope<string> Envelope(string work) => new(work, WorkClass.Default, 0);

    private static EnqueueResult Enqueue(KeyedWorkQueue<string, string> queue, string work) => queue.Enqueue(Envelope(work));

    private static void Admit(KeyedWorkQueue<string, string> queue, params string[] work)
    {
        foreach (var item in work)
        {
            if (!queue.Enqueue(Envelope(item)).IsAccepted)
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
