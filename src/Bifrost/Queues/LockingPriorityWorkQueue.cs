// =============================================================================
// <copyright file="LockingPriorityWorkQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using Bifrost.Core;

namespace Bifrost.Queues;

/// <summary>
/// The exact-ordering lock-based priority binding of <see cref="IWorkQueue{T}"/> (DR-4):
/// a <see cref="PriorityQueue{TElement, TPriority}"/> ordered by the WFQ
/// virtual-time key (<see cref="PriorityKey"/>, DR-5), composed with a counted
/// <see cref="SemaphoreSlim"/> wake-up — the same composition as
/// <see cref="ConcurrentPriorityWorkQueue{TWork}"/> over a different structure. The
/// orchestrator selects this binding when class-based priority dispatch is enabled with
/// the lock-based strategy; the default strategy remains
/// <see cref="FifoChannelWorkQueue{T}"/>.
/// </summary>
/// <typeparam name="TWork">The type of work item carried by each envelope.</typeparam>
/// <remarks>
/// A single lock protects every BCL PriorityQueue access, admission decision and completion state.
/// Priorities are fixed long keys using the default comparer. Equal keys are not FIFO-stable.
/// Capacity and class watermarks are exact at admission. Every accepted item funds a semaphore
/// wake; completion closes admission and wakes readers to drain the remaining items.
/// </remarks>
internal sealed class LockingPriorityWorkQueue<TWork> : IWorkQueue<WorkEnvelope<TWork>>, IDisposable
{
    private readonly PriorityQueue<WorkEnvelope<TWork>, long> _queue;
    private readonly int _capacity;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly PriorityKey.Boosts _boosts;

    /// <summary>
    /// Per-class admission threshold counts (DR-6), precomputed once via the shared
    /// <see cref="AdmissionThresholds.Precompute"/> so the hot-path check is a single
    /// integer comparison. Because the watermark fractions are at most 1.0, every
    /// threshold is at most the capacity — the Interactive threshold doubles as this
    /// wrapper's hard capacity bound.
    /// </summary>
    private readonly AdmissionThresholds _thresholds;

    /// <summary>
    /// Serializes producers across the count-check-then-insert pair in
    /// <see cref="TryEnqueue(in WorkEnvelope{TWork})"/>, making the watermark and hard
    /// capacity bounds exact: between a gated check and its insert no other producer
    /// can insert, and concurrent consumers only lower the count.
    /// </summary>
    private readonly Lock _admissionGate = new();

    /// <summary>
    /// The number of consumers currently inside
    /// <see cref="WaitToDequeueAsync(CancellationToken)"/>. One side of the completion
    /// handshake: incremented (full fence) before the completed flag is read, so
    /// <see cref="Complete"/> can fund one wake-up permit per registered waiter.
    /// </summary>
    private int _waiterCount;

    /// <summary>
    /// Whether the queue has been completed for adding. Volatile: read on the enqueue
    /// and wait paths, published by <see cref="Complete"/> ahead of a full fence.
    /// </summary>
    private volatile bool _completed;

    /// <summary>
    /// Idempotency gate for <see cref="Dispose"/> (DR-3): the first caller flips it
    /// from 0 to 1 and disposes the owned <see cref="SemaphoreSlim"/>; later calls are
    /// no-ops, so double-dispose never reaches <see cref="SemaphoreSlim.Dispose()"/>
    /// twice.
    /// </summary>
    private int _disposedGate;

