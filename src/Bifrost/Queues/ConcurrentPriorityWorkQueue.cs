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
/// item-counting <see cref="SemaphoreSlim"/> wake-up. The orchestrator selects this
/// binding when class-based priority dispatch is enabled with the lock-free MultiQueue
/// strategy (T18's <c>DispatchStrategy.PriorityMultiQueue</c>); the default strategy
/// remains <see cref="FifoChannelWorkQueue{T}"/>.
/// </summary>
/// <typeparam name="TWork">The type of work item carried by each envelope.</typeparam>
/// <remarks>
/// <para>
/// <b>Semaphore-counts-items invariant.</b> The semaphore's permit count tracks the
/// number of accepted-but-unclaimed items: <see cref="TryEnqueue(in WorkEnvelope{TWork})"/>
/// releases exactly one permit per item the priority queue ACCEPTED (a capacity or
/// post-completion rejection never releases), and each successful
/// <see cref="WaitToDequeueAsync(CancellationToken)"/> consumes exactly one. N accepted
/// items therefore fund exactly N successful waits — no lost or phantom wake-ups.
/// </para>
/// <para>
/// <b>Relaxed ordering (DR-5).</b> Dequeue order respects the virtual-time key only
/// approximately: the MultiQueue's two-choice <c>TryDequeue</c> removes an element with
/// an expected rank error of <c>(5/6)·n</c> (n = sub-queue count ≈ 4 × processor count),
/// a relaxation that rides on top of the total order the key defines and is acceptable
/// dispatch-ordering noise per design DR-5. Correctness must never depend on how close
/// to the true minimum a pop lands; the starvation bound comes from the bounded boost
/// window in the key itself, not from dequeue exactness.
/// </para>
/// <para>
/// <b>Completion wake mechanism.</b> <see cref="Complete"/> must wake consumers parked
/// on the semaphore of an empty queue (the no-hang property) without disturbing the
/// permit accounting of residual items. It does so by releasing one permit per
/// registered waiter: <see cref="WaitToDequeueAsync(CancellationToken)"/> registers
/// itself (an <see cref="Interlocked"/> increment, a full fence) BEFORE reading the
/// completed flag, and <see cref="Complete"/> publishes the flag (with a full fence)
/// BEFORE reading the waiter count — a store-buffering handshake guaranteeing that a
/// waiter either observes completion and never parks, or is observed by
/// <see cref="Complete"/> and receives a wake-up permit. Never neither, so no consumer
/// is left parked forever. After completion the semaphore is no longer authoritative
/// (residual items may be drained directly via <see cref="TryDequeue(out WorkEnvelope{TWork})"/>
/// without consuming permits), so the wait degrades to a poll: <c>true</c> while
/// residual items remain, <c>false</c> once completed AND empty. Over-released permits
/// from this handshake are harmless: a woken waiter that finds the queue completed and
/// empty reports shutdown, and post-completion waits never park.
/// </para>
/// <para>
/// <b>Class-aware watermark admission (DR-6).</b> Under pressure this binding sheds
/// the LOWEST class FIRST, at admission: Batch enqueues are rejected once the
/// approximate count reaches <see cref="PriorityDispatchOptions.BatchAdmissionWatermark"/>
/// × capacity (0.90 by default), Default at
/// <see cref="PriorityDispatchOptions.DefaultAdmissionWatermark"/> × capacity (0.95),
/// and Interactive is admitted to
/// <see cref="PriorityDispatchOptions.InteractiveAdmissionWatermark"/> × capacity
/// (1.0 — hard capacity). This is the WRED / priority-load-shedding precedent applied
/// at the enqueue gate: shedding happens by REFUSING new low-class work while
/// reserving the remaining headroom for urgent work, so no eviction machinery (and no
/// remove-from-middle support in the CPQ) is ever needed. It is also the design's
/// admission-side priority-inversion fix: the rejected alternative —
/// producer-waits-at-capacity — would let a backlog of Batch items block the producer
/// of an Interactive item, inverting priority at the queue boundary; shedding the low
/// class at admission keeps the gate open for urgent work instead.
/// </para>
/// <para>
/// <b>Approximate-count tolerance (DR-6).</b> The watermark check reads
/// <see cref="Count"/>, the MultiQueue's striped sum, which is approximate under
/// concurrency — so admission near a watermark may transiently over- or under-admit
/// by roughly the in-flight operation count. This slop is tolerated by design:
/// watermarks are load-shedding heuristics, not invariants, and the structure's hard
/// capacity gate remains the exact backstop. At quiescence the striped sum is exact,
/// so the boundary is exact (count 89 admits Batch at the default 0.90 × 100; count
/// 90 rejects).
/// </para>
/// <para>
/// <b>Interaction with the virtual-time key (DR-5 × DR-6).</b> The two mechanisms are
/// complementary, not overlapping: watermarks govern the ADMISSION side (which
/// classes still get in as the queue fills — shed order under pressure), while the
/// key's bounded boost window governs the DEQUEUE side (admitted low-class work
/// cannot be starved longer than the boost window). Neither subsumes the other — the
/// key alone cannot prevent a full queue of Batch work from crowding out Interactive
/// admission, and watermarks alone cannot bound how long admitted Batch work waits.
/// </para>
/// <para>
/// <see cref="Complete"/> is a concrete-only member — it is not part of
/// <see cref="IWorkQueue{T}"/>; the orchestrator reaches it via the concrete type,
/// mirroring <see cref="FifoChannelWorkQueue{T}"/>. This binding has no producer-wait
/// accept path: producers that need backpressure observe <c>TryEnqueue == false</c>.
/// </para>
/// </remarks>
internal sealed class ConcurrentPriorityWorkQueue<TWork> : IWorkQueue<WorkEnvelope<TWork>>, IDisposable
{
    private readonly ConcurrentPriorityQueue<WorkEnvelope<TWork>, long> _queue;
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
    /// The number of consumers currently inside
    /// <see cref="WaitToDequeueAsync(CancellationToken)"/>. One side of the completion
    /// handshake: incremented (full fence) before the completed flag is read, so
    /// <see cref="Complete"/> can fund one wake-up permit per registered waiter.
    /// </summary>
    private int _waiterCount;

