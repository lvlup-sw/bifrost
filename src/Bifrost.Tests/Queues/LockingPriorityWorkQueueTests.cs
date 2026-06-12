// =============================================================================
// <copyright file="LockingPriorityWorkQueueTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Contract and binding-specific tests for <c>LockingPriorityWorkQueue&lt;TWork&gt;</c>,
/// the exact-ordering lock-based priority binding of <see cref="IWorkQueue{T}"/>
/// (T22, DR-4): a <c>Bifrost.Concurrency.LockingPriorityQueue</c> ordered by the WFQ
/// virtual-time key, composed with an item-counting <see cref="SemaphoreSlim"/> wake-up,
/// class-aware watermark admission, and the completion handshake — the same composition
/// as <c>ConcurrentPriorityWorkQueue&lt;TWork&gt;</c> over a different structure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract-suite adapter choice.</b> Mirrors the sibling MultiQueue binding's tests:
/// <see cref="CreateQueue(int)"/> returns a thin test-only adapter
/// (<see cref="IntEnvelopeAdapter"/>) wrapping each <see cref="int"/> into a
/// <see cref="WorkEnvelope{TWork}"/> with <see cref="WorkClass.Interactive"/> — the class
/// admitted to full hard capacity under the watermark admission policy (DR-6), which the
/// suite's fill-to-capacity expectations require — and a monotonic stamp, so contract
/// items dequeue in plain enqueue order. The adapter forwards <c>Count</c>, the wait, and
/// the dequeue unchanged, so every contract property exercises the real composed binding
/// under DEFAULT dispatch options.
/// </para>
/// <para>
/// <b>The distinguishing property: exact ordering.</b> Unlike the MultiQueue binding —
/// whose relaxed two-choice dequeue forces a rank-BOUNDED (statistical) ordering
/// assertion — every dequeue from the locking binding returns the true minimum
/// virtual-time key, because all operations serialize on one global lock around a binary
/// heap. <see cref="ExactOrdering_QuiescentDrain_StrictVirtualTimeOrder"/> therefore
/// asserts the STRICT drain sequence, item by item, with no statistical margin. The
/// <see cref="IWorkQueue{T}"/> contract still permits spurious dequeue misses and rank
/// relaxation — this binding simply never produces them.
/// </para>
/// </remarks>
[InheritsTests]
public sealed class LockingPriorityWorkQueueTests : WorkQueueContractTests
{
    /// <summary>
    /// Per-test ceiling for waits that are expected to complete promptly.
    /// </summary>
    private static readonly TimeSpan BindingWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Generous ceiling for the multi-producer/multi-consumer stress test.
    /// </summary>
    private static readonly TimeSpan StressTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The timestamp frequency used by these tests: 1 000 units per second, so one
    /// timestamp tick is one millisecond and boost-window conversions stay exact.
    /// </summary>
    private const long TestTimestampFrequency = 1_000;

    /// <summary>
    /// The capacity used by the watermark scenario: 100, so the default fractions land
    /// on whole-item thresholds (Batch 90, Default 95, Interactive 100).
    /// </summary>
    private const int WatermarkCapacity = 100;

