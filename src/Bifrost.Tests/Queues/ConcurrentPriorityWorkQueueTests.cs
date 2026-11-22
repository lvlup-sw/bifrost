// =============================================================================
// <copyright file="ConcurrentPriorityWorkQueueTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Contract and binding-specific tests for <c>ConcurrentPriorityWorkQueue&lt;TWork&gt;</c>,
/// the MultiQueue concurrent-priority-queue binding of <see cref="IWorkQueue{T}"/>
/// (T20, DR-4/DR-5): a bounded <c>Bifrost.Concurrency.ConcurrentPriorityQueue</c> composed
/// with an item-counting <see cref="SemaphoreSlim"/> wake-up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract-suite adapter choice.</b> The binding's element type is
/// <see cref="WorkEnvelope{TWork}"/> — its priority key is computed FROM the envelope —
/// but <see cref="WorkQueueContractTests"/> is written against <see cref="IWorkQueue{T}"/>
/// with <c>T = int</c>. Rather than reshaping the abstract suite (out of scope), the
/// <see cref="CreateQueue(int)"/> override returns a thin test-only adapter
/// (<see cref="IntEnvelopeAdapter"/>) that wraps each <see cref="int"/> into a
/// <see cref="WorkEnvelope{TWork}"/> with <see cref="WorkClass.Interactive"/> — the class
/// admitted to full hard capacity under the T21 watermark admission policy (DR-6), which
/// is what the suite's fill-to-capacity expectations require; its constant boost shifts
/// every key equally — and a monotonic stamp, so contract items dequeue by plain enqueue
/// order modulo the documented MultiQueue rank relaxation — which the suite's canonical
/// consume loop already tolerates. The adapter forwards <c>Count</c>, the wait, and the
/// dequeue unchanged, so every contract property exercises the real composed binding
/// under DEFAULT dispatch options.
/// </para>
/// <para>
/// <b>Rank-relaxation handling in the ordering test.</b> The CPQ's relaxed dequeue has a
/// documented expected rank error of <c>(5/6)·n</c> (n = sub-queue count ≈ 4 × cores,
/// rounded up to a power of two). Exact drain order is only guaranteed at
/// <c>subQueueCount == 1</c>, and the CPQ's sub-queue-pinning constructor is internal to
/// <c>Bifrost.Concurrency</c> (visible to <c>Bifrost.Tests.Concurrency</c> only, not this
/// assembly), so <see cref="PriorityOrdering_QuiescentDrain_RespectsVirtualTimeKey"/>
/// asserts a rank-BOUNDED property instead of exact order: with per-class groups sized to
/// <c>max(200, 3·n)</c> items, the per-pop rank noise (≈ <c>(5/6)·n</c>) averages out over
/// a group, so the MEAN drain position of each class group must respect the virtual-time
/// key order (Interactive &lt; Default &lt; Batch) by a many-sigma margin on any plausible
/// host. The group size scales with the host's sub-queue count so the margin holds on
/// large-core machines too.
/// </para>
/// </remarks>
[InheritsTests]
public sealed class ConcurrentPriorityWorkQueueTests : WorkQueueContractTests
{
    /// <summary>
    /// Per-test ceiling for waits that are expected to complete promptly.
    /// </summary>
    private static readonly TimeSpan BindingWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Generous ceiling for the multi-producer/multi-consumer stress tests.
    /// </summary>
    private static readonly TimeSpan StressTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The timestamp frequency used by these tests: 1 000 units per second, so one
    /// timestamp tick is one millisecond and boost-window conversions stay exact.
    /// </summary>
    private const long TestTimestampFrequency = 1_000;

