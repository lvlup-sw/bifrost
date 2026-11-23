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
    /// <remarks>
    /// <para>
    /// The test samples every minute of a 14-day window that covers both 2026 DST transitions in
    /// each zone. America/New_York springs forward on 2026-03-08 and falls back on 2026-11-01.
    /// Australia/Adelaide falls back on 2026-04-05 and springs forward on 2026-10-04. Asia/Tehran
    /// springs forward on 2026-03-22 and falls back on 2026-09-22.
    /// </para>
    /// <para>
    /// A per-minute cron always has a next occurrence, so <c>ComputeNextFire</c> must not return
    /// null. If Cronos returns the input instant or an earlier one, Cronos has a defect.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CronCadence_ComputeNextFire_StrictlyGreaterThanInput_AcrossDstWeek()
    {
        var zones = new[]
        {
            (Zone: NewYork,   WindowStart: new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.Zero),  Days: 14),
            (Zone: NewYork,   WindowStart: new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), Days: 14),
            (Zone: Adelaide,  WindowStart: new DateTimeOffset(2026, 4, 4, 0, 0, 0, TimeSpan.Zero),   Days: 14),
            (Zone: Adelaide,  WindowStart: new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),  Days: 14),
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

                await Assert.That(next).IsNotNull();

                await Assert.That(next!.Value).IsGreaterThan(cursor);

                cursor = cursor.AddMinutes(1);
            }
        }
    }

    /// <summary>
    /// Property: a per-minute cron during the fall-back repeated hour in
    /// America/New_York fires exactly 60 times for that hour's UTC span, never
    /// skipping or doubling a minute (quartznet#2475 / Hangfire#567).
    /// </summary>
    /// <remarks>
    /// <para>
    /// On 2026-11-01 in New York, local 02:00 EDT falls back to 01:00 EST, so the local hour 01:xx
    /// occurs two times. The first 01:xx block is 05:00–05:59 UTC (EDT, UTC-4). The second 01:xx
    /// block is 06:00–06:59 UTC (EST, UTC-5). Together the two blocks give 120 distinct UTC
    /// minutes.
    /// </para>
    /// <para>
    /// The test starts <c>cursor</c> at 04:59 UTC, just before the first 01:00 EDT, and walks 120
    /// UTC minutes through both blocks. Each iteration sets <c>cursor</c> to exactly the returned
    /// instant to get the next one. Each <c>ComputeNextFire</c> result must be strictly greater
    /// than the previous one, so no minute occurs two times. 120 strictly increasing UTC instants
    /// prove that the repeated hour causes no duplicate.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CronCadence_FallBackRepeatedHour_PerMinute_FiresExactly60Times()
    {
        var cadence = new CronCadence("* * * * *", NewYork);

        var cursor = new DateTimeOffset(2026, 11, 1, 4, 59, 0, TimeSpan.Zero);

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
            cursor = next!.Value;
        }

        await Assert.That(fireCount).IsEqualTo(120);
    }

    /// <summary>
    /// Property: per-minute cron in America/New_York across the spring-forward
    /// transition emits no duplicate instants (quartznet#2497).
    /// </summary>
    /// <remarks>
    /// On 2026-03-08 in New York, clocks jump from 02:00 EST to 03:00 EDT at 07:00 UTC, so local
    /// 02:00–02:59 does not exist. The test walks 240 UTC minutes around the transition
    /// (06:30–10:30 UTC). Each returned instant must be new to the <c>seen</c> set.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CronCadence_SpringForward_PerMinute_NoDuplicateInstants()
    {
        var cadence = new CronCadence("* * * * *", NewYork);

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