    /// <summary>
    /// Idempotency gate for <see cref="Complete"/>: the first caller flips it and
    /// performs the completion wake; later calls are no-ops.
    /// </summary>
    private int _completedGate;

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

        _queue = new ConcurrentPriorityQueue<WorkEnvelope<TWork>, long>(capacity);
    }

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
    /// permit, so the semaphore-counts-items invariant is preserved. The watermark
    /// check is approximate under concurrency (striped count) and exact at quiescence;
    /// the structure's hard capacity gate stays as the exact backstop.
    /// </remarks>
    public bool TryEnqueue(in WorkEnvelope<TWork> item)
    {
        if (_completed)
        {
            // Mirrors the FIFO binding's completed-channel behavior: rejection via false,
            // never an exception.
            return false;
        }

        if (_queue.Count >= _thresholds.ThresholdFor(item.Class))
        {
            // Watermark shed (DR-6): the queue has filled past this class's admission
            // threshold — refuse the item, reserving the remaining headroom for more
            // urgent classes. No permit is released. The striped count read here may
            // be slightly stale under concurrency; that slop is tolerated by design.
            return false;
        }

        if (!_queue.TryEnqueue(item, PriorityKey.Compute(in item, in _boosts)))
        {
            // Hard capacity: the bounded gate rejected the item — no permit is released.
            // This exact backstop also covers any watermark under-count slop.
            return false;
        }

        // Semaphore counts ITEMS: exactly one permit per accepted item.
        _signal.Release();
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Cancellation surfaces as <see cref="OperationCanceledException"/> — thrown
    /// upfront for a pre-cancelled token and natively by
    /// <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/> for a parked wait —
    /// per the contract: the canonical consume loop owns the catch, keeping all three
    /// bindings consistent (DR-7). After <see cref="Complete"/>, the wait completes
    /// <c>false</c> once the queue is also empty; while residual items remain it
    /// completes <c>true</c> so the canonical consume loop can drain them (see the
    /// completion-wake remarks on the class).
    /// </remarks>
    /// <remarks>
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
    /// one of the smallest virtual-time keys, within the documented <c>(5/6)·n</c>
    /// expected rank error (acceptable dispatch noise per DR-5). A <c>false</c> may be
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
        if (Interlocked.Exchange(ref _completedGate, 1) == 1)
        {
            return;
        }

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
