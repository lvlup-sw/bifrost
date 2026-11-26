// =============================================================================
// <copyright file="ObservedKeyedQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Tests.Orchestrator;

/// <summary>
/// Wraps a <see cref="KeyedWorkQueue{TWork, TKey}"/> and records what the orchestrator does with it:
/// leased dequeues by item, releases, swept items, dequeue misses, and waits.
/// </summary>
/// <remarks>
/// Every call is forwarded unchanged, so the orchestrator sees the real binding's behavior. The
/// lease is the key state and is shared by the items of one key, so releases are counted, not
/// matched to items. A release the binding rejects is counted as a failed release and rethrown.
/// <para>
/// Tests wait on checkpoints instead of polling: <see cref="WaitCallsReachedAsync"/>,
/// <see cref="ReleasesReachedAsync"/>, and <see cref="WakesConsumedAsync"/> return tasks that
/// complete when their condition holds. Every forwarded call that can change a checked count or
/// the binding's wake permits re-checks the open checkpoints after the binding returns, under one
/// lock, so a checkpoint cannot miss the call that satisfies it. A wait is counted after the
/// binding has registered it, so a wait-count checkpoint means the consumer reached the binding's
/// semaphore. The returned tasks never time out; callers bound them with <c>WaitAsync</c>.
/// </para>
/// </remarks>
/// <param name="inner">The wrapped keyed queue.</param>
internal sealed class ObservedKeyedQueue(KeyedWorkQueue<string, string> inner)
    : IWorkQueue<WorkEnvelope<string>>, ILeasedWorkQueue<WorkEnvelope<string>>, IWorkQueueLifecycle, IDispatchableBacklog,
        IDisposable
{
    private readonly ConcurrentQueue<string> _dequeued = new();
    private readonly ConcurrentQueue<string> _discarded = new();
    private readonly Lock _checkpointGate = new();
    private readonly List<(Func<bool> Condition, TaskCompletionSource Signal)> _checkpoints = [];
    private int _releases;
    private int _failedReleases;
    private int _misses;
    private int _waitCalls;
    private int _discardCalls;

    /// <summary>Gets the wrapped queue.</summary>
    public KeyedWorkQueue<string, string> Inner => inner;

    /// <summary>Gets the items handed out by leased dequeues, in dequeue order.</summary>
    public string[] Dequeued => [.. _dequeued];

    /// <summary>Gets the items removed by the abandonment sweep.</summary>
    public string[] Discarded => [.. _discarded];

    /// <summary>Gets the number of successful releases.</summary>
    public int Releases => Volatile.Read(ref _releases);

    /// <summary>Gets the number of releases the binding rejected.</summary>
    public int FailedReleases => Volatile.Read(ref _failedReleases);

    /// <summary>Gets the number of leased dequeues that found nothing.</summary>
    public int Misses => Volatile.Read(ref _misses);

    /// <summary>Gets the number of waits the consumers started.</summary>
    public int WaitCalls => Volatile.Read(ref _waitCalls);

    /// <summary>Gets the number of sweeps that reached the binding.</summary>
    public int DiscardCalls => Volatile.Read(ref _discardCalls);

    /// <inheritdoc/>
    public int Count => inner.Count;

    /// <summary>Completes once at least <paramref name="count"/> waits have reached the binding.</summary>
    /// <param name="count">The wait count to reach.</param>
    /// <returns>A task that completes at the checkpoint.</returns>
    public Task WaitCallsReachedAsync(int count) => CheckpointAsync(() => WaitCalls >= count);

    /// <summary>Completes once at least <paramref name="count"/> releases have succeeded.</summary>
    /// <param name="count">The release count to reach.</param>
    /// <returns>A task that completes at the checkpoint.</returns>
    public Task ReleasesReachedAsync(int count) => CheckpointAsync(() => Releases >= count);

    /// <summary>Completes once the binding holds no pending wake permit.</summary>
    /// <returns>A task that completes at the checkpoint.</returns>
    public Task WakesConsumedAsync() => CheckpointAsync(() => inner.PendingWakeCountForTest == 0);

    /// <inheritdoc/>
    public int DispatchableCount => inner.DispatchableCount;

    /// <inheritdoc/>
    public DispatchBacklog Backlog => inner.Backlog;

    /// <inheritdoc/>
    public EnqueueResult Enqueue(in WorkEnvelope<string> item)
    {
        var result = inner.Enqueue(item);
        ReachCheckpoints();
        return result;
    }

    /// <inheritdoc/>
    public bool TryEnqueue(in WorkEnvelope<string> item)
    {
        var accepted = inner.TryEnqueue(item);
        ReachCheckpoints();
        return accepted;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The binding's wait is an async method that registers on its semaphore before it returns, so
    /// the wait is counted after that registration, and a permit it took at once is already gone
    /// when the checkpoints are re-checked.
    /// </remarks>
    public ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken)
    {
        var wait = inner.WaitToDequeueAsync(cancellationToken);
        Interlocked.Increment(ref _waitCalls);
        ReachCheckpoints();
        return wait;
    }

    /// <inheritdoc/>
    public bool TryDequeue([MaybeNullWhen(false)] out WorkEnvelope<string> item)
        => ((IWorkQueue<WorkEnvelope<string>>)inner).TryDequeue(out item);

    /// <inheritdoc/>
    public bool TryDequeue([MaybeNullWhen(false)] out WorkEnvelope<string> item, out object? lease)
    {
        if (!inner.TryDequeue(out item, out lease))
        {
            Interlocked.Increment(ref _misses);
            ReachCheckpoints();
            return false;
        }

        _dequeued.Enqueue(item.Work);
        ReachCheckpoints();
        return true;
    }

    /// <inheritdoc/>
    public void Release(object lease)
    {
        try
        {
            inner.Release(lease);
            Interlocked.Increment(ref _releases);
        }
        catch
        {
            Interlocked.Increment(ref _failedReleases);
            throw;
        }
        finally
        {
            ReachCheckpoints();
        }
    }

    /// <inheritdoc/>
    public int DiscardQueued(ICollection<WorkEnvelope<string>> discarded)
    {
        Interlocked.Increment(ref _discardCalls);
        var start = discarded.Count;
        var removed = inner.DiscardQueued(discarded);
        foreach (var item in discarded.Skip(start)) _discarded.Enqueue(item.Work);
        ReachCheckpoints();
        return removed;
    }

    /// <inheritdoc/>
    public void Complete()
    {
        inner.Complete();
        ReachCheckpoints();
    }

    /// <inheritdoc/>
    public void WakeConsumer()
    {
        inner.WakeConsumer();
        ReachCheckpoints();
    }

    /// <inheritdoc/>
    public void ConsumeMatchedWake()
    {
        inner.ConsumeMatchedWake();
        ReachCheckpoints();
    }

    /// <inheritdoc/>
    public void Dispose() => inner.Dispose();

    /// <summary>Returns a task that completes once <paramref name="condition"/> holds.</summary>
    /// <param name="condition">The checkpoint condition, read under the checkpoint lock.</param>
    /// <returns>A completed task if the condition holds now; otherwise a task that a later call completes.</returns>
    private Task CheckpointAsync(Func<bool> condition)
    {
        lock (_checkpointGate)
        {
            if (condition()) return Task.CompletedTask;
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _checkpoints.Add((condition, signal));
            return signal.Task;
        }
    }

    /// <summary>Completes and removes every open checkpoint whose condition now holds.</summary>
    private void ReachCheckpoints()
    {
        lock (_checkpointGate)
        {
            if (_checkpoints.Count == 0) return;
            _checkpoints.RemoveAll(static checkpoint =>
            {
                if (!checkpoint.Condition()) return false;
                checkpoint.Signal.TrySetResult();
                return true;
            });
        }
    }
}