    /// <summary>
    /// Verifies the binding's DISTINGUISHING property (DR-4): a quiescent
    /// single-threaded drain returns envelopes in EXACT virtual-time key order — the
    /// strict sequence Interactive group, then Default group, then Batch group, each in
    /// stamp order — with every <c>TryDequeue</c> popping (no spurious misses). The
    /// sibling MultiQueue binding can only assert a rank-bounded MEAN-position property
    /// here; the lock-based heap admits an exact, per-item assertion.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ExactOrdering_QuiescentDrain_StrictVirtualTimeOrder()
    {
        // Arrange — no statistical sizing needed (exact ordering): a modest group size
        // suffices. 2× capacity headroom so the class watermarks (Batch 0.90, Default
        // 0.95 of capacity) never trip during the interleaved fill — this test isolates
        // dequeue-side ORDERING; admission is covered by Watermarks_ApplyIdentically.
        const int groupSize = 64;
        const int totalItems = 3 * groupSize;

        var options = new PriorityDispatchOptions
        {
            InteractiveBoostWindow = TimeSpan.FromSeconds(30), // 30_000 ticks at 1 kHz
            BatchPenaltyWindow = TimeSpan.FromSeconds(60),     // 60_000 ticks at 1 kHz
        };
        var queue = CreateEnvelopeQueue(2 * totalItems, options);

        // Virtual-time layout (ticks at 1 kHz; smaller key = sooner), mirroring the
        // sibling binding's ordering test:
        //   Interactive — FRESHEST timestamps (1_520_000+i), key 1_490_000+i (boost −30_000)
        //   Default     — middle timestamps   (1_500_000+i), key 1_500_000+i (no boost)
        //   Batch       — OLDEST timestamps   (1_450_000+i), key 1_510_000+i (penalty +60_000)
        // Ids: interactive [0, g), default [g, 2g), batch [2g, 3g) — so the exact key
        // order is precisely id order 0, 1, …, 3g−1. All keys are distinct, so the heap
        // order is fully determined.
        for (var i = 0; i < groupSize; i++)
        {
            var batchOk = queue.TryEnqueue(
                new WorkEnvelope<int>((2 * groupSize) + i, WorkClass.Batch, 1_450_000 + i));
            await Assert.That(batchOk).IsTrue();
            var defaultOk = queue.TryEnqueue(
                new WorkEnvelope<int>(groupSize + i, WorkClass.Default, 1_500_000 + i));
            await Assert.That(defaultOk).IsTrue();
            var interactiveOk = queue.TryEnqueue(
                new WorkEnvelope<int>(i, WorkClass.Interactive, 1_520_000 + i));
            await Assert.That(interactiveOk).IsTrue();
        }

        // Act — quiescent drain: under the global lock every TryDequeue on a non-empty
        // queue pops the true minimum key; a miss would be a binding bug, not relaxation.
        var drainOrder = new List<int>(totalItems);
        for (var pos = 0; pos < totalItems; pos++)
        {
            var got = queue.TryDequeue(out var envelope);
            await Assert.That(got).IsTrue();
            drainOrder.Add(envelope.Work);
        }

        await Assert.That(queue.Count).IsEqualTo(0);

        // Assert — STRICT sequence: exactly 0, 1, …, 3g−1, no rank error tolerated.
        var strictlyOrdered = drainOrder.SequenceEqual(Enumerable.Range(0, totalItems));
        await Assert.That(strictlyOrdered).IsTrue();
    }

