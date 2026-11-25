// =============================================================================
// <copyright file="KeyedWorkQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using Bifrost.Core;

namespace Bifrost.Queues;

/// <summary>Creates <see cref="KeyedWorkQueue{TWork, TKey}"/> instances.</summary>
internal static class KeyedWorkQueue
{
    /// <summary>Creates a keyed work queue.</summary>
    /// <typeparam name="TWork">The work item type.</typeparam>
    /// <typeparam name="TKey">The work key type.</typeparam>
    /// <param name="selector">Returns the key of a work item. It runs once per admission attempt.</param>
    /// <param name="capacity">The most items that may be queued across all keys.</param>
    /// <param name="maxPendingPerKey">The most items that may be queued for one key.</param>
    /// <param name="maxConcurrencyPerKey">The most items of one key that may run at the same time.</param>
    /// <param name="comparer">The key comparer, or <see langword="null"/> for the default comparer.</param>
    /// <returns>A new, empty keyed work queue.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a limit is less than one.</exception>
    public static KeyedWorkQueue<TWork, TKey> Create<TWork, TKey>(
        Func<TWork, TKey> selector,
        int capacity,
        int maxPendingPerKey,
        int maxConcurrencyPerKey,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingPerKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrencyPerKey);
        return new KeyedWorkQueue<TWork, TKey>(selector, capacity, maxPendingPerKey, maxConcurrencyPerKey, comparer);
    }
}

