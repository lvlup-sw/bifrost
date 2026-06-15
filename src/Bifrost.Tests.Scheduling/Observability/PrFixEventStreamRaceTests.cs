// =============================================================================
// <copyright file="PrFixEventStreamRaceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Observability;

namespace Bifrost.Tests.Scheduling.Observability;

/// <summary>
/// Regression tests for PR #25 CodeRabbit FIX B3: <see cref="SchedulerEventStream"/>'s
/// Subscribe/Dispose race. A <c>Subscribe</c> that loses the race against <c>Dispose</c>
/// must not register a live subscription that never receives events and never completes;
/// instead it returns an already-completed stream so its enumeration finishes promptly.
/// </summary>
[ParallelLimiter<TickEngine.TickEngineParallelLimit>]
public sealed class PrFixEventStreamRaceTests
{
    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that calling <c>Subscribe</c> AFTER the stream is disposed returns a
    /// stream that completes (its enumeration finishes) rather than hanging forever on
    /// a channel that will never be completed by <c>Dispose</c>. The enumeration is
    /// awaited with a timeout so a hang fails the test deterministically rather than
    /// wedging the run.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SubscribeAfterDispose_StreamCompletes_DoesNotHang()
    {
        var stream = new SchedulerEventStream();
        stream.Dispose();

        // Subscribe after dispose: must yield a completed stream, not a live-but-orphan
        // subscription whose channel is never completed.
        var enumeration = DrainAsync(stream);

        // If Subscribe added a never-completed channel, this enumeration would hang; the
        // timeout converts a hang into a deterministic failure.
        var count = await enumeration.WaitAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies the inverse ordering is also safe: a subscription taken BEFORE dispose is
    /// completed by <c>Dispose</c>, so its enumeration also finishes promptly.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SubscribeBeforeDispose_DisposeCompletesStream_DoesNotHang()
    {
        var stream = new SchedulerEventStream();
        var enumeration = DrainAsync(stream);

        // Dispose should complete every live subscriber channel so the in-flight
        // enumeration ends.
        stream.Dispose();

        var count = await enumeration.WaitAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(count).IsEqualTo(0);
    }

    private static async Task<int> DrainAsync(SchedulerEventStream stream)
    {
        var count = 0;
        await foreach (var _ in stream.Subscribe<JobFiredEvent>().ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }
}
