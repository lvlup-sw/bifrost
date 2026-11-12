#pragma warning disable CS1591, SA1600, SA1633

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Internal;

namespace Bifrost.Tests.Scheduling;

public sealed class CronCoalesceTests
{
    [Test]
    [Arguments("UTC")]
    [Arguments("America/New_York")]
    [Arguments("Europe/Berlin")]
    [Arguments("Australia/Lord_Howe")]
    public async Task ReverseMatchesForwardAcrossCalendarBoundaries(string zoneName)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName);
        foreach (var expression in new[] { "* * * * *", "30 1 * * *", "30 2 * * *", "0 0 L * *", "0 0 29 2 *" })
        {
            var cadence = new CronCadence(expression, zone);
            foreach (var date in new[] { "2024-02-28", "2026-03-07", "2026-03-28", "2026-04-04", "2026-10-03", "2026-10-24", "2026-10-31", "2026-12-30" })
            {
                var last = DateTimeOffset.Parse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
                var cutoff = last.AddDays(3);
                DateTimeOffset? expected = null;
                var cursor = last;
                while (cadence.ComputeNextFire(cursor, cursor) is { } next && next <= cutoff)
                {
                    expected = next;
                    cursor = next;
                }

                var actual = Compute(cadence, last, cutoff);
                await Assert.That(actual.SingleOrDefault()).IsEqualTo(expected ?? default);
                await Assert.That(actual.Count).IsEqualTo(expected.HasValue ? 1 : 0);
                if (expected is { } exact)
                {
                    await Assert.That(Compute(cadence, last, exact).Single()).IsEqualTo(exact);
                    await Assert.That(Compute(cadence, exact, exact)).IsEmpty();
                }
            }
        }
    }

    [Test]
    public async Task StartupBoundsOffsetsAndLargeBacklog()
    {
        var cadence = new CronCadence("* * * * *", null);
        var now = new DateTimeOffset(2026, 11, 10, 12, 34, 0, TimeSpan.FromHours(5));
        await Assert.That(Compute(cadence, null, now)).IsEmpty();
        await Assert.That(Compute(cadence, now.AddMinutes(1), now)).IsEmpty();
        await Assert.That(Compute(cadence, now.AddTicks(-1), now).Single()).IsEqualTo(now);
        await Assert.That(Compute(cadence, now, now.AddTicks(1))).IsEmpty();
        await Assert.That(Compute(cadence, now.AddMinutes(-1_000_000), now).Single()).IsEqualTo(now);
        await Assert.That(Compute(cadence, DateTimeOffset.MinValue, DateTimeOffset.MinValue)).IsEmpty();
        var upper = DateTimeOffset.MaxValue;
        var expected = new DateTimeOffset(9999, 12, 31, 23, 59, 0, TimeSpan.Zero);
        await Assert.That(Compute(cadence, upper.AddHours(-1), upper).Single()).IsEqualTo(expected);
        var all = MissedFirePolicyApplier.ComputeMissedFires(cadence, now.AddMinutes(-101), now, MissedFirePolicy.FireAllMissed, 100);
        await Assert.That(all.Count).IsEqualTo(100);
        await Assert.That(all[^1]).IsEqualTo(now.AddMinutes(-1));
    }

    private static IReadOnlyList<DateTimeOffset> Compute(Cadence cadence, DateTimeOffset? last, DateTimeOffset cutoff)
        => MissedFirePolicyApplier.ComputeMissedFires(cadence, last, cutoff, MissedFirePolicy.Coalesce);
}
