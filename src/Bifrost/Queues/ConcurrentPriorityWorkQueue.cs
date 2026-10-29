// =============================================================================
// <copyright file="ConcurrentPriorityWorkQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using Bifrost.Concurrency;
using Bifrost.Core;

namespace Bifrost.Queues;

/// <summary>
/// The MultiQueue concurrent-priority binding of <see cref="IWorkQueue{T}"/> (DR-4):
/// a bounded <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/> ordered by the
/// WFQ virtual-time key (<see cref="PriorityKey"/>, DR-5), composed with an
/// counted <see cref="SemaphoreSlim"/> wake-up. The orchestrator selects this
/// binding when class-based priority dispatch is enabled with the lock-striped MultiQueue
/// strategy (T18's <c>DispatchStrategy.PriorityRelaxed</c>); the default strategy
/// remains <see cref="FifoChannelWorkQueue{T}"/>.
/// </summary>
/// <typeparam name="TWork">The type of work item carried by each envelope.</typeparam>
/// <remarks>
/// <para>Each accepted enqueue funds a semaphore wake. A wait consumes one permit, but a
/// consumer may drain multiple entries afterward; permit count is not queue length and a wake
/// does not reserve an item. Consumers must retry dequeue/wait when another consumer wins.</para>
/// <para>Completion closes admission and waits for in-flight producers to publish their entries
/// and wakes before publishing the completed flag. Waiters register before reading that flag;
/// completion publishes it before reading the waiter count, with full fences. Thus a waiter
/// observes closure or receives a completion wake. After closure, waits poll residual entries.</para>
/// <para>Watermarks are approximate under concurrency because they use striped Count; hard
/// capacity remains enforced by the underlying queue's reservation counter. Enqueue reports
/// CapacityExceeded, WatermarkExceeded or Shutdown; TryEnqueue discards that distinction.</para>
/// <para>Virtual-time offsets express ordering preferences, not service-time guarantees.
/// A Batch key outranks a fresh Interactive key after the boost plus batch penalty. MultiQueue
/// removal is relaxed, so it need not select that key. Backlog and handler availability also
/// determine waiting time. Admission is not durable execution.</para>
/// </remarks>
internal sealed class ConcurrentPriorityWorkQueue<TWork> : IWorkQueue<WorkEnvelope<TWork>>, IDisposable
{
    private readonly ConcurrentPriorityQueue<WorkEnvelope<TWork>, long> _queue;
    private int _admissionState;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly PriorityKey.Boosts _boosts;

    /// <summary>
    /// Per-class admission threshold COUNTS (DR-6), precomputed once in the
    /// constructor via the shared <see cref="AdmissionThresholds.Precompute"/> (also
    /// used by <see cref="LockingPriorityWorkQueue{TWork}"/>) so the hot-path check is
    /// a single integer comparison — no floating point, no allocation. An enqueue of a
    /// class is rejected while <see cref="Count"/> ≥ its threshold.
    /// </summary>
    private readonly AdmissionThresholds _thresholds;

    /// <summary>
    /// The stickiness factor the binding resolved from <see cref="PriorityDispatchOptions.CpqTuning"/>
    /// and handed the MultiQueue at construction.
    /// </summary>
    private readonly int _resolvedStickiness;

    /// <summary>
    /// The buffer capacity the binding resolved from <see cref="PriorityDispatchOptions.CpqTuning"/> and
    /// the element type, and handed the MultiQueue at construction. Reference-bearing work buffers; pure
    /// value-type work does not, unless a profile forces it.
    /// </summary>
    private readonly int _resolvedBufferCapacity;

    /// <summary>
    /// The number of consumers currently inside
    /// <see cref="WaitToDequeueAsync(CancellationToken)"/>. One side of the completion
    /// handshake: incremented (full fence) before the completed flag is read, so
    /// <see cref="Complete"/> can fund one wake-up permit per registered waiter.
    /// </summary>
    private int _waiterCount;

    /// <summary>
    /// Serializes completion calls through admission closure and consumer wake-up.
    /// </summary>
    private readonly Lock _completionGate = new();

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
    /// Initializes a new instance of the <see cref="ConcurrentPriorityWorkQueue{TWork}"/> class.
    /// </summary>
    /// <param name="capacity">The bounded capacity of the queue.</param>
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
    /// (<c>Batch ≤ Default ≤ Interactive</c>) — a more urgent class must never be
    /// shed before a less urgent one (DR-6). Validated by the shared
    /// <see cref="AdmissionThresholds.Precompute"/>, at consumption, because the
    /// cross-property relation cannot be a single-setter guard on the options without
    /// order-of-assignment traps.
    /// </exception>
    public ConcurrentPriorityWorkQueue(int capacity, PriorityDispatchOptions options, long timestampFrequency)
    {
        _boosts = PriorityKey.Precompute(options, timestampFrequency);

        // Validate watermark monotonicity and precompute the per-class admission
        // thresholds as integer COUNTS (DR-6), mirroring the Boosts precompute above:
        // the floating-point watermark × capacity products are evaluated exactly once,
        // so TryEnqueue's admission check is pure integer comparison.
        _thresholds = AdmissionThresholds.Precompute(options, capacity);

        // Resolve the tuning profile to concrete MultiQueue settings once. Buffering is keyed off the
        // element's reference-ness because that is where it earns its keep; the work envelope is a struct,
        // so this turns on whenever TWork is, or contains, a managed reference and stays off for pure
        // value-type work.
        (_resolvedStickiness, _resolvedBufferCapacity) = CpqTuningResolver.Resolve(
            options.CpqTuning,
            RuntimeHelpers.IsReferenceOrContainsReferences<WorkEnvelope<TWork>>());

        _queue = ConcurrentPriorityQueue<WorkEnvelope<TWork>, long>.Create(new()
        {
            Capacity = capacity,
            Stickiness = _resolvedStickiness,
            BufferCapacity = _resolvedBufferCapacity,
        });
    }

