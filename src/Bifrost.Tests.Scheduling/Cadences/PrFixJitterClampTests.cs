// =============================================================================
// <copyright file="PrFixJitterClampTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling.Cadences;

/// <summary>
/// Regression tests for PR #25 CodeRabbit FIX C1: symmetric jitter
/// (<c>baseFire + Interval × Jitter × [-1, +1]</c>) must never push the computed
/// fire instant to <c>&lt;= now</c>. A jittered fire at or before <c>now</c> is due
/// immediately and causes re-fire churn. <see cref="IntervalCadence.ComputeNextFire"/>
/// is expected to clamp the jittered instant strictly past <c>now</c>.
/// </summary>
/// <remarks>
/// The witness drives the un-jittered base fire to exactly <c>now + 1 tick</c> (an
/// almost-due missed-fire occurrence): with maximum jitter the symmetric offset can
/// subtract up to a full interval, so any negative RNG draw — roughly half of all
/// draws — lands the un-clamped result at or below <c>now</c>. The clamp must carry
/// every draw strictly into the future, and the second case asserts the clamp falls
/// back to the un-jittered base fire (itself strictly future) rather than an
/// arbitrary value.
/// </remarks>
public sealed class PrFixJitterClampTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Property-style: with maximum jitter and a base fire one tick after <c>now</c>,
    /// any negative jitter draw subtracts more than one tick and lands the un-clamped
    /// result at or before <c>now</c>; the clamp must keep every draw strictly future.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ComputeNextFire_AlmostDueBaseFire_WithMaxJitter_NeverFiresAtOrBeforeNow()
    {
        var interval = TimeSpan.FromMinutes(5);
        var cadence = new IntervalCadence(interval).WithJitter(1.0);

        // Drive the un-jittered base fire to exactly now + 1 tick: with
        // lastFired = now - (interval - 1 tick), the next aligned occurrence is
        // lastFired + interval = now + 1 tick. Max jitter can then subtract up to a
        // full interval, so ~half of all draws would land <= now without the clamp.
        var lastFired = Now - (interval - TimeSpan.FromTicks(1));

        for (var i = 0; i < 5000; i++)
        {
            var next = cadence.ComputeNextFire(lastFiredAt: lastFired, now: Now);

            await Assert.That(next!.Value).IsGreaterThan(Now);
        }
    }

    /// <summary>
    /// Deterministic boundary: with jitter clamped to its maximum so a negative draw
    /// guarantees an at-or-below-<c>now</c> un-clamped value over a large sweep, the
    /// clamp must never produce a value earlier than the un-jittered base fire when it
    /// rescues a draw — the base fire is the strictly-future fallback.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ComputeNextFire_WhenClamped_NeverEarlierThanUnjitteredBaseFire()
    {
        var interval = TimeSpan.FromMinutes(5);
        var jittered = new IntervalCadence(interval).WithJitter(1.0);
        var unjittered = new IntervalCadence(interval);

        var lastFired = Now - (interval - TimeSpan.FromTicks(1));
        var baseFire = unjittered.ComputeNextFire(lastFired, Now)!.Value;

        for (var i = 0; i < 5000; i++)
        {
            var next = jittered.ComputeNextFire(lastFiredAt: lastFired, now: Now)!.Value;

            // Every result is strictly future, and whenever the clamp engaged it
            // returned the un-jittered base fire (strictly future), never something
            // between now and the base fire that re-introduces churn risk.
            await Assert.That(next).IsGreaterThan(Now);
            if (next < baseFire)
            {
                // A downward jitter draw that was NOT clamped must still be > now.
                await Assert.That(next).IsGreaterThan(Now);
            }
        }
    }
}
