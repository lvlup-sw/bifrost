// =============================================================================
// <copyright file="WatermarkAdmissionTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Tests for class-aware watermark admission on
/// <c>ConcurrentPriorityWorkQueue&lt;TWork&gt;</c> (T21, DR-6): bounded priority queues
/// shed the LOWEST class FIRST at admission — Batch is rejected once the approximate
/// count reaches 0.90 × capacity (default), Default at 0.95, and Interactive admits to
/// full capacity — with no eviction machinery (WRED / priority-load-shedding precedent).
/// </summary>
/// <remarks>
/// <para>
/// All scenarios here run QUIESCENT (single-threaded, no concurrent producers or
/// consumers), where <see cref="IWorkQueue{T}.Count"/> is exact by contract, so the
/// admission boundaries assert deterministically. Under concurrency the striped count
/// is approximate and watermark slop is tolerated by design — that is intentionally
/// NOT asserted here.
/// </para>
/// <para>
/// Policy rejections surface uniformly as <c>TryEnqueue == false</c> per the pinned
/// <see cref="IWorkQueue{T}"/> contract — indistinguishable from hard capacity. The
/// rejected counter / DLQ routing is T23's scope.
/// </para>
/// </remarks>
public sealed class WatermarkAdmissionTests
{
    /// <summary>
    /// The capacity used by the watermark scenarios: 100, so the default fractions
    /// land on whole-item thresholds (Batch 90, Default 95, Interactive 100).
    /// </summary>
    private const int WatermarkCapacity = 100;

    /// <summary>
    /// The timestamp frequency used by these tests: 1 000 units per second, matching
    /// the sibling binding tests.
    /// </summary>
    private const long TestTimestampFrequency = 1_000;

    /// <summary>
    /// Verifies the Batch admission watermark: with a capacity-100 queue filled to 90
    /// with Default-class items (quiescent, so the count is exact), a Batch enqueue is
    /// rejected while an Interactive enqueue still succeeds — the lowest class sheds
    /// first.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TryEnqueue_Batch_RejectedAboveBatchWatermark()
    {
        // Arrange — fill to the Batch threshold (0.90 × 100 = 90) with Default items.
        var queue = CreateEnvelopeQueue(WatermarkCapacity);
        var stamp = 0L;
        for (var i = 0; i < 90; i++)
        {
            var accepted = queue.TryEnqueue(new WorkEnvelope<int>(i, WorkClass.Default, ++stamp));
            await Assert.That(accepted).IsTrue();
        }

        await Assert.That(queue.Count).IsEqualTo(90);

        // Act
        var batchAccepted = queue.TryEnqueue(new WorkEnvelope<int>(900, WorkClass.Batch, ++stamp));
        var interactiveAccepted = queue.TryEnqueue(new WorkEnvelope<int>(901, WorkClass.Interactive, ++stamp));

        // Assert — Batch shed at its watermark; Interactive still admitted.
        await Assert.That(batchAccepted).IsFalse();
        await Assert.That(interactiveAccepted).IsTrue();
        await Assert.That(queue.Count).IsEqualTo(91);
    }

    /// <summary>
    /// Verifies the Default admission watermark: at a fill level of 95
    /// (0.95 × 100), a Default enqueue is rejected while an Interactive enqueue still
    /// succeeds.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TryEnqueue_Default_RejectedAboveDefaultWatermark()
    {
        // Arrange — ramp to 95 with Interactive items (admitted to full capacity), so
        // the fill itself never trips a watermark.
        var queue = CreateEnvelopeQueue(WatermarkCapacity);
        var stamp = 0L;
        for (var i = 0; i < 95; i++)
        {
            var accepted = queue.TryEnqueue(new WorkEnvelope<int>(i, WorkClass.Interactive, ++stamp));
            await Assert.That(accepted).IsTrue();
        }

        await Assert.That(queue.Count).IsEqualTo(95);

        // Act
        var defaultAccepted = queue.TryEnqueue(new WorkEnvelope<int>(900, WorkClass.Default, ++stamp));
        var interactiveAccepted = queue.TryEnqueue(new WorkEnvelope<int>(901, WorkClass.Interactive, ++stamp));

        // Assert
        await Assert.That(defaultAccepted).IsFalse();
        await Assert.That(interactiveAccepted).IsTrue();
        await Assert.That(queue.Count).IsEqualTo(96);
    }

    /// <summary>
    /// Verifies that Interactive work is admitted all the way to hard capacity: 100
    /// Interactive enqueues into a capacity-100 queue all succeed, and only the 101st
    /// is rejected.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TryEnqueue_Interactive_AdmittedToFullCapacity()
    {
        // Arrange
        var queue = CreateEnvelopeQueue(WatermarkCapacity);
        var stamp = 0L;

        // Act + Assert — every enqueue up to capacity is admitted.
        for (var i = 0; i < WatermarkCapacity; i++)
        {
            var accepted = queue.TryEnqueue(new WorkEnvelope<int>(i, WorkClass.Interactive, ++stamp));
            await Assert.That(accepted).IsTrue();
        }

        await Assert.That(queue.Count).IsEqualTo(WatermarkCapacity);

        // Hard capacity is the only Interactive limit.
        var overCapacity = queue.TryEnqueue(new WorkEnvelope<int>(900, WorkClass.Interactive, ++stamp));
        await Assert.That(overCapacity).IsFalse();
    }

