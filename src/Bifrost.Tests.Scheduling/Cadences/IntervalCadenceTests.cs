// =============================================================================
// <copyright file="IntervalCadenceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling.Cadences;

/// <summary>
/// Tests for <see cref="IntervalCadence"/> without jitter (Task 10): first/subsequent
/// occurrence projection, strict-future advance over missed intervals, constructor
/// argument validation, and the <see cref="Cadence.Interval(TimeSpan)"/> factory.
/// </summary>
public sealed class IntervalCadenceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies the first occurrence (never fired) is one interval after now.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ComputeNextFire_FirstRun_ReturnsNowPlusInterval()
    {
        var interval = TimeSpan.FromMinutes(5);
        var cadence = new IntervalCadence(interval);

        var next = cadence.ComputeNextFire(lastFiredAt: null, now: Now);

        await Assert.That(next).IsEqualTo(Now + interval);
    }

    /// <summary>
    /// Verifies a subsequent occurrence is one interval after the last fire when
    /// that lands in the future.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ComputeNextFire_SubsequentRun_ReturnsLastFiredPlusInterval()
    {
        var interval = TimeSpan.FromMinutes(5);
        var lastFired = Now - TimeSpan.FromMinutes(1);
        var cadence = new IntervalCadence(interval);

        var next = cadence.ComputeNextFire(lastFiredAt: lastFired, now: Now);

        // lastFired + 5m = now + 4m, already strictly after now.
        await Assert.That(next).IsEqualTo(lastFired + interval);
    }

    /// <summary>
    /// Verifies the next occurrence is always strictly after now: when several
    /// intervals were missed, the cadence advances to the next aligned occurrence
    /// strictly after now (not merely <c>lastFired + interval</c>, which is in the past).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ComputeNextFire_WhenIntervalsMissed_AdvancesToStrictlyFutureAlignedOccurrence()
    {
        var interval = TimeSpan.FromMinutes(5);
        // Last fired 12 minutes ago: lastFired + 5m and + 10m are both <= now.
        var lastFired = Now - TimeSpan.FromMinutes(12);
        var cadence = new IntervalCadence(interval);

        var next = cadence.ComputeNextFire(lastFiredAt: lastFired, now: Now);

        // Aligned occurrences: -12, -7, -2, +3 minutes from now. First strict-future is +3m.
        await Assert.That(next).IsEqualTo(lastFired + (3 * interval));
        await Assert.That(next!.Value).IsGreaterThan(Now);
    }

    /// <summary>
    /// Verifies that when an aligned occurrence lands exactly on now, the cadence
    /// advances past it (the result is strictly greater than now).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ComputeNextFire_WhenAlignedOccurrenceEqualsNow_AdvancesPastIt()
    {
        var interval = TimeSpan.FromMinutes(5);
        // Last fired exactly 10 minutes ago: lastFired + 10m == now.
        var lastFired = Now - TimeSpan.FromMinutes(10);
        var cadence = new IntervalCadence(interval);

        var next = cadence.ComputeNextFire(lastFiredAt: lastFired, now: Now);

        await Assert.That(next).IsEqualTo(Now + interval);
        await Assert.That(next!.Value).IsGreaterThan(Now);
    }

    /// <summary>
    /// Verifies a zero interval is rejected at construction.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Construction_WithZeroInterval_Throws()
    {
        await Assert.That(() => new IntervalCadence(TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Verifies a negative interval is rejected at construction.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Construction_WithNegativeInterval_Throws()
    {
        await Assert.That(() => new IntervalCadence(TimeSpan.FromMinutes(-1)))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Verifies <see cref="Cadence.Interval(TimeSpan)"/> builds an
    /// <see cref="IntervalCadence"/> carrying the supplied interval.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Cadence_Interval_ReturnsIntervalCadence()
    {
        var interval = TimeSpan.FromMinutes(5);

        var cadence = Cadence.Interval(interval);

        await Assert.That(cadence).IsTypeOf<IntervalCadence>();
        await Assert.That(((IntervalCadence)cadence).Interval).IsEqualTo(interval);
    }
}
