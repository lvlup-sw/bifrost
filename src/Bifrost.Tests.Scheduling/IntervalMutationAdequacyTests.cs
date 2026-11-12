#pragma warning disable CS1591, SA1600, SA1633

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling;

public sealed class IntervalMutationAdequacyTests
{
    [Test]
    public async Task ExactNextBoundary_AdvancesOneWholeInterval()
    {
        var last = new DateTimeOffset(100, TimeSpan.Zero);
        var now = last.AddTicks(2);
        var next = new IntervalCadence(TimeSpan.FromTicks(2)).ComputeNextFire(last, now);
        await Assert.That(next!.Value.Ticks).IsEqualTo(104);
    }

    [Test]
    public async Task JitterAtTickBoundary_AlwaysRemainsStrictlyFuture()
    {
        var last = new DateTimeOffset(100, TimeSpan.Zero);
        var now = last.AddTicks(1);
        var cadence = new IntervalCadence(TimeSpan.FromTicks(2), 1);
        for (var draw = 0; draw < 1024; draw++)
        {
            var next = cadence.ComputeNextFire(last, now);
            await Assert.That(next!.Value).IsGreaterThan(now);
            await Assert.That(next.Value).IsLessThanOrEqualTo(last.AddTicks(4));
        }
    }
}
