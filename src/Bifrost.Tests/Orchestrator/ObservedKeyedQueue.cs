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
/// </remarks>
/// <param name="inner">The wrapped keyed queue.</param>
internal sealed class ObservedKeyedQueue(KeyedWorkQueue<string, string> inner)
    : IWorkQueue<WorkEnvelope<string>>, ILeasedWorkQueue<WorkEnvelope<string>>, IWorkQueueLifecycle, IDispatchableBacklog,
        IDisposable
{
    private readonly ConcurrentQueue<string> _dequeued = new();
    private readonly ConcurrentQueue<string> _discarded = new();
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

    /// <inheritdoc/>
    public int DispatchableCount => inner.DispatchableCount;

    /// <inheritdoc/>
    public DispatchBacklog Backlog => inner.Backlog;

    /// <inheritdoc/>
    public EnqueueResult Enqueue(in WorkEnvelope<string> item) => inner.Enqueue(item);

    /// <inheritdoc/>
    public bool TryEnqueue(in WorkEnvelope<string> item) => inner.TryEnqueue(item);

    /// <inheritdoc/>
    public ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waitCalls);
        return inner.WaitToDequeueAsync(cancellationToken);
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
            return false;
        }

        _dequeued.Enqueue(item.Work);
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
    }

    /// <inheritdoc/>
    public int DiscardQueued(ICollection<WorkEnvelope<string>> discarded)
    {
        Interlocked.Increment(ref _discardCalls);
        var start = discarded.Count;
        var removed = inner.DiscardQueued(discarded);
        foreach (var item in discarded.Skip(start)) _discarded.Enqueue(item.Work);
        return removed;
    }

    /// <inheritdoc/>
    public void Complete() => inner.Complete();

    /// <inheritdoc/>
    public void WakeConsumer() => inner.WakeConsumer();

    /// <inheritdoc/>
    public void ConsumeMatchedWake() => inner.ConsumeMatchedWake();

    /// <inheritdoc/>
    public void Dispose() => inner.Dispose();
}