    /// <summary>
    /// Verifies the watermark admission policy (DR-6) applies IDENTICALLY to this
    /// binding, reusing the sibling's quiescent-exactness pattern in one ramped pass:
    /// Batch rejected once the count reaches 0.90 × capacity, Default at 0.95, and
    /// Interactive admitted all the way to hard capacity — only the 101st Interactive
    /// enqueue into a capacity-100 queue is rejected. Quiescent, so the lock-exact count
    /// makes every boundary deterministic.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Watermarks_ApplyIdentically()
    {
        // Arrange — fill to the Batch threshold (0.90 × 100 = 90) with Default items
        // (Default threshold is 95, so the fill itself never trips a watermark).
        var queue = CreateEnvelopeQueue(WatermarkCapacity);
        var stamp = 0L;
        for (var i = 0; i < 90; i++)
        {
            var accepted = queue.TryEnqueue(new WorkEnvelope<int>(i, WorkClass.Default, ++stamp));
            await Assert.That(accepted).IsTrue();
        }

        await Assert.That(queue.Count).IsEqualTo(90);

        // Act + Assert — Batch sheds at its watermark; Interactive is still admitted.
        var batchAtBatchWatermark = queue.TryEnqueue(new WorkEnvelope<int>(900, WorkClass.Batch, ++stamp));
        await Assert.That(batchAtBatchWatermark).IsFalse();

        // Ramp to the Default threshold (95) with Interactive items.
        for (var i = 90; i < 95; i++)
        {
            var accepted = queue.TryEnqueue(new WorkEnvelope<int>(i, WorkClass.Interactive, ++stamp));
            await Assert.That(accepted).IsTrue();
        }

        await Assert.That(queue.Count).IsEqualTo(95);

        // Default now sheds too; Interactive continues to full capacity.
        var defaultAtDefaultWatermark = queue.TryEnqueue(new WorkEnvelope<int>(901, WorkClass.Default, ++stamp));
        await Assert.That(defaultAtDefaultWatermark).IsFalse();

        for (var i = 95; i < WatermarkCapacity; i++)
        {
            var accepted = queue.TryEnqueue(new WorkEnvelope<int>(i, WorkClass.Interactive, ++stamp));
            await Assert.That(accepted).IsTrue();
        }

        await Assert.That(queue.Count).IsEqualTo(WatermarkCapacity);

        // Hard capacity is the only Interactive limit — enforced by the wrapper's
        // admission path, since the locking structure has no built-in bounding.
        var interactiveOverCapacity = queue.TryEnqueue(new WorkEnvelope<int>(902, WorkClass.Interactive, ++stamp));
        await Assert.That(interactiveOverCapacity).IsFalse();
        await Assert.That(queue.Count).IsEqualTo(WatermarkCapacity);
    }

    /// <summary>
    /// Verifies the completion no-hang property: consumers parked in
    /// <c>WaitToDequeueAsync</c> on an EMPTY queue are all woken by <c>Complete()</c>
    /// (one semaphore release per registered waiter) and complete <c>false</c> — no
    /// consumer is left waiting forever.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Complete_WakesParkedConsumers_NoHang()
    {
        // Arrange — three consumers parked on an empty queue with a non-cancellable token.
        const int consumerCount = 3;
        var queue = CreateEnvelopeQueue(SmallCapacity);
        var waits = new Task<bool>[consumerCount];
        for (var c = 0; c < consumerCount; c++)
        {
            waits[c] = queue.WaitToDequeueAsync(CancellationToken.None).AsTask();
        }

        // Give the consumers time to actually park on the semaphore.
        await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        await Assert.That(waits.Any(w => w.IsCompleted)).IsFalse();

        // Act — completion must wake every parked waiter.
        queue.Complete();
        var results = await Task.WhenAll(waits).WaitAsync(BindingWaitTimeout).ConfigureAwait(false);

        // Assert — completed AND empty: every wait reports shutdown via false.
        foreach (var signaled in results)
        {
            await Assert.That(signaled).IsFalse();
        }
    }

    /// <summary>
    /// Verifies graceful drain after <c>Complete()</c>, mirroring the FIFO and
    /// MultiQueue bindings' semantics: residual items remain reachable through the
    /// canonical wait+dequeue loop (the wait stays <c>true</c> while items remain), and
    /// once the queue is empty the wait completes <c>false</c> without cancellation and
    /// without throwing. Because this binding is exact, the residuals drain in strict
    /// key order — asserted as a SEQUENCE, where the relaxed sibling could only assert
    /// the set.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Complete_DrainsResidual_ThenWaitReturnsFalse()
    {
        // Arrange — two residual envelopes with distinct keys, then complete the queue.
        var queue = CreateEnvelopeQueue(SmallCapacity);
        await Assert.That(queue.TryEnqueue(new WorkEnvelope<int>(1, WorkClass.Default, 100))).IsTrue();
        await Assert.That(queue.TryEnqueue(new WorkEnvelope<int>(2, WorkClass.Default, 101))).IsTrue();
        queue.Complete();

        // Act + Assert — residual items drain through the canonical loop in EXACT key
        // order (stamp 100 before stamp 101 — strict sequence, not just the set).
        var drained = new List<int>();
        using var timeoutCts = new CancellationTokenSource(BindingWaitTimeout);
        while (drained.Count < 2)
        {
            var (received, envelope) = await ConsumeOneAsync(queue, timeoutCts.Token).ConfigureAwait(false);
            await Assert.That(received).IsTrue();
            drained.Add(envelope.Work);
        }

        await Assert.That(drained.SequenceEqual(new[] { 1, 2 })).IsTrue();

        // Completed AND empty: the wait reports shutdown via false — never throws, and
        // needs no cancellation to unblock.
        var signaled = await queue.WaitToDequeueAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(BindingWaitTimeout)
            .ConfigureAwait(false);
        await Assert.That(signaled).IsFalse();
    }

