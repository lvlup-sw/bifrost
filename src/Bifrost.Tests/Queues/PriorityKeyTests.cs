// =============================================================================
// <copyright file="PriorityKeyTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Tests for <see cref="PriorityKey"/> and <see cref="PriorityDispatchOptions"/> (T19, DR-5):
/// the WFQ-style virtual-time priority key where
/// <c>effectivePriority = EnqueuedAtTicks − Boost(Class)</c> and smaller = sooner.
/// </summary>
/// <remarks>
/// <para>
/// The consequences under test, per design DR-5:
/// (a) same-class items preserve FIFO order;
/// (b) an Interactive item jumps ahead of Default/Batch items enqueued up to the boost
/// window earlier;
/// (c) a Batch item older than the boost window outranks a fresh Interactive item — the
/// starvation bound is the window itself, by construction;
/// (d) Default has zero boost; Batch is unboosted by default (penalty window default 0).
/// </para>
/// <para>
/// <see cref="WorkEnvelope{TWork}.EnqueuedAtTicks"/> is in
/// <see cref="TimeProvider.TimestampFrequency"/> units — NOT necessarily
/// <see cref="TimeSpan"/> ticks — so tests exercise multiple explicit synthetic
/// frequencies plus <see cref="FakeTimeProvider"/>'s own frequency.
/// </para>
/// </remarks>
public class PriorityKeyTests
{
    /// <summary>
    /// A synthetic frequency matching <see cref="TimeSpan.TicksPerSecond"/> (10 MHz),
    /// the typical normalized Windows QPC resolution.
    /// </summary>
    private const long TimeSpanTickFrequency = 10_000_000L;

    /// <summary>
    /// A synthetic 1 GHz (nanosecond) frequency, the typical Linux monotonic-clock
    /// resolution.
    /// </summary>
    private const long NanosecondFrequency = 1_000_000_000L;

    /// <summary>
    /// Verifies consequence (a): two items of the same class receive keys ordered by
    /// their enqueue timestamps — FIFO order is preserved within a class because the
    /// class boost is a constant offset.
    /// </summary>
    /// <param name="workClass">The work class shared by both items.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(WorkClass.Interactive)]
    [Arguments(WorkClass.Default)]
    [Arguments(WorkClass.Batch)]
    public async Task Compute_SameClass_PreservesFifoOrder(WorkClass workClass)
    {
        // Arrange
        var boosts = PriorityKey.Precompute(new PriorityDispatchOptions(), TimeSpanTickFrequency);
        var earlier = new WorkEnvelope<string>("first", workClass, 1_000_000_000L);
        var later = new WorkEnvelope<string>("second", workClass, 1_000_000_001L);

        // Act
        var earlierKey = PriorityKey.Compute(in earlier, in boosts);
        var laterKey = PriorityKey.Compute(in later, in boosts);

        // Assert — smaller = sooner, so the earlier item must hold the smaller key.
        await Assert.That(earlierKey).IsLessThan(laterKey);
    }

    /// <summary>
    /// Verifies consequence (b): an Interactive item enqueued up to (but within) the
    /// boost window AFTER Default and Batch items still receives a smaller key, i.e.
    /// it jumps ahead of work enqueued up to <c>InteractiveBoostWindow</c> earlier.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Compute_Interactive_JumpsBoostWindowAhead()
    {
        // Arrange — default 30s window at 10 MHz = 300,000,000 boost ticks.
        var boosts = PriorityKey.Precompute(new PriorityDispatchOptions(), TimeSpanTickFrequency);
        const long BoostTicks = 30L * TimeSpanTickFrequency;
        const long BaseTicks = 5_000_000_000L;

        var olderDefault = new WorkEnvelope<string>("default", WorkClass.Default, BaseTicks);
        var olderBatch = new WorkEnvelope<string>("batch", WorkClass.Batch, BaseTicks);

        // Enqueued one timestamp unit INSIDE the boost window after the older items.
        var freshInteractive = new WorkEnvelope<string>(
            "interactive", WorkClass.Interactive, BaseTicks + BoostTicks - 1);

        // Act
        var defaultKey = PriorityKey.Compute(in olderDefault, in boosts);
        var batchKey = PriorityKey.Compute(in olderBatch, in boosts);
        var interactiveKey = PriorityKey.Compute(in freshInteractive, in boosts);

        // Assert — the interactive item outranks both older items.
        await Assert.That(interactiveKey).IsLessThan(defaultKey);
        await Assert.That(interactiveKey).IsLessThan(batchKey);
    }

    /// <summary>
    /// Verifies consequence (c), the starvation bound: a Batch item that has waited
    /// LONGER than the boost window outranks a freshly enqueued Interactive item.
    /// Aging is inherent in the key construction — no decrease-key or re-scoring.
    /// Uses <see cref="FakeTimeProvider"/> end to end: its timestamps and its
    /// <see cref="TimeProvider.TimestampFrequency"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Compute_BatchOlderThanBoostWindow_OutranksFreshInteractive()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var options = new PriorityDispatchOptions();
        var boosts = PriorityKey.Precompute(options, fakeTime.TimestampFrequency);

        var batch = new WorkEnvelope<string>("batch", WorkClass.Batch, fakeTime.GetTimestamp());

        // Age the batch item past the interactive boost window, then enqueue interactive.
        fakeTime.Advance(options.InteractiveBoostWindow + TimeSpan.FromMilliseconds(1));
        var freshInteractive = new WorkEnvelope<string>(
            "interactive", WorkClass.Interactive, fakeTime.GetTimestamp());

