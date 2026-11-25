// =============================================================================
// <copyright file="JobFailureIsolationTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// Tests for per-job <c>ComputeNextFire</c> fault isolation in the
/// <see cref="ScheduleTickLoop"/> (Task 48, DR-10, Hangfire#529/#530/#537):
/// a cadence that throws in <c>ComputeNextFire</c> marks only the failing job
/// <see cref="JobState.Faulted"/> and publishes a
/// <see cref="JobFireFailedEvent"/>, while all other jobs continue to fire
/// without interruption.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class JobFailureIsolationTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    // A real root provider so each fire opens (and disposes) a genuine per-fire scope
    // (F2/M2) — exercises the per-fire scope lifecycle in this fixture's tick loop.
    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that when a job's cadence throws in <c>ComputeNextFire</c>, the job
    /// is marked <see cref="JobState.Faulted"/> and a
    /// <see cref="JobFireFailedEvent"/> is published — without crashing the loop
    /// (Hangfire#529/#530).
    /// </summary>
    /// <remarks>
    /// The cadence of the job throws on the second <c>ComputeNextFire</c> call. The first call
    /// gives a valid fire instant. The second call, which <c>DispatchDueJobs</c> makes to project
    /// the next occurrence, throws. The advance triggers the first fire, and the loop must publish
    /// a <see cref="JobFireFailedEvent"/> for the faulting job. The loop must not crash
    /// (<c>IsFaulted</c> refers to the loop, not to the job).
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ComputeNextFire_Throws_JobMarkedFaulted_PublishesJobFireFailedEvent()
    {
        await using var fx = await Fixture.StartAsync();

        var failingCadence = new FailOnNextFireCadence(
            FirstNextFire: Start.AddMinutes(5),
            ThrowOnSubsequentCalls: true);
        var dispatcher = new CountingDispatcher();
        await fx.Registry.RegisterAsync(
            "faulting-job", failingCadence, MissedFirePolicy.Coalesce, dispatcher);
        await fx.Loop.WaitForIdleAsync(TestTimeout);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout);

        var failedEvents = fx.Events.OfType<JobFireFailedEvent>().ToList();
        await Assert.That(failedEvents.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(failedEvents.Any(e => e.JobName == "faulting-job")).IsTrue();

        await Assert.That(fx.Loop.IsFaulted).IsFalse();
    }

    /// <summary>
    /// Verifies that a job whose cadence throws in <c>ComputeNextFire</c> does not
    /// prevent other jobs from firing (Hangfire#537).
    /// </summary>
    /// <remarks>
    /// The cadence of the faulting job throws on the second <c>ComputeNextFire</c> call. The
    /// healthy job fires on the same interval with a different name. Both jobs are due at Start + 5
    /// min. The healthy job must fire exactly once, and the loop must still run (not globally
    /// faulted). On the next advance, the healthy job keeps firing.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ComputeNextFire_Throws_OtherJobsKeepFiring()
    {
        await using var fx = await Fixture.StartAsync();

        var failingCadence = new FailOnNextFireCadence(
            FirstNextFire: Start.AddMinutes(5),
            ThrowOnSubsequentCalls: true);
        await fx.Registry.RegisterAsync(
            "faulting-job", failingCadence, MissedFirePolicy.Coalesce, new CountingDispatcher());
        await fx.Loop.WaitForIdleAsync(TestTimeout);

        var healthy = new CountingDispatcher();
        await fx.Registry.RegisterAsync(
            "healthy-job", Cadence.Interval(TimeSpan.FromMinutes(5)), MissedFirePolicy.Coalesce, healthy);
        await fx.Loop.WaitForIdleAsync(TestTimeout);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout);

        await Assert.That(healthy.FireCount).IsEqualTo(1);

        await Assert.That(fx.Loop.IsFaulted).IsFalse();

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout);
        await Assert.That(healthy.FireCount).IsEqualTo(2);
    }

    /// <summary>
    /// A test harness owning a started <see cref="ScheduleTickLoop"/>.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly RecordingSchedulerEventSink sink;

        private Fixture(
            FakeTimeProvider time,
            ScheduleRegistry registry,
            ScheduleTickLoop loop,
            RecordingSchedulerEventSink sink)
        {
            this.Time = time;
            this.Registry = registry;
            this.Loop = loop;
            this.sink = sink;
        }

        public FakeTimeProvider Time { get; }

        public ScheduleRegistry Registry { get; }

        public ScheduleTickLoop Loop { get; }

        public IReadOnlyList<object> Events => this.sink.Published;

        public static async Task<Fixture> StartAsync()
        {
            var time = new FakeTimeProvider(Start);
            var store = new InMemoryScheduleStore();
            var registry = new ScheduleRegistry(store, time);
            var sink = new RecordingSchedulerEventSink();
            var router = new JobDispatcherRouter(sink);
            var loop = new ScheduleTickLoop(
                registry, store, time, router, sink,
                NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions(),
                serviceProvider: Services);

            var fx = new Fixture(time, registry, loop, sink);
            await ((IHostedService)loop).StartAsync(CancellationToken.None);
            await loop.WaitForIdleAsync(TestTimeout);
            return fx;
        }

        public async ValueTask DisposeAsync()
        {
            await ((IHostedService)this.Loop).StopAsync(CancellationToken.None);
            this.Loop.Dispose();
        }
    }

    /// <summary>
    /// A cadence that returns a fixed first fire instant and throws on all subsequent
    /// <c>ComputeNextFire</c> calls, simulating a cadence that becomes invalid after
    /// its first occurrence.
    /// </summary>
    private sealed record FailOnNextFireCadence(DateTimeOffset FirstNextFire, bool ThrowOnSubsequentCalls) : Cadence
    {
        private int callCount;

        // The first call returns the initial fire instant, which Arm and registration use. Later
        // calls throw to simulate a broken cadence.
        public override DateTimeOffset? ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)
        {
            var count = Interlocked.Increment(ref this.callCount);

            if (count == 1)
            {
                return FirstNextFire;
            }

            if (ThrowOnSubsequentCalls)
            {
                throw new InvalidOperationException("Cadence ComputeNextFire intentionally failed.");
            }

            return null;
        }
    }

    private sealed class CountingDispatcher : IJobDispatcher
    {
        private int fireCount;

        public int FireCount => Volatile.Read(ref this.fireCount);

        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref this.fireCount);
            return ValueTask.CompletedTask;
        }
    }
}