    /// <summary>
    /// Verifies that <c>TryEnqueue</c> after <c>Complete()</c> reports rejection via
    /// <c>false</c> (no exception), mirroring the sibling bindings' completed behavior,
    /// and that the rejection funds no wake-up permit.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TryEnqueue_AfterComplete_ReturnsFalse()
    {
        // Arrange
        var queue = CreateEnvelopeQueue(SmallCapacity);
        queue.Complete();

        // Act
        var accepted = queue.TryEnqueue(new WorkEnvelope<int>(1, WorkClass.Default, 100));

        // Assert — rejected, and the queue stays empty (no phantom item, no permit).
        await Assert.That(accepted).IsFalse();
        await Assert.That(queue.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Adapted conservation under stress for the composed (locking heap + semaphore)
    /// queue: 4 producers × 250 envelopes against 4 canonical-loop consumers — every
    /// item is received exactly once, no loss, no duplication.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Conservation_ComposedQueue_UnderStress()
    {
        // Arrange
        const int producerCount = 4;
        const int itemsPerProducer = 250;
        const int consumerCount = 4;
        const int totalItems = producerCount * itemsPerProducer;

        var queue = CreateEnvelopeQueue(totalItems);
        using var timeoutCts = new CancellationTokenSource(StressTimeout);
        using var doneCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);

        var received = new ConcurrentQueue<int>();
        var receivedCount = 0;
        long stampSource = 0;

        var producers = new Task[producerCount];
        for (var p = 0; p < producerCount; p++)
        {
            var producerIndex = p;
            producers[p] = Task.Run(async () =>
            {
                for (var i = 0; i < itemsPerProducer; i++)
                {
                    var id = (producerIndex * itemsPerProducer) + i;
                    var envelope = new WorkEnvelope<int>(
                        id,
                        (WorkClass)(id % 3), // cycle Interactive/Default/Batch
                        Interlocked.Increment(ref stampSource));
                    while (!queue.TryEnqueue(envelope))
                    {
                        if (timeoutCts.IsCancellationRequested)
                        {
                            return;
                        }

                        await Task.Yield();
                    }
                }
            });
        }

        var consumers = new Task[consumerCount];
        for (var c = 0; c < consumerCount; c++)
        {
            consumers[c] = Task.Run(async () =>
            {
                while (true)
                {
                    var (gotItem, envelope) = await ConsumeOneAsync(queue, doneCts.Token).ConfigureAwait(false);
                    if (!gotItem)
                    {
                        return; // Shutdown (all items received) or timeout.
                    }

                    received.Enqueue(envelope.Work);
                    if (Interlocked.Increment(ref receivedCount) == totalItems)
                    {
                        await doneCts.CancelAsync().ConfigureAwait(false);
                    }
                }
            });
        }

        // Act
        await Task.WhenAll(producers).ConfigureAwait(false);
        await Task.WhenAll(consumers).ConfigureAwait(false);

        // Assert — exactly-once: count matches and the distinct set is the full range.
        await Assert.That(receivedCount).IsEqualTo(totalItems);
        var distinct = new HashSet<int>(received);
        await Assert.That(distinct.Count).IsEqualTo(totalItems);
        await Assert.That(distinct.SetEquals(Enumerable.Range(0, totalItems))).IsTrue();
    }

    /// <summary>
    /// Verifies constructor validation: a non-positive capacity (the wrapper enforces
    /// bounding — the locking structure has none of its own), a null options instance,
    /// a non-positive timestamp frequency, and non-monotone admission watermarks are
    /// all rejected eagerly, matching the sibling binding's validation surface.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Constructor_InvalidArguments_Throw()
    {
        await Assert.That(() => new LockingPriorityWorkQueue<int>(
                0, new PriorityDispatchOptions(), TestTimestampFrequency))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new LockingPriorityWorkQueue<int>(
                SmallCapacity, null!, TestTimestampFrequency))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new LockingPriorityWorkQueue<int>(
                SmallCapacity, new PriorityDispatchOptions(), 0))
            .Throws<ArgumentOutOfRangeException>();