    /// <summary>
    /// Verifies the semaphore-counts-items invariant: each accepted enqueue releases
    /// exactly one permit (a rejected enqueue releases none), so N accepted items fund
    /// exactly N successful wait+dequeue cycles and not one more.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// <para>
    /// The test fills the queue to capacity and then shows that the over-capacity rejection funds no
    /// permit. It uses the Interactive class, which the T21 watermark policy admits to hard capacity. The
    /// class is incidental to the invariant under test.
    /// </para>
    /// <para>
    /// After exactly <c>itemCount</c> wait+dequeue cycles, there are no extra signals. A further wait
    /// must park until its token cancels, and then surface <see cref="OperationCanceledException"/> per
    /// the contract. The consume loop owns that catch, so here the test plays the role of the loop.
    /// </para>
    /// </remarks>
    [Test]
    public async Task WaitToDequeueAsync_SemaphoreCountsItems_ReleasePerEnqueue()
    {
        const int itemCount = 5;
        var queue = CreateEnvelopeQueue(itemCount);
        for (var i = 0; i < itemCount; i++)
        {
            var accepted = queue.TryEnqueue(new WorkEnvelope<int>(i, WorkClass.Interactive, 1_000 + i));
            await Assert.That(accepted).IsTrue();
        }

        var rejected = queue.TryEnqueue(new WorkEnvelope<int>(999, WorkClass.Interactive, 2_000));
        await Assert.That(rejected).IsFalse();

        using var timeoutCts = new CancellationTokenSource(BindingWaitTimeout);
        for (var i = 0; i < itemCount; i++)
        {
            var signaled = await queue.WaitToDequeueAsync(timeoutCts.Token).ConfigureAwait(false);
            await Assert.That(signaled).IsTrue();
            var dequeued = queue.TryDequeue(out _);
            await Assert.That(dequeued).IsTrue();
        }

        using var extraCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var extraSurfacedCancellation = false;
        try
        {
            _ = await queue.WaitToDequeueAsync(extraCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            extraSurfacedCancellation = true;
        }

        await Assert.That(extraSurfacedCancellation).IsTrue();
    }

    /// <summary>
    /// Verifies the canonical consume loop under live contention: relaxed
    /// <c>TryDequeue</c> misses are tolerated by looping back to the wait, and no item is
    /// lost — every produced envelope is received exactly once.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TryDequeue_SpuriousMiss_LoopsBackToWait_NoItemLost()
        => await RunComposedConservationAsync(producerCount: 2, itemsPerProducer: 100, consumerCount: 2)
            .ConfigureAwait(false);

    /// <summary>
    /// Adapted conservation under stress for the composed (CPQ + semaphore) queue:
    /// 4 producers × 250 envelopes against 4 canonical-loop consumers — every item is
    /// received exactly once, no loss, no duplication.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Conservation_ComposedQueue_UnderStress()
        => await RunComposedConservationAsync(producerCount: 4, itemsPerProducer: 250, consumerCount: 4)
            .ConfigureAwait(false);

    /// <summary>
    /// Verifies that a quiescent single-threaded drain respects the WFQ virtual-time key
    /// (DR-5) modulo the documented MultiQueue rank relaxation: a fresh Interactive item
    /// outranks an older Default item (boost), and the oldest Batch item still dispatches
    /// last (penalty). Because exact order is relaxed by the two-choice dequeue (expected
    /// rank error <c>(5/6)·n</c>) and the sub-queue-pinning constructor is not visible to
    /// this assembly, the assertion is rank-bounded: the MEAN drain position per class
    /// group must order Interactive &lt; Default &lt; Batch (see the class remarks for the
    /// statistical margin).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// <para>
    /// The group size scales with the sub-queue count of the host, so the margin of the mean position is
    /// larger than the per-pop rank noise on any core count.
    /// </para>
    /// <para>
    /// The queue has 2× headroom, so the T21 class watermarks (Batch 0.90, Default 0.95 of capacity)
    /// never trip during the interleaved fill. This test isolates dequeue-side ordering. The
    /// <c>WatermarkAdmissionTests</c> cover admission-side shedding.
    /// </para>
    /// <para>
    /// The virtual-time layout uses ticks at 1 kHz, so <c>InteractiveBoostWindow</c> is 30_000 ticks and
    /// <c>BatchPenaltyWindow</c> is 60_000 ticks. A smaller key is dequeued sooner:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Interactive has the freshest timestamps (1_520_000+i) and key 1_490_000+i (boost −30_000).</description></item>
    /// <item><description>Default has the middle timestamps (1_500_000+i) and key 1_500_000+i (no boost).</description></item>
    /// <item><description>Batch has the oldest timestamps (1_450_000+i) and key 1_510_000+i (penalty +60_000).</description></item>
    /// </list>
    /// <para>
    /// The ids are interactive [0, g), default [g, 2g), and batch [2g, 3g).
    /// </para>
    /// <para>
    /// The drain is quiescent and single-threaded. With no contention, <c>TryDequeue</c> of the CPQ
    /// reports false only on an authoritative observed-empty scan, so every call pops. At quiescence the
    /// drained set must be exactly the full range. The mean drain position per class group must respect
    /// the key order Interactive &lt; Default &lt; Batch. The relaxation noise averages out over a group
    /// sized to 3× the sub-queue count (see the class remarks).
    /// </para>
    /// </remarks>
    [Test]
    public async Task PriorityOrdering_QuiescentDrain_RespectsVirtualTimeKey()
    {
        var subQueueCount = (int)BitOperations.RoundUpToPowerOf2((uint)(4 * Environment.ProcessorCount));
        var groupSize = Math.Max(200, 3 * subQueueCount);
        var totalItems = 3 * groupSize;

        var options = new PriorityDispatchOptions
        {
            InteractiveBoostWindow = TimeSpan.FromSeconds(30),
            BatchPenaltyWindow = TimeSpan.FromSeconds(60),
        };

        var queue = CreateEnvelopeQueue(2 * totalItems, options);

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

        var drainOrder = new List<int>(totalItems);
        for (var pos = 0; pos < totalItems; pos++)
        {
            var got = queue.TryDequeue(out var envelope);
            await Assert.That(got).IsTrue();
            drainOrder.Add(envelope.Work);
        }

        await Assert.That(queue.Count).IsEqualTo(0);

        var distinct = new HashSet<int>(drainOrder);
        await Assert.That(distinct.Count).IsEqualTo(totalItems);
        await Assert.That(distinct.SetEquals(Enumerable.Range(0, totalItems))).IsTrue();

        var meanInteractive = MeanDrainPosition(drainOrder, id => id < groupSize);
        var meanDefault = MeanDrainPosition(drainOrder, id => id >= groupSize && id < 2 * groupSize);
        var meanBatch = MeanDrainPosition(drainOrder, id => id >= 2 * groupSize);

        await Assert.That(meanInteractive).IsLessThan(meanDefault);
        await Assert.That(meanDefault).IsLessThan(meanBatch);
    }

    /// <summary>
    /// Verifies the completion no-hang property: consumers parked in
    /// <c>WaitToDequeueAsync</c> on an EMPTY queue are all woken by <c>Complete()</c>
    /// (one semaphore release per registered waiter) and complete <c>false</c> — no
    /// consumer is left waiting forever.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// Three consumers park on an empty queue with a non-cancellable token. The 100 ms delay gives the
    /// consumers time to actually park on the semaphore before <c>Complete()</c> runs.
    /// </remarks>
    [Test]
    public async Task Complete_WakesParkedConsumers_NoHang()
    {
        const int consumerCount = 3;
        var queue = CreateEnvelopeQueue(SmallCapacity);
        var waits = new Task<bool>[consumerCount];
        for (var c = 0; c < consumerCount; c++)
        {
            waits[c] = queue.WaitToDequeueAsync(CancellationToken.None).AsTask();
        }

        await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        await Assert.That(waits.Any(w => w.IsCompleted)).IsFalse();

        queue.Complete();
        var results = await Task.WhenAll(waits).WaitAsync(BindingWaitTimeout).ConfigureAwait(false);

        foreach (var signaled in results)
        {
            await Assert.That(signaled).IsFalse();
        }
    }

    /// <summary>
    /// Verifies graceful drain after <c>Complete()</c>, mirroring the FIFO binding's
    /// semantics: residual items remain reachable through the canonical wait+dequeue loop
    /// (the wait stays <c>true</c> while items remain), and once the queue is empty the
    /// wait completes <c>false</c> without cancellation and without throwing.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// Priority order is relaxed, so the test asserts the set of drained items, not their order. When the
    /// queue is complete and empty, the wait reports shutdown through <c>false</c>. It never throws and
    /// needs no cancellation to unblock.
    /// </remarks>
    [Test]
    public async Task Complete_DrainsResidual_ThenWaitReturnsFalse()
    {
        var queue = CreateEnvelopeQueue(SmallCapacity);
        await Assert.That(queue.TryEnqueue(new WorkEnvelope<int>(1, WorkClass.Default, 100))).IsTrue();
        await Assert.That(queue.TryEnqueue(new WorkEnvelope<int>(2, WorkClass.Default, 101))).IsTrue();
        queue.Complete();

        var drained = new HashSet<int>();
        using var timeoutCts = new CancellationTokenSource(BindingWaitTimeout);
        while (drained.Count < 2)
        {
            var (received, envelope) = await ConsumeOneAsync(queue, timeoutCts.Token).ConfigureAwait(false);
            await Assert.That(received).IsTrue();
            drained.Add(envelope.Work);
        }

        await Assert.That(drained.SetEquals(new[] { 1, 2 })).IsTrue();

        var signaled = await queue.WaitToDequeueAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(BindingWaitTimeout)
            .ConfigureAwait(false);
        await Assert.That(signaled).IsFalse();
    }

    /// <summary>
    /// Verifies that <c>TryEnqueue</c> after <c>Complete()</c> reports rejection via
    /// <c>false</c> (no exception), mirroring the FIFO binding's completed-channel
    /// behavior, and that the rejection funds no wake-up permit.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The queue stays empty after the rejection: no phantom item and no permit.
    /// </remarks>
    [Test]
    public async Task TryEnqueue_AfterComplete_ReturnsFalse()
    {
        var queue = CreateEnvelopeQueue(SmallCapacity);
        queue.Complete();

        var accepted = queue.TryEnqueue(new WorkEnvelope<int>(1, WorkClass.Default, 100));

        await Assert.That(accepted).IsFalse();
        await Assert.That(queue.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies constructor validation: a non-positive capacity, a null options
    /// instance, and a non-positive timestamp frequency are all rejected eagerly.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Constructor_InvalidArguments_Throw()
    {
        await Assert.That(() => new ConcurrentPriorityWorkQueue<int>(
                0, new PriorityDispatchOptions(), TestTimestampFrequency))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ConcurrentPriorityWorkQueue<int>(
                SmallCapacity, null!, TestTimestampFrequency))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new ConcurrentPriorityWorkQueue<int>(
                SmallCapacity, new PriorityDispatchOptions(), 0))
            .Throws<ArgumentOutOfRangeException>();
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
    /// boost, no Batch penalty).
    /// </param>
    /// <returns>A fresh, empty queue instance.</returns>
    private static ConcurrentPriorityWorkQueue<int> CreateEnvelopeQueue(
        int capacity,
        PriorityDispatchOptions? options = null) =>
        new(capacity, options ?? new PriorityDispatchOptions(), TestTimestampFrequency);

    /// <summary>
    /// Computes the mean drain position of the items selected by
    /// <paramref name="belongsToGroup"/> within the observed drain order.
    /// </summary>
    /// <param name="drainOrder">The ids in the order they were drained.</param>
    /// <param name="belongsToGroup">Selects the ids belonging to the class group.</param>
    /// <returns>The mean zero-based drain position of the group's items.</returns>
    private static double MeanDrainPosition(List<int> drainOrder, Func<int, bool> belongsToGroup)
    {
        long positionSum = 0;
        var memberCount = 0;
        for (var pos = 0; pos < drainOrder.Count; pos++)
        {
            if (belongsToGroup(drainOrder[pos]))
            {
                positionSum += pos;
                memberCount++;
            }
        }

        return (double)positionSum / memberCount;
    }

    /// <summary>
    /// Runs the composed-queue conservation scenario: producers enqueue uniquely numbered
    /// envelopes (cycling work classes to exercise the key computation) while consumers
    /// run the canonical wait+dequeue loop; asserts every item is received exactly once.
    /// </summary>
    /// <param name="producerCount">The number of concurrent producers.</param>
    /// <param name="itemsPerProducer">The number of envelopes each producer enqueues.</param>
    /// <param name="consumerCount">The number of concurrent canonical-loop consumers.</param>
    /// <returns>A task representing the asynchronous scenario.</returns>
    /// <remarks>
    /// The producers cycle the work class through Interactive, Default, and Batch. A consumer returns when
    /// <c>ConsumeOneAsync</c> reports shutdown: all items were received, or the timeout fired.
    /// Exactly-once means that the count matches and the distinct set is the full range.
    /// </remarks>
    private static async Task RunComposedConservationAsync(
        int producerCount,
        int itemsPerProducer,
        int consumerCount)
    {
        var totalItems = producerCount * itemsPerProducer;
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
                        (WorkClass)(id % 3),
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
                        return;
                    }

                    received.Enqueue(envelope.Work);
                    if (Interlocked.Increment(ref receivedCount) == totalItems)
                    {
                        await doneCts.CancelAsync().ConfigureAwait(false);
                    }
                }
            });
        }

        await Task.WhenAll(producers).ConfigureAwait(false);
        await Task.WhenAll(consumers).ConfigureAwait(false);

        await Assert.That(receivedCount).IsEqualTo(totalItems);
        var distinct = new HashSet<int>(received);
        await Assert.That(distinct.Count).IsEqualTo(totalItems);
        await Assert.That(distinct.SetEquals(Enumerable.Range(0, totalItems))).IsTrue();
    }

    /// <summary>
    /// Test-only adapter that lets the int-typed contract suite drive the envelope-typed
    /// ordering: each int is wrapped into a <see cref="WorkEnvelope{TWork}"/> with
    /// <see cref="WorkClass.Interactive"/> — admitted to full hard capacity under the
    /// T21 watermark admission policy, matching the suite's fill-to-capacity
    /// expectations; its constant boost shifts every key by the same amount — and a
    /// monotonically increasing stamp, so the priority key degenerates to enqueue order
    /// and the suite's expectations hold modulo the documented rank relaxation (which
    /// the canonical consume loop tolerates). Count, the wait, and the dequeue are
    /// forwarded unchanged, so the contract runs against the real composed
    /// (CPQ + semaphore) queue.
    /// </summary>
    private sealed class IntEnvelopeAdapter : IWorkQueue<int>
    {
        private readonly ConcurrentPriorityWorkQueue<int> _inner;
        private long _stamp;

        /// <summary>
        /// Initializes a new instance of the <see cref="IntEnvelopeAdapter"/> class.
        /// </summary>
        /// <param name="inner">The envelope-typed binding under test.</param>
        public IntEnvelopeAdapter(ConcurrentPriorityWorkQueue<int> inner) => _inner = inner;

        /// <inheritdoc/>
        public int Count => _inner.Count;

        /// <inheritdoc/>
        public EnqueueResult Enqueue(in int item) =>
            _inner.Enqueue(new WorkEnvelope<int>(
                item,
                WorkClass.Interactive,
                Interlocked.Increment(ref _stamp)));

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
