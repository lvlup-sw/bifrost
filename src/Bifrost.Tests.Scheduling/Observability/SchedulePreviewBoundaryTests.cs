// =============================================================================
// <copyright file="SchedulePreviewBoundaryTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Observability;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Bifrost.Tests.Scheduling.Observability;

/// <summary>Checks previews against the already scheduled occurrence.</summary>
public class SchedulePreviewBoundaryTests
{
    /// <summary>Reading a preview later cannot shift the first occurrence of an unfired interval.</summary>
    [Test]
    public async Task PreviewRetainsScheduledFirstOccurrence()
    {
        var clock = new FakeTimeProvider();
        var epoch = clock.GetUtcNow();
        using var registry = new ScheduleRegistry(new InMemoryScheduleStore(), clock);
        await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(10)), MissedFirePolicy.Coalesce, Substitute.For<IJobDispatcher>()).ConfigureAwait(false);
        clock.Advance(TimeSpan.FromMinutes(1));
        var preview = new ScheduleInspector(registry).GetNextOccurrences("job", 2);
        await Assert.That(preview[0]).IsEqualTo(epoch.AddMinutes(10));
        await Assert.That(preview[1]).IsEqualTo(epoch.AddMinutes(20));
    }

    /// <summary>A future-occurrence preview cannot include an expired one-shot.</summary>
    [Test]
    public async Task PreviewExcludesPastOneShot()
    {
        var clock = new FakeTimeProvider();
        using var registry = new ScheduleRegistry(new InMemoryScheduleStore(), clock);
        await registry.RegisterAsync("job", Cadence.After(TimeSpan.FromMinutes(1)), MissedFirePolicy.SkipMissed, Substitute.For<IJobDispatcher>()).ConfigureAwait(false);
        clock.Advance(TimeSpan.FromMinutes(2));
        await Assert.That(new ScheduleInspector(registry).GetNextOccurrences("job", 2)).IsEmpty();
    }
}