    /// <summary>
    /// Verifies the shed order under a ramped fill: as the queue fills toward
    /// capacity, Batch starts being rejected FIRST (at 90), then Default (at 95), and
    /// Interactive only at hard capacity (100) — the WRED-style lowest-class-first
    /// shed order of DR-6.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ShedOrder_UnderPressure_BatchFirstThenDefault()
    {
        // Arrange — ramp with Interactive items; at each fill level probe Batch and
        // Default. A successful probe is immediately dequeued back out (quiescent, so
        // the dequeue is guaranteed to pop) to keep the ramp level undisturbed.
        var queue = CreateEnvelopeQueue(WatermarkCapacity);
        var stamp = 0L;
        var rejectionOrder = new List<WorkClass>();
        int? batchRejectedAt = null;
        int? defaultRejectedAt = null;

        for (var fill = 0; fill <= WatermarkCapacity; fill++)
        {
            if (batchRejectedAt is null)
            {
                if (!queue.TryEnqueue(new WorkEnvelope<int>(-1, WorkClass.Batch, ++stamp)))
                {
                    batchRejectedAt = fill;
                    rejectionOrder.Add(WorkClass.Batch);
                }
                else
                {
                    var reclaimed = queue.TryDequeue(out _);
                    await Assert.That(reclaimed).IsTrue();
                }
            }

            if (defaultRejectedAt is null)
            {
                if (!queue.TryEnqueue(new WorkEnvelope<int>(-2, WorkClass.Default, ++stamp)))
                {
                    defaultRejectedAt = fill;
                    rejectionOrder.Add(WorkClass.Default);
                }
                else
                {
                    var reclaimed = queue.TryDequeue(out _);
                    await Assert.That(reclaimed).IsTrue();
                }
            }

            if (fill < WatermarkCapacity)
            {
                var ramped = queue.TryEnqueue(new WorkEnvelope<int>(fill, WorkClass.Interactive, ++stamp));
                await Assert.That(ramped).IsTrue();
            }
        }

        // Act — at full capacity even Interactive is rejected (the hard backstop).
        var interactiveAtCapacity = queue.TryEnqueue(new WorkEnvelope<int>(-3, WorkClass.Interactive, ++stamp));

        // Assert — Batch sheds first, then Default, then (and only then) Interactive.
        await Assert.That(rejectionOrder).IsEquivalentTo(new[] { WorkClass.Batch, WorkClass.Default });
        await Assert.That(batchRejectedAt).IsEqualTo(90);
        await Assert.That(defaultRejectedAt).IsEqualTo(95);
        await Assert.That(interactiveAtCapacity).IsFalse();
    }

    /// <summary>
    /// Verifies the watermark fractions are configurable on
    /// <see cref="PriorityDispatchOptions"/> with defaults 0.90 / 0.95 / 1.0, and that
    /// lowering the Batch fraction to 0.5 shifts the Batch rejection point to a count
    /// of 50.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Watermarks_Configurable()
    {
        // Defaults — 0.90 / 0.95 / 1.0 per DR-6.
        var defaults = new PriorityDispatchOptions();
        await Assert.That(defaults.BatchAdmissionWatermark).IsEqualTo(0.90);
        await Assert.That(defaults.DefaultAdmissionWatermark).IsEqualTo(0.95);
        await Assert.That(defaults.InteractiveAdmissionWatermark).IsEqualTo(1.0);

        // Arrange — Batch watermark lowered to 0.5: rejection point moves to 50.
        var options = new PriorityDispatchOptions { BatchAdmissionWatermark = 0.5 };
        var queue = CreateEnvelopeQueue(WatermarkCapacity, options);
        var stamp = 0L;
        for (var i = 0; i < 49; i++)
        {
            var accepted = queue.TryEnqueue(new WorkEnvelope<int>(i, WorkClass.Default, ++stamp));
            await Assert.That(accepted).IsTrue();
        }

        // Act + Assert — Batch admitted at 49 (below the shifted threshold), rejected at 50.
        var batchAtBoundary = queue.TryEnqueue(new WorkEnvelope<int>(900, WorkClass.Batch, ++stamp));
        await Assert.That(batchAtBoundary).IsTrue();
        await Assert.That(queue.Count).IsEqualTo(50);

        var batchAboveBoundary = queue.TryEnqueue(new WorkEnvelope<int>(901, WorkClass.Batch, ++stamp));
        await Assert.That(batchAboveBoundary).IsFalse();

        // The other class thresholds are unaffected by the Batch override.
        var defaultStillAdmitted = queue.TryEnqueue(new WorkEnvelope<int>(902, WorkClass.Default, ++stamp));
        await Assert.That(defaultStillAdmitted).IsTrue();
    }