/// <summary>
/// A leased work queue that keeps one FIFO per work key and runs at most a fixed number of items
/// of each key at the same time.
/// </summary>
/// <typeparam name="TWork">The work item type.</typeparam>
/// <typeparam name="TKey">The work key type.</typeparam>
/// <remarks>
/// <para>
/// <b>State.</b> One <see cref="Lock"/> protects all state: a dictionary from key to
/// <see cref="KeyState"/> (the key's queued items and its active count), a ready list, and the
/// total queued count. A key is in the ready list exactly when it has queued items and a free
/// slot, and it appears there at most once. A key with no queued and no active items is removed,
/// so idle keys hold no memory.
/// </para>
/// <para>
/// <b>Dispatch.</b> A dequeue takes the key at the head of the ready list, pops that key's oldest
/// item, and reserves a slot, all in one state change. The key goes back to the tail when it is
/// still ready, so ready keys take turns (round robin). The lease is the key's
/// <see cref="KeyState"/>, so a dequeue allocates nothing. <see cref="Release"/> frees the slot,
/// puts the key back in the ready list when it has queued items, and removes the key when it is
/// idle. A release of a lease that holds no slot throws, so a double release fails loudly.
/// </para>
/// <para>
/// <b>Admission.</b> The selector runs once, outside the lock. Under the lock the checks run in
/// this order: shutdown, then the global limit (<see cref="RejectionReason.CapacityExceeded"/>),
/// then the per-key limit (<see cref="RejectionReason.KeyLimitExceeded"/>), then the insert.
/// Pending means queued and not yet dequeued, so running items do not count against either limit.
/// </para>
/// <para>
/// <b>Wake accounting.</b> An item is dispatchable when it is among the first
/// <c>maxConcurrencyPerKey - active</c> queued items of its key. The queue releases one semaphore
/// permit each time an item becomes dispatchable: on an admission into a free slot, and on a
/// release that frees a slot some queued item waits for. A dequeue consumes one dispatchable item.
/// So a submission or a released slot wakes a worker exactly when there is work it can run, and a
/// saturated key wakes no one.
/// </para>
/// <para>
/// <b>Completion.</b> The semaphore stays authoritative after <see cref="Complete"/>. A wait never
/// polls for a positive <see cref="Count"/>, because queued items behind a saturated key still
/// count, and a poll would make every idle worker spin. A wait returns <see langword="false"/>
/// only when the queue is completed and has no queued items. When that state begins (at
/// <see cref="Complete"/> on an empty queue, or at the dequeue of the last item after it), the
/// queue releases one permit per registered waiter so that parked workers wake and exit.
/// <see cref="ConsumeMatchedWake"/> takes no permit in that state. Surplus permits (from the
/// retirement hand-off or the completion broadcast) are tolerated: they end in a dequeue miss.
/// </para>
/// </remarks>
internal sealed class KeyedWorkQueue<TWork, TKey>
    : IWorkQueue<WorkEnvelope<TWork>>, ILeasedWorkQueue<WorkEnvelope<TWork>>, IWorkQueueLifecycle, IDisposable
    where TKey : notnull
{
    private readonly Func<TWork, TKey> _selector;
    private readonly int _capacity;
    private readonly int _maxPendingPerKey;
    private readonly int _maxConcurrencyPerKey;
    private readonly Dictionary<TKey, KeyState> _keys;
    private readonly Queue<KeyState> _ready = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private int _queued;
    private int _dispatchable;
    private int _waiterCount;
    private bool _completed;
    private int _disposedGate;

    /// <summary>Initializes a new instance of the <see cref="KeyedWorkQueue{TWork, TKey}"/> class.</summary>
    /// <param name="selector">Returns the key of a work item.</param>
    /// <param name="capacity">The global queued-item limit.</param>
    /// <param name="maxPendingPerKey">The per-key queued-item limit.</param>
    /// <param name="maxConcurrencyPerKey">The per-key running-item limit.</param>
    /// <param name="comparer">The key comparer, or <see langword="null"/> for the default comparer.</param>
    /// <remarks>Use <see cref="KeyedWorkQueue.Create"/>, which validates the arguments.</remarks>
    internal KeyedWorkQueue(Func<TWork, TKey> selector, int capacity, int maxPendingPerKey, int maxConcurrencyPerKey,
        IEqualityComparer<TKey>? comparer)
    {
        _selector = selector;
        _capacity = capacity;
        _maxPendingPerKey = maxPendingPerKey;
        _maxConcurrencyPerKey = maxConcurrencyPerKey;
        _keys = new Dictionary<TKey, KeyState>(comparer);
    }

    /// <inheritdoc/>
    /// <remarks>The total number of queued items across all keys. Running items are not counted.</remarks>
    public int Count
    {
        get
        {
            lock (_gate) return _queued;
        }
    }

    /// <summary>Gets the number of queued items that a free worker could take now.</summary>
    internal int DispatchableCount
    {
        get
        {
            lock (_gate) return _dispatchable;
        }
    }

    /// <summary>Gets the number of keys with queued or running items. Test inspection only.</summary>
    internal int TrackedKeyCountForTest
    {
        get
        {
            lock (_gate) return _keys.Count;
        }
    }

    /// <summary>Gets the number of keys in the ready list. Test inspection only.</summary>
    internal int ReadyKeyCountForTest
    {
        get
        {
            lock (_gate) return _ready.Count;
        }
    }

    /// <summary>Gets the number of wake permits still available. Test inspection only.</summary>
    internal int PendingWakeCountForTest => _signal.CurrentCount;

    /// <summary>Gets a value indicating whether <see cref="Dispose"/> has run. Test inspection only.</summary>
    internal bool IsDisposedForTest => Volatile.Read(ref _disposedGate) == 1;

    /// <summary>Gets the number of running items of <paramref name="key"/>. Test inspection only.</summary>
    /// <param name="key">The work key.</param>
    /// <returns>The key's active count, or zero when the key is not tracked.</returns>
    internal int ActiveCountForTest(TKey key)
    {
        lock (_gate) return _keys.TryGetValue(key, out var state) ? state.Active : 0;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Thrown when the selector returns a <see langword="null"/> key.</exception>
    /// <remarks>
    /// The selector runs once, before the lock is taken, and its exceptions propagate to the
    /// caller. Under the lock the checks run in order: shutdown, the global limit, the per-key
    /// limit. An admission into a free slot releases one wake permit.
    /// </remarks>
    public EnqueueResult Enqueue(in WorkEnvelope<TWork> item)
    {
        var key = _selector(item.Work);
        if (key is null) throw new ArgumentException("The work key selector returned null.", nameof(item));
        lock (_gate)
        {
            if (_completed) return EnqueueResult.Rejected(RejectionReason.Shutdown);
            if (_queued >= _capacity) return EnqueueResult.Rejected(RejectionReason.CapacityExceeded);
            if (_keys.TryGetValue(key, out var state))
            {
                if (state.Items.Count >= _maxPendingPerKey)
                    return EnqueueResult.Rejected(RejectionReason.KeyLimitExceeded);
            }
            else
            {
                state = new KeyState(key);
                _keys.Add(key, state);
            }

            var dispatchable = state.Items.Count < _maxConcurrencyPerKey - state.Active;
            state.Items.Enqueue(item);
            _queued++;
            if (dispatchable) MarkDispatchable(state);
            return EnqueueResult.Accepted;
        }
    }

    /// <inheritdoc/>
    public bool TryEnqueue(in WorkEnvelope<TWork> item) => Enqueue(item).IsAccepted;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The wait registers as a waiter before it reads the state under the lock. The completion
    /// broadcast reads the waiter count under the same lock after it has made the state
    /// completed and empty. So either the wait sees that state and returns
    /// <see langword="false"/> at once, or the broadcast sees the registration and funds a permit.
    /// </para>
    /// <para>
    /// After <see cref="Complete"/>, the wait still parks on the semaphore while items are queued,
    /// even when none of them is dispatchable. A woken wait returns <see langword="true"/> unless
    /// the queue is completed and empty. The pooled builder keeps a parked wait from allocating a
    /// new state machine each time.
    /// </para>
    /// </remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waiterCount);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsCompletedAndEmpty()) return false;
            await _signal.WaitAsync(cancellationToken);
            return !IsCompletedAndEmpty();
        }
        finally
        {
            Interlocked.Decrement(ref _waiterCount);
        }
    }

    /// <summary>Always throws: a keyed item must be dequeued with its lease.</summary>
    /// <param name="item">Never set.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
    /// <remarks>
    /// A dequeue without a lease would reserve a key slot that nothing releases. Throwing makes
    /// any call site that skips the leased path fail at once instead of leaking a slot.
    /// </remarks>
    bool IWorkQueue<WorkEnvelope<TWork>>.TryDequeue([MaybeNullWhen(false)] out WorkEnvelope<TWork> item)
        => throw new InvalidOperationException("A keyed work queue must be dequeued through ILeasedWorkQueue.");

    /// <inheritdoc/>
    /// <remarks>
    /// The dequeue takes the ready key at the head of the list, pops its oldest item, and reserves
    /// a slot in one state change. The key goes back to the tail when it still has queued items and
    /// a free slot. The dequeue of the last queued item after <see cref="Complete"/> releases one
    /// permit per registered waiter, so parked workers wake and see the end of the queue.
    /// </remarks>
    public bool TryDequeue([MaybeNullWhen(false)] out WorkEnvelope<TWork> item, out object? lease)
    {
        lock (_gate)
        {
            if (!_ready.TryDequeue(out var state))
            {
                item = default;
                lease = null;
                return false;
            }

            state.InReady = false;
            item = state.Items.Dequeue();
            state.Active++;
            _queued--;
            _dispatchable--;
            if (state.Items.Count > 0 && state.Active < _maxConcurrencyPerKey) EnqueueReady(state);
            if (_completed && _queued == 0) BroadcastToWaiters();
            lease = state;
            return true;
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Thrown when <paramref name="lease"/> is not a lease of this queue type.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the lease holds no slot: it was already released, or it belongs to another queue.
    /// </exception>
    /// <remarks>
    /// The release frees one slot of the lease's key. When a queued item of that key was waiting
    /// for the slot, the item becomes dispatchable: the key goes back to the ready list and one
    /// wake permit is released. When the key has no queued and no running items left, it is
    /// removed.
    /// </remarks>
    public void Release(object lease)
    {
        if (lease is not KeyState state)
            throw new ArgumentException("The lease was not issued by a keyed work queue.", nameof(lease));
        lock (_gate)
        {
            if (state.Active == 0 || !_keys.TryGetValue(state.Key, out var tracked) || !ReferenceEquals(tracked, state))
                throw new InvalidOperationException("The lease holds no slot. It was already released or belongs to another queue.");

            var unblocks = state.Items.Count > _maxConcurrencyPerKey - state.Active;
            state.Active--;
            if (unblocks) MarkDispatchable(state);
            else if (state.Active == 0 && state.Items.Count == 0) _keys.Remove(state.Key);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The flag is published under the lock. When no items are queued, every registered waiter
    /// gets a permit so that it wakes and returns <see langword="false"/>. Otherwise the
    /// broadcast waits for the dequeue of the last queued item. Idempotent.
    /// </remarks>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            if (_queued == 0) BroadcastToWaiters();
        }
    }

    /// <inheritdoc/>
    /// <remarks>A retiring worker can win a permit and exit without a dequeue, so one permit is restored.</remarks>
    public void WakeConsumer() => _signal.Release();

    /// <inheritdoc/>
    /// <remarks>
    /// The check runs under the lock that <see cref="Complete"/> and the completion broadcast use,
    /// so a broadcast permit meant for a parked worker is never taken here.
    /// </remarks>
    public void ConsumeMatchedWake()
    {
        lock (_gate)
        {
            if (_completed && _queued == 0) return;
            _signal.Wait(0);
        }
    }

    /// <summary>Disposes the wake semaphore. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedGate, 1) == 1) return;
        _signal.Dispose();
    }

    private bool IsCompletedAndEmpty()
    {
        lock (_gate) return _completed && _queued == 0;
    }

    /// <summary>Records that one more item of <paramref name="state"/> is dispatchable and funds one wake.</summary>
    /// <param name="state">The key whose item became dispatchable.</param>
    /// <remarks>Called under the lock.</remarks>
    private void MarkDispatchable(KeyState state)
    {
        _dispatchable++;
        if (!state.InReady) EnqueueReady(state);
        _signal.Release();
    }

    private void EnqueueReady(KeyState state)
    {
        state.InReady = true;
        _ready.Enqueue(state);
    }

    /// <summary>Releases one permit per registered waiter.</summary>
    /// <remarks>
    /// Called under the lock once the queue is completed and empty. The fence orders the state
    /// change before the read of the waiter count.
    /// </remarks>
    private void BroadcastToWaiters()
    {
        Interlocked.MemoryBarrier();
        var waiters = Volatile.Read(ref _waiterCount);
        if (waiters > 0) _signal.Release(waiters);
    }

    /// <summary>The per-key state, which is also the lease for a dequeued item of that key.</summary>
    /// <param name="key">The work key.</param>
    internal sealed class KeyState(TKey key)
    {
        /// <summary>Gets the work key.</summary>
        public TKey Key { get; } = key;

        /// <summary>Gets the key's queued items, oldest first.</summary>
        public Queue<WorkEnvelope<TWork>> Items { get; } = new();

        /// <summary>Gets or sets the number of the key's items that are running.</summary>
        public int Active { get; set; }

        /// <summary>Gets or sets a value indicating whether the key is in the ready list.</summary>
        public bool InReady { get; set; }
    }
}