        // Act
        var batchKey = PriorityKey.Compute(in batch, in boosts);
        var interactiveKey = PriorityKey.Compute(in freshInteractive, in boosts);

        // Assert — the over-aged batch item wins: starvation bounded by construction.
        await Assert.That(batchKey).IsLessThan(interactiveKey);
    }

    /// <summary>
    /// Verifies consequence (d), Default half: the Default class has zero boost, so its
    /// key is exactly its enqueue timestamp at any frequency.
    /// </summary>
    /// <param name="frequency">The timestamp frequency to precompute against.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(TimeSpanTickFrequency)]
    [Arguments(NanosecondFrequency)]
    public async Task Compute_DefaultClass_ZeroBoost(long frequency)
    {
        // Arrange
        var boosts = PriorityKey.Precompute(new PriorityDispatchOptions(), frequency);
        const long EnqueuedAt = 123_456_789L;
        var envelope = new WorkEnvelope<string>("default", WorkClass.Default, EnqueuedAt);

        // Act
        var key = PriorityKey.Compute(in envelope, in boosts);

        // Assert — key equals the raw timestamp: zero boost, no scaling applied.
        await Assert.That(key).IsEqualTo(EnqueuedAt);
    }

    /// <summary>
    /// Verifies consequence (d), Batch half: with a configured positive
    /// <see cref="PriorityDispatchOptions.BatchPenaltyWindow"/>, the Batch boost is
    /// negative — the key GROWS by exactly the penalty ticks, deferring batch work.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Compute_BatchPenaltyWindowConfigured_AddsPenaltyTicks()
    {
        // Arrange — 5s penalty at 10 MHz = 50,000,000 penalty ticks.
        var options = new PriorityDispatchOptions
        {
            BatchPenaltyWindow = TimeSpan.FromSeconds(5),
        };
        var boosts = PriorityKey.Precompute(options, TimeSpanTickFrequency);
        const long EnqueuedAt = 9_000_000_000L;
        const long PenaltyTicks = 5L * TimeSpanTickFrequency;
        var envelope = new WorkEnvelope<string>("batch", WorkClass.Batch, EnqueuedAt);

        // Act
        var key = PriorityKey.Compute(in envelope, in boosts);

        // Assert — timestamp − (−penalty) = timestamp + penalty.
        await Assert.That(key).IsEqualTo(EnqueuedAt + PenaltyTicks);
    }

    /// <summary>
    /// Verifies the hot path is pure long arithmetic: after a one-time precompute,
    /// repeated <see cref="PriorityKey.Compute{TWork}"/> calls with identical inputs
    /// yield bit-identical keys matching a pure-integer expectation, across the
    /// synthetic 10 MHz and 1 GHz frequencies plus <see cref="FakeTimeProvider"/>'s
    /// frequency — no per-call floating-point conversion, no drift.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Compute_HotPath_NoFloatingPointDrift()
    {
        // Arrange — the default 30s window is a whole number of seconds, so the exact
        // boost is expressible in pure long arithmetic as 30 × frequency.
        var frequencies = new[]
        {
            TimeSpanTickFrequency,
            NanosecondFrequency,
            new FakeTimeProvider().TimestampFrequency,
        };
        const long EnqueuedAt = 7_777_777_777L;
        const int Iterations = 10_000;

        foreach (var frequency in frequencies)
        {
            var boosts = PriorityKey.Precompute(new PriorityDispatchOptions(), frequency);
            var envelope = new WorkEnvelope<string>("hot", WorkClass.Interactive, EnqueuedAt);
            var expected = EnqueuedAt - (30L * frequency);

            // Act — hammer the hot path; every call must produce the identical key.
            var first = PriorityKey.Compute(in envelope, in boosts);
            var allIdentical = true;
            for (var i = 0; i < Iterations; i++)
            {
                if (PriorityKey.Compute(in envelope, in boosts) != first)
                {
                    allIdentical = false;
                    break;
                }
            }

            // Assert
            await Assert.That(first).IsEqualTo(expected);
            await Assert.That(allIdentical).IsTrue();
        }
    }

    /// <summary>
    /// Verifies the documented option defaults: a 30-second interactive boost window
    /// and a zero batch penalty window (batch is unboosted, not penalized, by default).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Options_Defaults_Interactive30s_BatchZero()
    {
        // Arrange & Act
        var options = new PriorityDispatchOptions();

        // Assert
        await Assert.That(options.InteractiveBoostWindow).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(options.BatchPenaltyWindow).IsEqualTo(TimeSpan.Zero);
    }

    /// <summary>
    /// Verifies that a negative <see cref="PriorityDispatchOptions.InteractiveBoostWindow"/>
    /// is rejected at assignment.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Options_NegativeInteractiveBoostWindow_Throws()
    {
        // Act & Assert
        await Assert.That(() => new PriorityDispatchOptions
        {
            InteractiveBoostWindow = TimeSpan.FromSeconds(-1),
        }).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Verifies that a negative <see cref="PriorityDispatchOptions.BatchPenaltyWindow"/>
    /// is rejected at assignment.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Options_NegativeBatchPenaltyWindow_Throws()
    {
        // Act & Assert
        await Assert.That(() => new PriorityDispatchOptions
        {
            BatchPenaltyWindow = TimeSpan.FromSeconds(-1),
        }).Throws<ArgumentOutOfRangeException>();
    }
}
