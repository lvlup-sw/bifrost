#pragma warning disable CS1591, SA1600, SA1633

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Internal;

namespace Bifrost.Tests.Scheduling;

public sealed class IntervalInvariantTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments(17)]
    [Arguments(83)]
    [Arguments(2026)]
    public async Task NextFire_MatchesTickOracle(int seed)
    {
        var random = new Random(seed);
        for (var sample = 0; sample < 128; sample++)
        {
            var ticks = random.NextInt64(1, TimeSpan.TicksPerDay);
            var now = Epoch.AddTicks(random.NextInt64(-TimeSpan.TicksPerDay, TimeSpan.TicksPerDay));
            foreach (var last in new DateTimeOffset?[] { null, now.AddTicks(-ticks * random.Next(1, 501) - random.NextInt64(ticks)), now, now.AddTicks(ticks * 3) })
            {
                var expected = NextTicks(last?.Ticks, now.Ticks, ticks);
                var actual = new IntervalCadence(TimeSpan.FromTicks(ticks)).ComputeNextFire(last, now);
                await Assert.That(actual?.Ticks).IsEqualTo(expected);
                await Assert.That(actual > now).IsTrue();
            }
        }
    }

    [Test]
    [Arguments(99)]
    [Arguments(100)]
    [Arguments(101)]
    [Arguments(500)]
    public async Task Policies_RespectBacklogAndMetamorphisms(int count)
    {
        foreach (var ticks in new long[] { 1, 17, TimeSpan.TicksPerMinute, TimeSpan.TicksPerHour })
        {
            var cadence = new IntervalCadence(TimeSpan.FromTicks(ticks));
            var last = Epoch.AddTicks(-count * ticks - ticks / 2);
            var expected = last.AddTicks(count * ticks);
            foreach (var cap in new[] { 1, 7, 100 })
            {
                var coalesced = MissedFirePolicyApplier.ComputeMissedFires(cadence, last, Epoch, MissedFirePolicy.Coalesce, cap);
                await Assert.That(coalesced).HasCount(1);
                await Assert.That(coalesced[0]).IsEqualTo(expected);
                var all = MissedFirePolicyApplier.ComputeMissedFires(cadence, last, Epoch, MissedFirePolicy.FireAllMissed, cap);
                await Assert.That(all).HasCount(Math.Min(count, cap));
                for (var i = 0; i < all.Count; i++)
                {
                    await Assert.That(all[i]).IsEqualTo(last.AddTicks((i + 1) * ticks));
                }

                await Assert.That(MissedFirePolicyApplier.ComputeMissedFires(cadence, last, Epoch, MissedFirePolicy.SkipMissed, cap)).IsEmpty();
            }

            var extended = MissedFirePolicyApplier.ComputeMissedFires(cadence, last.AddTicks(-211 * ticks), Epoch, MissedFirePolicy.Coalesce);
            await Assert.That(extended.Single()).IsEqualTo(expected);
            var shift = TimeSpan.FromDays(37);
            var translated = MissedFirePolicyApplier.ComputeMissedFires(cadence, last + shift, Epoch + shift, MissedFirePolicy.Coalesce);
            await Assert.That(translated.Single()).IsEqualTo(expected + shift);
        }
    }

    [Test]
    public async Task NoBacklog_AllPoliciesAreEmpty()
    {
        foreach (var last in new DateTimeOffset?[] { null, Epoch.AddTicks(-9), Epoch, Epoch.AddTicks(10) })
        {
            foreach (var policy in Enum.GetValues<MissedFirePolicy>())
            {
                await Assert.That(MissedFirePolicyApplier.ComputeMissedFires(new IntervalCadence(TimeSpan.FromTicks(10)), last, Epoch, policy)).IsEmpty();
            }
        }
    }

    [Test]
    public async Task SemanticNegativeControl_CapBeforeSelectionViolatesOracle()
    {
        var last = Epoch.AddMinutes(-500);
        var faulty = Enumerable.Range(1, 100).Select(i => last.AddMinutes(i)).Last();
        var expected = new DateTimeOffset(last.Ticks + ((Epoch.Ticks - last.Ticks) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute), TimeSpan.Zero);
        await Assert.That(faulty == expected).IsFalse();
        await Assert.That(expected).IsEqualTo(Epoch);
    }

    [Test]
    public async Task FireAll_StopsAtCapWithoutReadingRemainder()
    {
        var cadence = new CountingCadence();
        var result = MissedFirePolicyApplier.ComputeMissedFires(cadence, Epoch.AddTicks(-500), Epoch, MissedFirePolicy.FireAllMissed, 7);
        await Assert.That(result).HasCount(7);
        await Assert.That(cadence.Calls).IsEqualTo(7);
    }

    [Test]
    public async Task Coalesce_CronAndOneShotUseLatestOccurrence()
    {
        var cron = MissedFirePolicyApplier.ComputeMissedFires(Cadence.Cron("* * * * *"), Epoch.AddMinutes(-500), Epoch, MissedFirePolicy.Coalesce);
        await Assert.That(cron.Single()).IsEqualTo(Epoch);
        var once = MissedFirePolicyApplier.ComputeMissedFires(Cadence.At(Epoch.AddMinutes(-5)), null, Epoch, MissedFirePolicy.Coalesce);
        await Assert.That(once.Single()).IsEqualTo(Epoch.AddMinutes(-5));
        await Assert.That(MissedFirePolicyApplier.ComputeMissedFires(Cadence.At(Epoch), Epoch, Epoch, MissedFirePolicy.Coalesce)).IsEmpty();
    }

    private static long NextTicks(long? last, long now, long interval)
    {
        if (last is null)
        {
            return checked(now + interval);
        }

        var steps = now < last.Value ? 1 : ((now - last.Value) / interval) + 1;
        return checked(last.Value + (steps * interval));
    }

    private sealed record CountingCadence : Cadence
    {
        public int Calls { get; private set; }

        public override DateTimeOffset? ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)
        {
            this.Calls++;
            return now.AddTicks(1);
        }
    }
}
