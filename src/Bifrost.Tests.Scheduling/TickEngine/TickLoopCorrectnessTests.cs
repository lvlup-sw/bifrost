// =============================================================================
// <copyright file="TickLoopCorrectnessTests.cs" company="Levelup Software">
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
/// Deterministic correctness-by-construction tests for the command path in
/// <see cref="ScheduleTickLoop"/>: registration-scoped command handling, fault
/// isolation on <c>TriggerAsync</c>, idle-barrier command ordering, and restart
/// backoff isolation from ordinary commands.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class TickLoopCorrectnessTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that a queued <see cref="RegistryCommandKind.Trigger"/> does not fire
    /// a job that was paused before the trigger drained, and that a subsequent trigger
    /// while paused also does not dispatch.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TriggerOnPausedJob_DoesNotDispatch()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        var sink = new RecordingSchedulerEventSink();
        var router = new JobDispatcherRouter(sink);
        var dispatcher = new CountingDispatcher();

        await registry.RegisterAsync(
                "job",
                Cadence.Interval(TimeSpan.FromHours(1)),
                MissedFirePolicy.Coalesce,
                dispatcher)
            .ConfigureAwait(false);
        await registry.TriggerAsync("job").ConfigureAwait(false);
        await registry.PauseAsync("job").ConfigureAwait(false);

        var loop = new ScheduleTickLoop(
            registry, store, time, router, sink,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions(),
            serviceProvider: Services);

        await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Triggering while already paused also must not dispatch.
            await registry.TriggerAsync("job").ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await Assert.That(dispatcher.FireCount).IsEqualTo(0);
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
        }
    }

    /// <summary>
    /// Verifies that <see cref="IScheduleRegistry.TriggerAsync"/> on a job whose cadence
    /// throws in <c>ComputeNextFire</c> routes through <c>SafeComputeNextFire</c>: marks
    /// only that job <see cref="JobState.Faulted"/>, publishes
    /// <see cref="JobFireFailedEvent"/>, does not crash the tick loop, and allows other
    /// jobs to continue firing.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TriggerWithThrowingCadence_IsolatesJobFault_LoopContinues()
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

        await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            var throwingDispatcher = new CountingDispatcher();
            var healthyDispatcher = new CountingDispatcher();

            await registry.RegisterAsync(
                    "throwing-job",
                    new NullThenThrowCadence(),
                    MissedFirePolicy.Coalesce,
                    throwingDispatcher)
                .ConfigureAwait(false);
            await registry.RegisterAsync(
                    "healthy-job",
                    Cadence.Interval(TimeSpan.FromMinutes(5)),
                    MissedFirePolicy.Coalesce,
                    healthyDispatcher)
                .ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await registry.TriggerAsync("throwing-job").ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await Assert.That(loop.IsFaulted).IsFalse();
            await Assert.That(throwingDispatcher.FireCount).IsEqualTo(0);
            await Assert.That(registry.GetJob("throwing-job")!.Value.State).IsEqualTo(JobState.Faulted);
            await Assert.That(sink.Published.OfType<JobFireFailedEvent>().Any(e => e.JobName == "throwing-job"))
                .IsTrue();

            time.Advance(TimeSpan.FromMinutes(5));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            await Assert.That(healthyDispatcher.FireCount).IsEqualTo(1);
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
        }
    }

    /// <summary>
    /// Verifies that <see cref="ScheduleTickLoop.WaitForIdleAsync"/> does not release
    /// prematurely when a command is posted on the tick thread after <c>DrainCommands</c>
    /// and before <c>ReleaseIdleBarrier</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WaitForIdle_CommandPostedDuringDispatchBeforePark_DrainedBeforeIdleReturns()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        var triggeredDispatcher = new CountingDispatcher();
        var primaryDispatcher = new CountingDispatcher();

        var sink = new TriggerOnFirstFireSink(registry, "primary", "secondary");
        var router = new JobDispatcherRouter(sink);
        var loop = new ScheduleTickLoop(
            registry, store, time, router, sink,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions(),
            serviceProvider: Services);

        await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await registry.RegisterAsync(
                    "primary",
                    Cadence.Interval(TimeSpan.FromMinutes(1)),
                    MissedFirePolicy.Coalesce,
                    primaryDispatcher)
                .ConfigureAwait(false);
            await registry.RegisterAsync(
                    "secondary",
                    Cadence.Interval(TimeSpan.FromHours(1)),
                    MissedFirePolicy.Coalesce,
                    triggeredDispatcher)
                .ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            time.Advance(TimeSpan.FromMinutes(1));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await Assert.That(primaryDispatcher.FireCount).IsEqualTo(1);
            await Assert.That(triggeredDispatcher.FireCount).IsEqualTo(1);
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
        }
    }

    /// <summary>
    /// Verifies that ordinary registry commands (such as <c>RegisterAsync</c> and
    /// <c>TriggerAsync</c>) cannot wake the loop early out of <c>RestartBackoff</c>:
    /// the triggered job fires only once the fake clock advances across the backoff.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task OrdinaryCommandDuringRestartBackoff_DoesNotBypassBackoff()
    {
        var backoff = TimeSpan.FromMinutes(5);
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        var sink = new FaultThenRecoverSink(throwCount: 2);
        var router = new JobDispatcherRouter(sink);
        var loop = new ScheduleTickLoop(
            registry, store, time, router, sink,
            NullLogger<ScheduleTickLoop>.Instance,
            new SchedulerOptions
            {
                RestartBackoff = backoff,
                MaxRestartsInWindow = 10,
                RestartWindow = TimeSpan.FromHours(1),
            },
            serviceProvider: Services);

        await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await registry.RegisterAsync(
                    "faulting",
                    Cadence.Interval(TimeSpan.FromMinutes(1)),
                    MissedFirePolicy.Coalesce,
                    new CountingDispatcher())
                .ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Advance 2m -> Fault #1 (immediate retry) + Fault #2 (arms RestartBackoff).
            time.Advance(TimeSpan.FromMinutes(2));
            await sink.WaitForFaultCountAsync(2, TestTimeout).ConfigureAwait(false);

            var probeDispatcher = new CountingDispatcher();
            await registry.RegisterAsync(
                    "probe",
                    Cadence.Interval(TimeSpan.FromHours(1)),
                    MissedFirePolicy.Coalesce,
                    probeDispatcher)
                .ConfigureAwait(false);
            await registry.TriggerAsync("probe").ConfigureAwait(false);

            // Advance less than backoff: still parked in backoff, so probe must not fire.
            time.Advance(TimeSpan.FromMinutes(1));
            await Task.Yield();
            await Assert.That(probeDispatcher.FireCount).IsEqualTo(0);

            // Now advance across the remainder of RestartBackoff: loop resumes and drains
            // the queued Trigger command.
            time.Advance(backoff);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            await Assert.That(probeDispatcher.FireCount).IsEqualTo(1);
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
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

    private sealed record NullThenThrowCadence : Cadence
    {
        private int calls;

        public override DateTimeOffset? ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)
        {
            if (Interlocked.Increment(ref this.calls) == 1)
            {
                return null;
            }

            throw new InvalidOperationException("Simulated ComputeNextFire failure on trigger.");
        }
    }

    private sealed class TriggerOnFirstFireSink(
        ScheduleRegistry registry,
        string sourceJob,
        string targetJob) : ISchedulerEventSink
    {
        private int triggered;

        public void Publish<TEvent>(in TEvent schedulerEvent)
            where TEvent : struct
        {
            if (schedulerEvent is JobFiredEvent fired
                && fired.JobName == sourceJob
                && Interlocked.Exchange(ref this.triggered, 1) == 0)
            {
                var task = registry.TriggerAsync(targetJob, CancellationToken.None);
                if (!task.IsCompletedSuccessfully)
                {
                    throw new InvalidOperationException("Expected uncontended TriggerAsync to complete synchronously.");
                }
            }
        }
    }

    private sealed class FaultThenRecoverSink(int throwCount) : ISchedulerEventSink
    {
        private int firedThrows;
        private int faultCount;

        public int FaultCount => Volatile.Read(ref this.faultCount);

        public void Publish<TEvent>(in TEvent schedulerEvent)
            where TEvent : struct
        {
            if (schedulerEvent is JobFiredEvent
                && Interlocked.Increment(ref this.firedThrows) <= throwCount)
            {
                throw new InvalidOperationException("injected publish fault");
            }

            if (schedulerEvent is SchedulerFaultedEvent)
            {
                Interlocked.Increment(ref this.faultCount);
            }
        }

        public async Task WaitForFaultCountAsync(int target, TimeSpan timeout)
        {
            var deadline = Task.Delay(timeout, TimeProvider.System, CancellationToken.None);
            while (this.FaultCount < target)
            {
                if (deadline.IsCompleted)
                {
                    throw new TimeoutException(
                        $"Fault count reached {this.FaultCount}, expected {target}, within {timeout}.");
                }

                await Task.Yield();
            }
        }
    }
}
