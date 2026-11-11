// =============================================================================
// <copyright file="SchedulerBoundaryTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Bifrost.Tests.Scheduling.Registry;

/// <summary>Tests identity, floating-point, and clock sampling boundaries.</summary>
public class SchedulerBoundaryTests
{
    /// <summary>All interval construction paths reject NaN jitter.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task NaNJitterIsRejected(int path)
    {
        var cadence = new IntervalCadence(TimeSpan.FromSeconds(1));
        await Assert.That(() => path switch
        {
            0 => new IntervalCadence(TimeSpan.FromSeconds(1), double.NaN),
            1 => cadence.WithJitter(double.NaN),
            _ => cadence with { Jitter = double.NaN },
        }).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>The entire job name, including its final character, must match the identity grammar.</summary>
    [Test]
    public async Task TrailingNewlineIsRejectedBeforePersistence()
    {
        var store = new InMemoryScheduleStore();
        using var registry = new ScheduleRegistry(store, new FakeTimeProvider());
        await Assert.That(async () => await registry.RegisterAsync("job\n", new IntervalCadence(TimeSpan.FromMinutes(1)),
            MissedFirePolicy.Coalesce, Substitute.For<IJobDispatcher>()).ConfigureAwait(false)).Throws<ArgumentException>();
        await Assert.That(registry.GetJobs()).IsEmpty();
        await Assert.That(await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false)).IsEmpty();
    }

    /// <summary>A valid zero-delay cadence is resolved and validated against one clock sample.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RelativeOneShotUsesOneClockSample(bool update)
    {
        var clock = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(1) };
        using var registry = new ScheduleRegistry(new InMemoryScheduleStore(), clock);
        var dispatcher = Substitute.For<IJobDispatcher>();
        if (update)
        {
            await registry.RegisterAsync("job", new IntervalCadence(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, dispatcher).ConfigureAwait(false);
            await registry.UpdateAsync("job", Cadence.After(TimeSpan.Zero), MissedFirePolicy.Coalesce).ConfigureAwait(false);
        }
        else
        {
            await registry.RegisterAsync("job", Cadence.After(TimeSpan.Zero), MissedFirePolicy.Coalesce, dispatcher).ConfigureAwait(false);
        }

        await Assert.That(registry.GetJob("job")!.Value.NextFireAt).IsNotNull();
    }
}
