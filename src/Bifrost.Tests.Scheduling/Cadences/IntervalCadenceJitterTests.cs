// =============================================================================
// <copyright file="IntervalCadenceJitterTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling.Cadences;

/// <summary>
/// Tests for <see cref="IntervalCadence"/> jitter (Task 11): the
/// <see cref="IntervalCadence.WithJitter(double)"/> builder, its range validation,
/// and the symmetric bound that every jittered fire lands within
/// <c>±(Interval × Jitter)</c> of the un-jittered fire.
/// </summary>
/// <remarks>
/// Production jitter draws from the shared RNG; these tests never seed it. They
/// assert only the deterministic <em>bounds</em> over a fixed-seed loop of input
/// pairs, so the suite is reproducible without depending on the RNG draw or any
/// extra test package.
/// </remarks>
public sealed class IntervalCadenceJitterTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies <see cref="IntervalCadence.WithJitter(double)"/> returns a cadence
    /// carrying the supplied jitter fraction (and leaves the original unchanged).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WithJitter_ReturnsCadenceCarryingJitter()
    {
        var cadence = new IntervalCadence(TimeSpan.FromMinutes(5));

        var jittered = cadence.WithJitter(0.2);

        await Assert.That(jittered.Jitter).IsEqualTo(0.2);
        await Assert.That(jittered.Interval).IsEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(cadence.Jitter).IsEqualTo(0.0);
    }

    /// <summary>
    /// Verifies a negative jitter fraction is rejected.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WithJitter_NegativeFraction_Throws()
    {
        var cadence = new IntervalCadence(TimeSpan.FromMinutes(5));

        await Assert.That(() => cadence.WithJitter(-0.1))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Verifies a jitter fraction greater than 1 is rejected.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WithJitter_FractionAboveOne_Throws()
    {
        var cadence = new IntervalCadence(TimeSpan.FromMinutes(5));

        await Assert.That(() => cadence.WithJitter(1.5))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Verifies that over many samples, every jittered first-run fire lands within
    /// <c>[Interval × 0.8, Interval × 1.2]</c> relative to <c>now</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WithJitter_OverManySamples_StaysWithinSymmetricBounds()
    {
        var interval = TimeSpan.FromMinutes(5);
        const double jitter = 0.2;
        var cadence = new IntervalCadence(interval).WithJitter(jitter);

        var lower = Now + (interval * (1.0 - jitter));
        var upper = Now + (interval * (1.0 + jitter));

        for (var i = 0; i < 1000; i++)
        {
            var next = cadence.ComputeNextFire(lastFiredAt: null, now: Now);

            await Assert.That(next!.Value).IsGreaterThanOrEqualTo(lower);
            await Assert.That(next!.Value).IsLessThanOrEqualTo(upper);
        }
    }

    /// <summary>
    /// Property-style bound: across a fixed-seed sweep of <c>(interval, jitter)</c>
    /// pairs and base fires, the jittered fire never deviates from the un-jittered
    /// fire by more than <c>Interval × Jitter</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WithJitter_DeviationNeverExceedsIntervalTimesJitter()
    {
        // Deterministic input sweep (fixed seed) — NOT Random.Shared — so the test
        // is reproducible. The RNG here only varies the inputs; the assertion is a
        // bound that must hold for every RNG draw inside ComputeNextFire.
        var rng = new Random(Seed: 1234);

        for (var trial = 0; trial < 500; trial++)
        {
            var intervalMinutes = rng.Next(1, 240);
            var interval = TimeSpan.FromMinutes(intervalMinutes);
            var jitter = rng.NextDouble(); // [0, 1)

            var jittered = new IntervalCadence(interval).WithJitter(jitter);
            var unjittered = new IntervalCadence(interval); // jitter 0 → exact base

            var lastFired = Now - TimeSpan.FromMinutes(rng.Next(0, 60));

            var baseFire = unjittered.ComputeNextFire(lastFired, Now);
            var actual = jittered.ComputeNextFire(lastFired, Now);

            var deviation = (actual!.Value - baseFire!.Value).Duration();
            var maxDeviation = interval * jitter;

            // Allow one tick of slack for the long-cast truncation in offset math.
            await Assert.That(deviation)
                .IsLessThanOrEqualTo(maxDeviation + TimeSpan.FromTicks(1));
        }
    }
}
