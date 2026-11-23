// =============================================================================
// <copyright file="CronCadenceDstTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling.Cadences;

/// <summary>
/// DST-transition correctness for <see cref="CronCadence"/> (DR-2, DR-10). These
/// tests codify Cronos's existing contract across the spring-forward skipped hour
/// and the fall-back repeated hour in a real DST zone (<c>America/New_York</c>): a
/// job in the skipped window does not duplicate-fire, and a job in the repeated hour
/// does not double-fire. No production change is expected; this validates Cronos.
/// </summary>
public sealed class CronCadenceDstTests
{
    private static readonly TimeZoneInfo NewYork =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>
    /// Spring-forward (2026-03-08, clocks jump 02:00 → 03:00 EST→EDT): a daily job
    /// at 02:30 local falls in the skipped window. Cronos must not fire it twice
    /// around the transition — successive occurrences advance by a day, never
    /// repeating the skipped instant.
    /// </summary>
    /// <remarks>
    /// The expression <c>30 2 * * *</c> fires at 02:30 local every day, and local 02:30 does not
    /// exist on 2026-03-08. The test starts at 2026-03-07 12:00 UTC, before the transition day. The
    /// <c>first</c> fire is the shifted 2026-03-08 occurrence (03:00 EDT). The <c>second</c> fire
    /// must be the 2026-03-09 02:30 EDT occurrence, 23.5 hours later. The test requires a gap of
    /// 23 to 25 hours, so an extra fire later on the transition day (for example at 03:30 EDT)
    /// fails it. Then the test walks five occurrences and requires strict monotonicity, with no
    /// duplicate around the transition window.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SpringForward_SkippedHour_DoesNotDuplicateFire()
    {
        var cadence = new CronCadence("30 2 * * *", NewYork);

        var start = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero);

        var first = cadence.ComputeNextFire(lastFiredAt: null, now: start);
        await Assert.That(first).IsNotNull();

        var second = cadence.ComputeNextFire(lastFiredAt: first, now: first!.Value);
        await Assert.That(second).IsNotNull();
        await Assert.That(second!.Value).IsGreaterThan(first.Value);
        await Assert.That(second.Value - first.Value).IsGreaterThanOrEqualTo(TimeSpan.FromHours(23));
        await Assert.That(second.Value - first.Value).IsLessThanOrEqualTo(TimeSpan.FromHours(25));

        var cursor = first.Value;
        DateTimeOffset? previous = null;
        for (var i = 0; i < 5; i++)
        {
            var next = cadence.ComputeNextFire(lastFiredAt: cursor, now: cursor);
            await Assert.That(next).IsNotNull();
            if (previous is not null)
            {
                await Assert.That(next!.Value).IsGreaterThan(previous.Value);
            }

            previous = next;
            cursor = next!.Value;
        }
    }

    /// <summary>
    /// Fall-back (2026-11-01, clocks fall 02:00 → 01:00 EDT→EST): a daily job at
    /// 01:30 local would map to a repeated wall-clock instant. Cronos must emit a
    /// single occurrence for the day, not one per repeated hour.
    /// </summary>
    /// <remarks>
    /// The expression <c>30 1 * * *</c> fires at 01:30 local every day, and local 01:30 occurs two
    /// times on 2026-11-01. The test starts on 2026-10-31, the day before fall-back. The next
    /// occurrence after <c>transitionDayFire</c> must be on the following calendar day, more than
    /// 20 hours later. This proves that the repeated 01:30 instant does not fire a second time
    /// inside the repeated hour.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FallBack_RepeatedHour_DoesNotDoubleFire()
    {
        var cadence = new CronCadence("30 1 * * *", NewYork);

        var start = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        var transitionDayFire = cadence.ComputeNextFire(lastFiredAt: null, now: start);
        await Assert.That(transitionDayFire).IsNotNull();

        var afterTransition = cadence.ComputeNextFire(
            lastFiredAt: transitionDayFire,
            now: transitionDayFire!.Value);
        await Assert.That(afterTransition).IsNotNull();

        await Assert.That(afterTransition!.Value).IsGreaterThan(transitionDayFire.Value);
        await Assert.That(afterTransition.Value - transitionDayFire.Value)
            .IsGreaterThan(TimeSpan.FromHours(20));
    }

    /// <summary>
    /// Sanity check on the transition window itself: an hourly job during the
    /// fall-back repeated hour emits strictly increasing, non-duplicated instants.
    /// </summary>
    /// <remarks>
    /// The test starts at 05:30 UTC on 2026-11-01, just before the 02:00 EDT fall-back. After each
    /// occurrence, the loop moves <c>cursor</c> one second past it to request the following one.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FallBack_HourlyJob_EmitsStrictlyIncreasingInstants()
    {
        var cadence = new CronCadence("0 * * * *", NewYork);

        var cursor = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);

        DateTimeOffset? previous = null;
        for (var i = 0; i < 6; i++)
        {
            var next = cadence.ComputeNextFire(lastFiredAt: null, now: cursor);
            await Assert.That(next).IsNotNull();
            if (previous is not null)
            {
                await Assert.That(next!.Value).IsGreaterThan(previous.Value);
            }

            previous = next;
            cursor = next!.Value + TimeSpan.FromSeconds(1);
        }
    }
}