    /// <summary>
    /// Verifies watermark validation: fractions must lie in (0, 1] — rejected at
    /// assignment, matching the options' throwing-setter style — and must be monotone
    /// non-decreasing with class urgency (Batch ≤ Default ≤ Interactive), with
    /// violations thrown when the options are validated at queue construction (the
    /// cross-property relation cannot be a single-setter guard without
    /// order-of-assignment traps).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Watermarks_Validation()
    {
        // Range (0, 1]: zero, negative, and above-one fractions are rejected at assignment.
        await Assert.That(() => new PriorityDispatchOptions { BatchAdmissionWatermark = 0.0 })
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new PriorityDispatchOptions { DefaultAdmissionWatermark = -0.1 })
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new PriorityDispatchOptions { InteractiveAdmissionWatermark = 1.5 })
            .Throws<ArgumentOutOfRangeException>();

        // Monotonicity Batch ≤ Default ≤ Interactive: violations throw when the
        // consuming queue validates the options at construction.
        var batchAboveDefault = new PriorityDispatchOptions { BatchAdmissionWatermark = 0.97 };
        await Assert.That(() => new ConcurrentPriorityWorkQueue<int>(
                WatermarkCapacity, batchAboveDefault, TestTimestampFrequency))
            .Throws<ArgumentException>();

        var defaultAboveInteractive = new PriorityDispatchOptions { InteractiveAdmissionWatermark = 0.92 };
        await Assert.That(() => new ConcurrentPriorityWorkQueue<int>(
                WatermarkCapacity, defaultAboveInteractive, TestTimestampFrequency))
            .Throws<ArgumentException>();

        // A monotone non-default configuration is accepted.
        var monotone = new PriorityDispatchOptions
        {
            BatchAdmissionWatermark = 0.5,
            DefaultAdmissionWatermark = 0.75,
            InteractiveAdmissionWatermark = 1.0,
        };
        var queue = new ConcurrentPriorityWorkQueue<int>(
            WatermarkCapacity, monotone, TestTimestampFrequency);
        await Assert.That(queue.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies the explicit <see cref="double.NaN"/> rejection in the watermark
    /// validator (DR-6): <c>NaN</c> fails every ordering comparison, so the bare
    /// <c>ThrowIfNegativeOrZero</c> / <c>ThrowIfGreaterThan</c> pair would let it slip
    /// through — each of the three watermark setters must throw
    /// <see cref="ArgumentOutOfRangeException"/> on assignment of <c>NaN</c>. This
    /// covers the explicit-<c>NaN</c> guard arm distinct from the (0, 1] range arms
    /// asserted by <see cref="Watermarks_Validation"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Watermarks_RejectNaN()
    {
        // Each setter routes through ThrowIfNotInUnitInterval, whose explicit NaN guard
        // must fire before the ordering checks (which NaN would silently pass).
        await Assert.That(() => new PriorityDispatchOptions { BatchAdmissionWatermark = double.NaN })
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new PriorityDispatchOptions { DefaultAdmissionWatermark = double.NaN })
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new PriorityDispatchOptions { InteractiveAdmissionWatermark = double.NaN })
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Verifies the boundary is exact at quiescence: with the count at 89 a Batch
    /// enqueue is admitted (89 &lt; 90), and at 90 it is rejected — the approximate
    /// striped count is exact with no concurrent operations, so no slop appears.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Watermark_QuiescentExactness()
    {
        // Arrange — fill to 89 with Default items.
        var queue = CreateEnvelopeQueue(WatermarkCapacity);
        var stamp = 0L;
        for (var i = 0; i < 89; i++)
        {
            var accepted = queue.TryEnqueue(new WorkEnvelope<int>(i, WorkClass.Default, ++stamp));
            await Assert.That(accepted).IsTrue();
        }

        await Assert.That(queue.Count).IsEqualTo(89);

        // Act + Assert — count == 89 admits Batch (89 < 90) ...
        var batchAtBoundary = queue.TryEnqueue(new WorkEnvelope<int>(900, WorkClass.Batch, ++stamp));
        await Assert.That(batchAtBoundary).IsTrue();
        await Assert.That(queue.Count).IsEqualTo(90);

        // ... and count == 90 rejects it (90 >= 90), exactly at the boundary.
        var batchAboveBoundary = queue.TryEnqueue(new WorkEnvelope<int>(901, WorkClass.Batch, ++stamp));
        await Assert.That(batchAboveBoundary).IsFalse();
        await Assert.That(queue.Count).IsEqualTo(90);
    }

    /// <summary>
    /// Creates the envelope-typed binding under test with these tests' fixed timestamp
    /// frequency (1 kHz — one tick per millisecond).
    /// </summary>
    /// <param name="capacity">The bounded capacity of the queue.</param>
    /// <param name="options">
    /// The dispatch options, or <see langword="null"/> for defaults (watermarks
    /// 0.90 / 0.95 / 1.0).
    /// </param>
    /// <returns>A fresh, empty queue instance.</returns>
    private static ConcurrentPriorityWorkQueue<int> CreateEnvelopeQueue(
        int capacity,
        PriorityDispatchOptions? options = null) =>
        new(capacity, options ?? new PriorityDispatchOptions(), TestTimestampFrequency);
}