        // Watermark monotonicity (Batch ≤ Default ≤ Interactive) is validated at
        // consumption, identically to the sibling binding (DR-6).
        var batchAboveDefault = new PriorityDispatchOptions { BatchAdmissionWatermark = 0.97 };
        await Assert.That(() => new LockingPriorityWorkQueue<int>(
                WatermarkCapacity, batchAboveDefault, TestTimestampFrequency))
            .Throws<ArgumentException>();
    }

    /// <inheritdoc/>
    protected override IWorkQueue<int> CreateQueue(int capacity) =>
        new IntEnvelopeAdapter(CreateEnvelopeQueue(capacity));

    /// <summary>
    /// Creates the envelope-typed binding under test with these tests' fixed timestamp
    /// frequency (1 kHz — one tick per millisecond).
    /// </summary>
    /// <param name="capacity">The bounded capacity of the queue.</param>
    /// <param name="options">
    /// The dispatch options, or <see langword="null"/> for defaults (30 s Interactive
    /// boost, no Batch penalty, watermarks 0.90 / 0.95 / 1.0).
    /// </param>
    /// <returns>A fresh, empty queue instance.</returns>
    private static LockingPriorityWorkQueue<int> CreateEnvelopeQueue(
        int capacity,
        PriorityDispatchOptions? options = null) =>
        new(capacity, options ?? new PriorityDispatchOptions(), TestTimestampFrequency);

    /// <summary>
    /// Test-only adapter that lets the int-typed contract suite drive the
    /// envelope-typed binding: each int is wrapped into a
    /// <see cref="WorkEnvelope{TWork}"/> with <see cref="WorkClass.Interactive"/> —
    /// admitted to full hard capacity under the watermark admission policy, matching
    /// the suite's fill-to-capacity expectations — and a monotonically increasing
    /// stamp, so the priority key degenerates to enqueue order and the suite's
    /// expectations hold exactly (this binding has no rank relaxation). Count, the
    /// wait, and the dequeue are forwarded unchanged, so the contract runs against the
    /// real composed (locking heap + semaphore) queue.
    /// </summary>
    private sealed class IntEnvelopeAdapter : IWorkQueue<int>
    {
        private readonly LockingPriorityWorkQueue<int> _inner;
        private long _stamp;

        /// <summary>
        /// Initializes a new instance of the <see cref="IntEnvelopeAdapter"/> class.
        /// </summary>
        /// <param name="inner">The envelope-typed binding under test.</param>
        public IntEnvelopeAdapter(LockingPriorityWorkQueue<int> inner) => _inner = inner;

        /// <inheritdoc/>
        public int Count => _inner.Count;

        /// <inheritdoc/>
        public bool TryEnqueue(in int item) =>
            _inner.TryEnqueue(new WorkEnvelope<int>(
                item,
                WorkClass.Interactive,
                Interlocked.Increment(ref _stamp)));

        /// <inheritdoc/>
        public ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken) =>
            _inner.WaitToDequeueAsync(cancellationToken);

        /// <inheritdoc/>
        public bool TryDequeue([MaybeNullWhen(false)] out int item)
        {
            if (_inner.TryDequeue(out var envelope))
            {
                item = envelope.Work;
                return true;
            }

            item = default;
            return false;
        }
    }
}