    /// <summary>
    /// The stickiness factor the binding resolved from its tuning profile and handed the MultiQueue.
    /// Test-only inspection seam (via InternalsVisibleTo).
    /// </summary>
    internal int ResolvedStickinessForTest => _resolvedStickiness;

    /// <summary>
    /// The buffer capacity the binding resolved from its tuning profile and element type, and handed the
    /// MultiQueue. Test-only inspection seam (via InternalsVisibleTo).
    /// </summary>
    internal int ResolvedBufferCapacityForTest => _resolvedBufferCapacity;

    /// <inheritdoc/>
    /// <remarks>
    /// The MultiQueue sums lock-striped per-sub-queue counters without locking, so the
    /// value is approximate under concurrency and exact at quiescence — exactly the
    /// contract's documented bounds.
    /// </remarks>
    public int Count => _queue.Count;

    /// <inheritdoc/>
    /// <remarks>
    /// Applies the class watermark admission check (DR-6) against the approximate
    /// count, then computes the virtual-time key from the envelope
    /// (<see cref="PriorityKey.Compute{TWork}"/>) and offers it to the bounded
    /// priority queue. <c>false</c> means a watermark rejection, hard capacity
    /// exhaustion, or a completed queue — intentionally indistinguishable per the
    /// <see cref="IWorkQueue{T}"/> contract; a rejection never releases a wake-up
    /// permit. The watermark
    /// check is approximate under concurrency (striped count) and exact at quiescence;
    /// the structure's hard capacity gate stays as the exact backstop.
    /// </remarks>
    public bool TryEnqueue(in WorkEnvelope<TWork> item) => Enqueue(item).IsAccepted;

    /// <inheritdoc/>
    public EnqueueResult Enqueue(in WorkEnvelope<TWork> item)
    {
        // The high bit closes admission. Complete waits for previously admitted producers,
        // including their wake-up releases, before allowing consumers to observe completion.
        int state;
        do
        {
            state = Volatile.Read(ref _admissionState);
            if (state < 0) return EnqueueResult.Rejected(RejectionReason.Shutdown);
            if (state == int.MaxValue) throw new InvalidOperationException("Too many concurrent producers.");
        }

        while (Interlocked.CompareExchange(ref _admissionState, state + 1, state) != state);
        try
        {
            int count = _queue.Count;
            if (count >= _queue.Capacity)
                return EnqueueResult.Rejected(RejectionReason.CapacityExceeded);
            if (count >= _thresholds.ThresholdFor(item.Class))
                return EnqueueResult.Rejected(RejectionReason.WatermarkExceeded);
            if (!_queue.TryEnqueue(item, PriorityKey.Compute(in item, in _boosts)))
                return EnqueueResult.Rejected(RejectionReason.CapacityExceeded);
            _signal.Release();
            return EnqueueResult.Accepted;
        }
        finally
        {
            Interlocked.Decrement(ref _admissionState);
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
                return !_queue.IsEmpty;
            }

            // Cancellation of a parked wait propagates the semaphore's native
            // OperationCanceledException to the consume loop's boundary.
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Permit acquired: either an item permit (an item is likely available —
            // TryDequeue may still spuriously miss; consumers loop back to this wait) or
            // a completion wake-up (completed AND empty: report shutdown).
            return !_completed || !_queue.IsEmpty;
        }
        finally
        {
            Interlocked.Decrement(ref _waiterCount);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to the MultiQueue's relaxed two-choice dequeue: the removed envelope has
    /// a relaxed virtual-time priority with no deterministic rank bound. A <c>false</c> may be
    /// spurious under concurrent mutation — consumers loop back to
    /// <see cref="WaitToDequeueAsync(CancellationToken)"/> per the canonical consume loop.
    /// </remarks>
    public bool TryDequeue([MaybeNullWhen(false)] out WorkEnvelope<TWork> item)
        => _queue.TryDequeue(out item, out _);

    /// <summary>
    /// Marks the queue as complete for adding (concrete-only member, mirroring
    /// <see cref="FifoChannelWorkQueue{T}.Complete"/>). Residual items remain dequeueable
    /// via <see cref="TryDequeue(out WorkEnvelope{TWork})"/> or the canonical consume
    /// loop; once drained, <see cref="WaitToDequeueAsync(CancellationToken)"/> completes
    /// <c>false</c>. Wakes every consumer currently parked on the semaphore by releasing
    /// one permit per registered waiter (the no-hang handshake — see the class remarks).
    /// Idempotent: only the first call performs the wake.
    /// </summary>
    public void Complete()
    {
        lock (_completionGate)
        {
            if (_completed) return;
            Interlocked.Or(ref _admissionState, int.MinValue);
            SpinWait spinner = default;
            while (Volatile.Read(ref _admissionState) != int.MinValue) spinner.SpinOnce();
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
    /// Releases the owned <see cref="SemaphoreSlim"/> wake-up (DR-3). The underlying
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/> holds no unmanaged or
    /// disposable resources, so the semaphore is the only resource to release. Guarded by
    /// an <see cref="Interlocked.Exchange(ref int, int)"/> idempotency flag, so a second
    /// <see cref="Dispose"/> call is a safe no-op (it never disposes the semaphore twice).
    /// A wait parked after disposal surfaces <see cref="ObjectDisposedException"/> from
    /// the disposed semaphore — disposal is the orchestrator's responsibility on the
    /// shutdown path, after the consume loop has drained.
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
