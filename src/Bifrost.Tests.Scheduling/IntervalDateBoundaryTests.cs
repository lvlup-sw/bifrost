// =============================================================================
// <copyright file="IntervalDateBoundaryTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling;

/// <summary>Tests representable date limits without overflow or invalid jitter dates.</summary>
public class IntervalDateBoundaryTests
{
    /// <summary>No representable future occurrence means exhaustion, not a scheduler fault.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExhaustedDateRangeReturnsNull(bool hasLast)
    {
        var cadence = new IntervalCadence(TimeSpan.FromTicks(2));
        var now = DateTimeOffset.MaxValue;
        await Assert.That(cadence.ComputeNextFire(hasLast ? now.AddTicks(-2) : null, now)).IsNull();
    }

    /// <summary>Even the largest valid interval cannot wrap a calendar date.</summary>
    [Test]
    public async Task OversizedIntervalIsExhausted()
    {
        await Assert.That(new IntervalCadence(TimeSpan.MaxValue).ComputeNextFire(null, DateTimeOffset.MinValue)).IsNull();
    }

    /// <summary>Jitter remains within both the future and the representable calendar.</summary>
    [Test]
    public async Task JitterNearCalendarEndRemainsRepresentable()
    {
        var now = DateTimeOffset.MaxValue.AddTicks(-100);
        var cadence = new IntervalCadence(TimeSpan.FromTicks(100), 1);
        for (var i = 0; i < 1000; i++)
        {
            var next = cadence.ComputeNextFire(null, now);
            await Assert.That(next > now).IsTrue();
            await Assert.That(next <= DateTimeOffset.MaxValue).IsTrue();
        }
    }
}
