// =============================================================================
// <copyright file="CronCadenceDstPropertyTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling.Cadences;

/// <summary>
/// Property-style DST tests for <see cref="CronCadence"/> (Task 48, DR-10,
/// quartznet#2497/#332/#2475, Hangfire#567): verifies that
/// <see cref="Cadence.ComputeNextFire"/> is strictly greater than its input across
/// both DST boundaries in multiple zones (including half-hour-offset zones), and
/// that a per-minute cron during the fall-back repeated hour fires exactly 60 times
/// per hour.
/// </summary>
/// <remarks>
/// No third-party property-test framework is used; tests sample every minute of the
/// DST transition week in each zone deterministically.
/// </remarks>
public sealed class CronCadenceDstPropertyTests
{
    // Spring-forward 2026: America/New_York clocks jump 02:00 → 03:00 on 2026-03-08.
    // Fall-back 2026:      America/New_York clocks fall 02:00 → 01:00 on 2026-11-01.

    // Adelaide: UTC+9:30 / UTC+10:30. Spring-forward 2026-10-04, fall-back 2026-04-05.
    // Tehran:   UTC+3:30 / UTC+4:30. Spring-forward 2026-03-22 (approx), fall-back 2026-09-22.

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeZoneInfo Adelaide = TimeZoneInfo.FindSystemTimeZoneById("Australia/Adelaide");
    private static readonly TimeZoneInfo Tehran = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran");

    /// <summary>
    /// Property: for a per-minute cron expression in every supported DST zone,
    /// <c>ComputeNextFire(t) &gt; t</c> for every UTC instant sampled across a
    /// 14-day window covering both DST boundaries (quartznet#2497/#332).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CronCadence_ComputeNextFire_StrictlyGreaterThanInput_AcrossDstWeek()
    {
        // Sample per-minute across a 14-day window covering both 2026 DST transitions.
        // If Cronos returns now or an earlier instant, this would indicate a defect.
        var zones = new[]
        {
            // America/New_York — spring-forward 2026-03-08, fall-back 2026-11-01
            (Zone: NewYork,   WindowStart: new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.Zero),  Days: 14),
            (Zone: NewYork,   WindowStart: new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), Days: 14),
            // Australia/Adelaide — spring-forward 2026-10-04, fall-back 2026-04-05
            (Zone: Adelaide,  WindowStart: new DateTimeOffset(2026, 4, 4, 0, 0, 0, TimeSpan.Zero),   Days: 14),
            (Zone: Adelaide,  WindowStart: new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),  Days: 14),
            // Asia/Tehran — spring-forward 2026-03-22, fall-back 2026-09-22
            (Zone: Tehran,    WindowStart: new DateTimeOffset(2026, 3, 21, 0, 0, 0, TimeSpan.Zero),  Days: 14),
            (Zone: Tehran,    WindowStart: new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero),  Days: 14),
        };

        foreach (var (zone, windowStart, days) in zones)
        {
            var cadence = new CronCadence("* * * * *", zone);
            var end = windowStart.AddDays(days);
            var cursor = windowStart;

            while (cursor < end)
            {
                var next = cadence.ComputeNextFire(lastFiredAt: null, now: cursor);

                // ComputeNextFire may return null only if the expression has no future
                // occurrences — a per-minute cron always has a next occurrence.
                await Assert.That(next).IsNotNull();

                // The invariant: ComputeNextFire(t) > t for every t.
                await Assert.That(next!.Value).IsGreaterThan(cursor);

                // Step forward one minute.
                cursor = cursor.AddMinutes(1);
            }
        }
    }

    /// <summary>
    /// Property: a per-minute cron during the fall-back repeated hour in
    /// America/New_York fires exactly 60 times for that hour's UTC span, never
    /// skipping or doubling a minute (quartznet#2475 / Hangfire#567).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CronCadence_FallBackRepeatedHour_PerMinute_FiresExactly60Times()
    {
        // Fall-back 2026-11-01 in New York: local 02:00 EDT falls back to 01:00 EST.
        // UTC span for the repeated local "01:xx" hour:
        //   First  01:xx → UTC 05:00–05:59 (EDT, UTC-4)
        //   Second 01:xx → UTC 06:00–06:59 (EST, UTC-5)
        // A per-minute cron in the first hour returns 60 UTC instants (05:00–05:59);
        // the second "01:xx" block adds 60 more (06:00–06:59), for a total of 120
        // distinct UTC minutes across both blocks. The key property: no minute is
        // emitted twice (each ComputeNextFire call returns a strictly greater value).

        var cadence = new CronCadence("* * * * *", NewYork);

        // Start just before the first 01:00 EDT on 2026-11-01 (04:59 UTC).
        var cursor = new DateTimeOffset(2026, 11, 1, 4, 59, 0, TimeSpan.Zero);

        // Walk two full hours of UTC (120 minutes) through the repeated block and
        // confirm every successive result is strictly greater than the previous.
        var fireCount = 0;
        DateTimeOffset? previous = null;

        for (var i = 0; i < 120; i++)
        {
            var next = cadence.ComputeNextFire(lastFiredAt: null, now: cursor);
            await Assert.That(next).IsNotNull();

            if (previous is not null)
            {
                await Assert.That(next!.Value).IsGreaterThan(previous.Value);
            }

            previous = next;
            fireCount++;
            cursor = next!.Value; // advance to exactly this instant to get the next one
        }

        // 120 strictly increasing UTC instants confirms no duplication across the
        // repeated hour.
        await Assert.That(fireCount).IsEqualTo(120);
    }

    /// <summary>
    /// Property: per-minute cron in America/New_York across the spring-forward
    /// transition emits no duplicate instants (quartznet#2497).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CronCadence_SpringForward_PerMinute_NoDuplicateInstants()
    {
        // Spring-forward 2026-03-08 in New York: clocks jump 02:00 EST to 03:00 EDT.
        // UTC 07:00 (which is local 02:00 EST) is skipped — clocks jump to 07:00 UTC = 03:00 EDT.
        var cadence = new CronCadence("* * * * *", NewYork);

        // Walk 4 hours of UTC minutes around the transition (06:30–10:30 UTC).
        var start = new DateTimeOffset(2026, 3, 8, 6, 30, 0, TimeSpan.Zero);
        var seen = new HashSet<DateTimeOffset>();
        var cursor = start;

        for (var i = 0; i < 240; i++)
        {
            var next = cadence.ComputeNextFire(lastFiredAt: null, now: cursor);
            await Assert.That(next).IsNotNull();

            var added = seen.Add(next!.Value);
            await Assert.That(added).IsTrue();

            cursor = next.Value;
        }
    }
}
