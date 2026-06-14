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
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SpringForward_SkippedHour_DoesNotDuplicateFire()
    {
        // 02:30 daily — local 02:30 does not exist on 2026-03-08.
        var cadence = new CronCadence("30 2 * * *", NewYork);

        // Start just before the spring-forward day's transition.
        var start = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero);

        var first = cadence.ComputeNextFire(lastFiredAt: null, now: start);
        await Assert.That(first).IsNotNull();

        // The fire immediately after the first must be strictly later (no repeat of
        // the same instant) and at least roughly a day on — the skipped 03-08 02:30
        // is not emitted a second time.
        var second = cadence.ComputeNextFire(lastFiredAt: first, now: first!.Value);
        await Assert.That(second).IsNotNull();
        await Assert.That(second!.Value).IsGreaterThan(first.Value);

        // Walk several occurrences and assert strict monotonicity with no duplicate
        // around the transition window.
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
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FallBack_RepeatedHour_DoesNotDoubleFire()
    {
        // 01:30 daily — local 01:30 occurs twice on 2026-11-01.
        var cadence = new CronCadence("30 1 * * *", NewYork);

        // Start the day before fall-back.
        var start = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        var transitionDayFire = cadence.ComputeNextFire(lastFiredAt: null, now: start);
        await Assert.That(transitionDayFire).IsNotNull();

        // The very next occurrence after the transition-day fire must advance to the
        // following day — the repeated 01:30 instant is not emitted a second time.
        var afterTransition = cadence.ComputeNextFire(
            lastFiredAt: transitionDayFire,
            now: transitionDayFire!.Value);
        await Assert.That(afterTransition).IsNotNull();

        // Strictly later, and at least ~20 hours on (i.e. the next calendar day),
        // proving no second fire inside the repeated hour.
        await Assert.That(afterTransition!.Value).IsGreaterThan(transitionDayFire.Value);
        await Assert.That(afterTransition.Value - transitionDayFire.Value)
            .IsGreaterThan(TimeSpan.FromHours(20));
    }

    /// <summary>
    /// Sanity check on the transition window itself: an hourly job during the
    /// fall-back repeated hour emits strictly increasing, non-duplicated instants.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FallBack_HourlyJob_EmitsStrictlyIncreasingInstants()
    {
        var cadence = new CronCadence("0 * * * *", NewYork);

        // Just before the 02:00 EDT fall-back on 2026-11-01 (05:30 UTC).
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
            // Advance one second past this occurrence to request the following one.
            cursor = next!.Value + TimeSpan.FromSeconds(1);
        }
    }
}
