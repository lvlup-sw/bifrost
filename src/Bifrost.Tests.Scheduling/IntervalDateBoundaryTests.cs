// =============================================================================
// <copyright file="IntervalDateBoundaryTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Internal;

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

    /// <summary>Zero-jitter Coalesce stops at the last representable local date when a positive offset exhausts the calendar before UTC now.</summary>
    [Test]
    public async Task CoalesceNearCalendarEndWithPositiveOffsetRespectsExhaustion()
    {
        var cadence = new IntervalCadence(TimeSpan.FromHours(1));
        var now = new DateTimeOffset(9999, 12, 31, 23, 0, 0, TimeSpan.Zero);

        var lastWithThreeRemaining = new DateTimeOffset(9999, 12, 31, 20, 0, 0, TimeSpan.FromHours(14));
        var coalesced = MissedFirePolicyApplier.ComputeMissedFires(cadence, lastWithThreeRemaining, now, MissedFirePolicy.Coalesce);
        var all = MissedFirePolicyApplier.ComputeMissedFires(cadence, lastWithThreeRemaining, now, MissedFirePolicy.FireAllMissed);
        await Assert.That(coalesced).HasCount(1);
        await Assert.That(coalesced[0]).IsEqualTo(new DateTimeOffset(9999, 12, 31, 23, 0, 0, TimeSpan.FromHours(14)));
        await Assert.That(coalesced[0]).IsEqualTo(all[^1]);

        var exhaustedLocal = new DateTimeOffset(9999, 12, 31, 23, 30, 0, TimeSpan.FromHours(14));
        await Assert.That(MissedFirePolicyApplier.ComputeMissedFires(cadence, exhaustedLocal, now, MissedFirePolicy.Coalesce)).IsEmpty();
        await Assert.That(MissedFirePolicyApplier.ComputeMissedFires(cadence, exhaustedLocal, now, MissedFirePolicy.FireAllMissed)).IsEmpty();
    }
}
