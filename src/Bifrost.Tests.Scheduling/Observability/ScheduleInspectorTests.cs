// =============================================================================
// <copyright file="ScheduleInspectorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Observability;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;

using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.Observability;

/// <summary>
/// Tests for <see cref="ScheduleInspector"/> and the public read-only
/// <see cref="IBifrostScheduleInspector"/> (Task 34, DR-8): job queries delegate to
/// the registry, the metrics snapshot reports current values, and the surface is
/// read-only — it exposes no mutating methods.
/// </summary>
public sealed class ScheduleInspectorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies <see cref="IBifrostScheduleInspector.GetJobs"/> delegates to the
    /// registry, returning every registered job.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GetJobs_DelegatesToRegistry()
    {
        var registry = NewRegistry();
        await registry.RegisterAsync("alpha", Interval(), MissedFirePolicy.Coalesce, new NoopDispatcher())
            .ConfigureAwait(false);
        await registry.RegisterAsync("beta", Interval(), MissedFirePolicy.Coalesce, new NoopDispatcher())
            .ConfigureAwait(false);
        IBifrostScheduleInspector inspector = new ScheduleInspector(registry);

        var jobs = inspector.GetJobs();

        await Assert.That(jobs.Select(j => j.Name)).Contains("alpha");
        await Assert.That(jobs.Select(j => j.Name)).Contains("beta");
        await Assert.That(jobs).HasCount(2);
    }

    /// <summary>
    /// Verifies <see cref="IBifrostScheduleInspector.GetJob"/> delegates to the
    /// registry for a single job, returning the descriptor or null.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GetJob_DelegatesToRegistry()
    {
        var registry = NewRegistry();
        await registry.RegisterAsync("alpha", Interval(), MissedFirePolicy.Coalesce, new NoopDispatcher())
            .ConfigureAwait(false);
        IBifrostScheduleInspector inspector = new ScheduleInspector(registry);

        var job = inspector.GetJob("alpha");
        var missing = inspector.GetJob("absent");

        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Value.Name).IsEqualTo("alpha");
        await Assert.That(missing).IsNull();
    }

    /// <summary>
    /// Verifies <see cref="IBifrostScheduleInspector.GetMetricsSnapshot"/> reports the
    /// current job count.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GetMetricsSnapshot_ReportsJobCount()
    {
        var registry = NewRegistry();
        await registry.RegisterAsync("alpha", Interval(), MissedFirePolicy.Coalesce, new NoopDispatcher())
            .ConfigureAwait(false);
        await registry.RegisterAsync("beta", Interval(), MissedFirePolicy.Coalesce, new NoopDispatcher())
            .ConfigureAwait(false);
        IBifrostScheduleInspector inspector = new ScheduleInspector(registry);

        var snapshot = inspector.GetMetricsSnapshot();

        await Assert.That(snapshot.JobCount).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies the metrics snapshot reports the recent fire count from the fire
    /// source the inspector reads.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GetMetricsSnapshot_ReportsRecentFireCount()
    {
        var registry = NewRegistry();
        var fireSource = new TickHealthMonitor();
        fireSource.RecordFireOutcome(success: true);
        fireSource.RecordFireOutcome(success: true);
        fireSource.RecordFireOutcome(success: false);
        IBifrostScheduleInspector inspector = new ScheduleInspector(registry, fireSource);

        var snapshot = inspector.GetMetricsSnapshot();

        await Assert.That(snapshot.RecentFireCount).IsEqualTo(3);
    }

    /// <summary>
    /// Verifies the inspector surface is read-only: <see cref="IBifrostScheduleInspector"/>
    /// exposes no mutating methods (Register / Unregister / Pause / Resume / Trigger).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Inspector_IsReadOnly()
    {
        var methods = typeof(IBifrostScheduleInspector)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToArray();

        foreach (var mutator in new[] { "Register", "Unregister", "Pause", "Resume", "Trigger" })
        {
            await Assert.That(methods.Any(name => name.Contains(mutator, StringComparison.Ordinal)))
                .IsFalse();
        }
    }

    private static IntervalCadence Interval() => new(TimeSpan.FromHours(1));

    private static ScheduleRegistry NewRegistry()
        => new(new InMemoryScheduleStore(), new FakeTimeProvider(Now));

    private sealed class NoopDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
            => ValueTask.CompletedTask;
    }
}