    /// <summary>
    /// Initializes a new instance of the <see cref="LockingPriorityWorkQueue{TWork}"/> class.
    /// </summary>
    /// <param name="capacity">
    /// The bounded capacity of the queue, enforced by this wrapper's admission path
    /// (the underlying locking structure has no built-in bounding).
    /// </param>
    /// <param name="options">The priority dispatch options defining the class boost windows.</param>
    /// <param name="timestampFrequency">
    /// The timestamp frequency, in units per second, of the <see cref="TimeProvider"/>
    /// that stamps <see cref="WorkEnvelope{TWork}.EnqueuedAtTicks"/> — i.e. its
    /// <see cref="TimeProvider.TimestampFrequency"/>. The boost windows are converted to
    /// these units exactly once here (<see cref="PriorityKey.Precompute"/>), keeping the
    /// per-item key computation pure <see cref="long"/> arithmetic.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="capacity"/> is less than one, or
    /// <paramref name="timestampFrequency"/> is zero or negative.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the admission watermarks in <paramref name="options"/> are not
    /// monotone non-decreasing with class urgency
    /// (<c>Batch ≤ Default ≤ Interactive</c>) — validated by the shared
    /// <see cref="AdmissionThresholds.Precompute"/> (DR-6).
    /// </exception>
    public LockingPriorityWorkQueue(int capacity, PriorityDispatchOptions options, long timestampFrequency)
    {
        _capacity = capacity;
        _boosts = PriorityKey.Precompute(options, timestampFrequency);
        _thresholds = AdmissionThresholds.Precompute(options, capacity);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        // Pre-size the heap to the hard bound: the admission path never lets the count
        // exceed the capacity, so the backing array never regrows after this.
        _queue = new PriorityQueue<WorkEnvelope<TWork>, long>(capacity);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The locking structure reads the heap count under its global lock, so the value
    /// is exact at the moment it is taken (it may be stale by the time the caller
    /// observes it under concurrency) — strictly tighter than the contract's
    /// approximate-under-concurrency allowance.
    /// </remarks>
    public int Count { get { lock (_admissionGate) return _queue.Count; } }

    /// <inheritdoc/>
    /// <remarks>
    /// Applies the class watermark admission check (DR-6) and the wrapper-enforced hard
    /// capacity bound in one gated step: producers serialize on the admission gate
    /// around the count-check-then-insert pair, so neither the watermark boundaries nor
    /// the capacity bound admit any transient slop — exact even under concurrency,
    /// unlike the sibling binding's approximate striped count. <c>false</c> means a
    /// watermark rejection, capacity exhaustion, or a completed queue — intentionally
    /// indistinguishable per the <see cref="IWorkQueue{T}"/> contract; a rejection never
    /// releases a wake-up permit.
    /// </remarks>
    public bool TryEnqueue(in WorkEnvelope<TWork> item) => Enqueue(item).IsAccepted;

    /// <inheritdoc/>
    public EnqueueResult Enqueue(in WorkEnvelope<TWork> item)
    {
        var key = PriorityKey.Compute(in item, in _boosts);
        lock (_admissionGate)
        {
            if (_completed) return EnqueueResult.Rejected(RejectionReason.Shutdown);
            int count = _queue.Count;
            if (count >= _capacity) return EnqueueResult.Rejected(RejectionReason.CapacityExceeded);
            if (count >= _thresholds.ThresholdFor(item.Class))
                return EnqueueResult.Rejected(RejectionReason.WatermarkExceeded);
            _queue.Enqueue(item, key);
            _signal.Release();
            return EnqueueResult.Accepted;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Cancellation surfaces as <see cref="OperationCanceledException"/> — thrown
    /// upfront for a pre-cancelled token and natively by
    /// <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/> for a parked wait —
    /// per the contract: the canonical consume loop owns the catch, keeping all three
    /// bindings consistent (DR-7). After <see cref="Complete"/>, the wait completes
    /// <c>false</c> once the queue is also empty; while residual items remain it
    /// completes <c>true</c> so the canonical consume loop can drain them (see the
    /// completion-wake remarks on the class).
    /// </para>
    /// <para>
    /// <b>Pooled async box (task-6, DR-3).</b> The park path
    /// (<see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>) suspends, so without
    /// pooling each parked wait heap-allocates a fresh async state-machine box. The
    /// <see cref="AsyncMethodBuilderAttribute"/> overriding the default builder with
    /// <see cref="PoolingAsyncValueTaskMethodBuilder{TResult}"/> amortizes that box via
    /// the runtime's per-thread pool, so steady-state park-path allocation drops toward
    /// zero. The pooled builder is trim/AOT-safe (no reflection or runtime codegen). The
    /// fast paths (pre-cancelled token; completed-queue poll) return synchronously and
    /// never rent a box. The completion-handshake semantics — the waiter-count
    /// increment/decrement, the <c>Interlocked.MemoryBarrier</c> fence in
    /// <see cref="Complete"/>, and the post-completion poll — are unchanged: only the
    /// state-machine's backing storage is pooled, not the awaited operation.
    /// </para>
    /// </remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken)
    {
        // Register as a waiter BEFORE reading the completed flag. Complete() publishes
        // the flag (full fence) before reading this count, so either this method sees
        // completion here and never parks, or Complete() sees the registration and funds
        // a wake-up permit — never neither (store-buffering handshake).
        Interlocked.Increment(ref _waiterCount);
        try
        {
            // Contract cancellation semantic: surface OperationCanceledException
            // (checked before the completed flag, matching the channel binding's
            // WaitToReadAsync ordering); the consume loop's boundary absorbs it.
            cancellationToken.ThrowIfCancellationRequested();

            if (_completed)
            {
                // Post-completion the semaphore is no longer authoritative (residual
                // items may be drained directly without consuming permits), so the wait
                // degrades to a poll: true while residual items remain, else false.
                return Count > 0;
            }

            // Cancellation of a parked wait propagates the semaphore's native
            // OperationCanceledException to the consume loop's boundary.
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Permit acquired: either an item permit (an item is available — this
            // binding's TryDequeue never spuriously misses, though a CONCURRENT consumer
            // may still claim it first, so callers keep the canonical loop) or a
            // completion wake-up (completed AND empty: report shutdown).
            return !_completed || Count > 0;
        }
        finally
        {
            Interlocked.Decrement(ref _waiterCount);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Exact, non-relaxed dequeue: the removed envelope has the TRUE minimum
    /// virtual-time key (no rank error), and <c>false</c> is returned only when the
    /// queue is genuinely empty — never spuriously. Callers must still tolerate misses
    /// per the <see cref="IWorkQueue{T}"/> contract (a concurrent consumer can drain
    /// the last item between the wait and this call), so the canonical consume loop is
    /// unchanged.
    /// </remarks>
    public bool TryDequeue([MaybeNullWhen(false)] out WorkEnvelope<TWork> item)
    {
        lock (_admissionGate) return _queue.TryDequeue(out item, out _);
    }

    /// <summary>
    /// Marks the queue as complete for adding (concrete-only member, mirroring
    /// <see cref="ConcurrentPriorityWorkQueue{TWork}.Complete"/> and
    /// <see cref="FifoChannelWorkQueue{T}.Complete"/>). Residual items remain
    /// dequeueable via <see cref="TryDequeue(out WorkEnvelope{TWork})"/> or the
    /// canonical consume loop; once drained,
    /// <see cref="WaitToDequeueAsync(CancellationToken)"/> completes <c>false</c>.
    /// Wakes every consumer currently parked on the semaphore by releasing one permit
    /// per registered waiter (the no-hang handshake — see the class remarks).
    /// Idempotent: only the first call performs the wake.
    /// </summary>
    public void Complete()
    {
        lock (_admissionGate)
        {
            if (_completed) return;
            _completed = true;

            // Full fence between publishing the flag and reading the waiter count: pairs
            // with the Interlocked.Increment in WaitToDequeueAsync so a waiter that missed
            // the flag is always visible here and receives a wake-up permit.
            Interlocked.MemoryBarrier();

            var waiters = Volatile.Read(ref _waiterCount);
            if (waiters > 0)
            {
                _signal.Release(waiters);
            }
        }
    }

    /// <summary>
    /// Releases the owned <see cref="SemaphoreSlim"/> wake-up (DR-3). Neither the
    /// underlying <see cref="PriorityQueue{TElement, TPriority}"/> nor the
    /// <see cref="Lock"/> admission gate is disposable, so the semaphore is the only
    /// resource to release. Guarded by an
    /// <see cref="Interlocked.Exchange(ref int, int)"/> idempotency flag, so a second
    /// <see cref="Dispose"/> call is a safe no-op (it never disposes the semaphore
    /// twice). A wait parked after disposal surfaces
    /// <see cref="ObjectDisposedException"/> from the disposed semaphore — disposal is
    /// the orchestrator's responsibility on the shutdown path, after the consume loop
    /// has drained.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedGate, 1) == 1)
        {
            return;
        }

        _signal.Dispose();
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="Dispose"/> has run (DR-3). Test-only
    /// inspection seam (via InternalsVisibleTo) for asserting that an owning
    /// <see cref="WorkOrchestrator{TWork}"/> disposed this binding, without widening the
    /// public surface or relying on the post-completion wait short-circuit (which never
    /// reaches the disposed semaphore on a completed queue).
    /// </summary>
    internal bool IsDisposedForTest => Volatile.Read(ref _disposedGate) == 1;
}
