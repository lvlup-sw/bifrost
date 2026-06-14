// =============================================================================
// <copyright file="ScheduleRegistryQueryTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Registry;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

namespace Bifrost.Tests.Scheduling.Registry;

/// <summary>
/// Tests for <see cref="ScheduleRegistry"/> query APIs (Task 21, DR-1):
/// <see cref="ScheduleRegistry.GetJobs"/> (empty, sorted) and
/// <see cref="ScheduleRegistry.GetJob"/> (found, absent, snapshot semantics).
/// </summary>
public sealed class ScheduleRegistryQueryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies <see cref="ScheduleRegistry.GetJobs"/> returns empty when nothing is
    /// registered.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GetJobs_WhenEmpty_ReturnsEmpty()
    {
        var registry = new ScheduleRegistry(Substitute.For<IScheduleStore>(), new FakeTimeProvider(Now));

        var jobs = registry.GetJobs();

        await Assert.That(jobs).IsEmpty();
    }

    /// <summary>
    /// Verifies <see cref="ScheduleRegistry.GetJobs"/> returns every registered job,
    /// sorted by name regardless of registration order.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GetJobs_WithMultipleJobs_ReturnsAllSortedByName()
    {
        var registry = new ScheduleRegistry(Substitute.For<IScheduleStore>(), new FakeTimeProvider(Now));
        await RegisterAsync(registry, "charlie").ConfigureAwait(false);
        await RegisterAsync(registry, "alpha").ConfigureAwait(false);
        await RegisterAsync(registry, "bravo").ConfigureAwait(false);

        var names = registry.GetJobs().Select(j => j.Name).ToList();

        // Ordered assertion: GetJobs must return names sorted, not merely all present.
        await Assert.That(names).HasCount(3);
        await Assert.That(names[0]).IsEqualTo("alpha");
        await Assert.That(names[1]).IsEqualTo("bravo");
        await Assert.That(names[2]).IsEqualTo("charlie");
    }

    /// <summary>
    /// Verifies <see cref="ScheduleRegistry.GetJob"/> returns the descriptor for an
    /// existing job.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GetJob_Existing_ReturnsDescriptor()
    {
        var registry = new ScheduleRegistry(Substitute.For<IScheduleStore>(), new FakeTimeProvider(Now));
        await RegisterAsync(registry, "daily-report").ConfigureAwait(false);

        var job = registry.GetJob("daily-report");

        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Value.Name).IsEqualTo("daily-report");
        await Assert.That(job.Value.State).IsEqualTo(JobState.Running);
        await Assert.That(job.Value.IsRunning).IsTrue();
    }

    /// <summary>
    /// Verifies <see cref="ScheduleRegistry.GetJob"/> returns null for an absent job.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GetJob_NonExistent_ReturnsNull()
    {
        var registry = new ScheduleRegistry(Substitute.For<IScheduleStore>(), new FakeTimeProvider(Now));

        var job = registry.GetJob("missing");

        await Assert.That(job).IsNull();
    }

    /// <summary>
    /// Verifies a returned descriptor is a snapshot: mutating the registry afterward
    /// (here, pausing the job) does not change a descriptor already returned.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GetJob_ReturnsSnapshot_UnaffectedByLaterMutation()
    {
        var registry = new ScheduleRegistry(Substitute.For<IScheduleStore>(), new FakeTimeProvider(Now));
        await RegisterAsync(registry, "daily-report").ConfigureAwait(false);
        var snapshot = registry.GetJob("daily-report")!.Value;

        await registry.PauseAsync("daily-report").ConfigureAwait(false);

        // The previously returned descriptor still reflects the running state.
        await Assert.That(snapshot.State).IsEqualTo(JobState.Running);
        await Assert.That(snapshot.IsRunning).IsTrue();
        // The registry now reflects the new state.
        await Assert.That(registry.GetJob("daily-report")!.Value.State).IsEqualTo(JobState.Paused);
    }

    private static async Task RegisterAsync(ScheduleRegistry registry, string name)
        => await registry.RegisterAsync(
                name, new IntervalCadence(TimeSpan.FromHours(1)), MissedFirePolicy.Coalesce, new StubDispatcher())
            .ConfigureAwait(false);

    private sealed class StubDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
