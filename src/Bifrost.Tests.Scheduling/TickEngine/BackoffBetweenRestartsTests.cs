// =============================================================================
// <copyright file="BackoffBetweenRestartsTests.cs" company="Levelup Software">
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
using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// Coverage for the tick-loop fault-restart backoff (#32, G6d): a crash loop backs off
/// between restarts on the injected <see cref="TimeProvider"/> instead of spinning the
/// CPU, the backoff is skipped for an isolated first fault, the give-up transition is
/// still reached with a non-zero backoff, and cancelling during a backoff exits cleanly
/// as a normal shutdown.
/// </summary>
/// <remarks>
/// All time flows through a <see cref="FakeTimeProvider"/>: the backoff is a
/// <c>Task.Delay</c> on that provider, so it only elapses when the test advances the fake
/// clock — which is exactly what lets these tests prove the loop is genuinely waiting on
/// the backoff rather than spinning. The faulted barrier
/// (<see cref="ScheduleTickLoop.WaitForFaultedAsync"/>) and a real-wall-clock test timeout
/// bound every wait so a regression fails fast instead of hanging.
/// </remarks>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class BackoffBetweenRestartsTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    // A real root provider so each fire opens (and disposes) a genuine per-fire scope.
    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies an isolated first fault recovers immediately — without any fake-clock
    /// advance — confirming the backoff is skipped for a one-off blip (only a repeated
    /// fault, the crash-loop signal, pays the delay). The single transient fault is logged
    /// critical and surfaced as a <see cref="SchedulerFaultedEvent"/>, then the next
    /// occurrence fires normally with no clock movement across the backoff.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task IsolatedFirstFault_DoesNotBackOff_RecoversImmediately()
    {
        var sink = new FaultRecordingEventSink { ThrowOnFiredCount = 1 };
        var logger = new CapturingLogger<ScheduleTickLoop>();
        await using var fx = await Fixture.StartAsync(
            sink, new SchedulerOptions { RestartBackoff = TimeSpan.FromSeconds(1) }, logger).ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(5))).ConfigureAwait(false);

        // One fire faults the loop. With the first-fault skip, the loop recovers WITHOUT a
        // backoff: WaitForIdleAsync returns even though the fake clock never advanced past
        // the (would-be) backoff. A regression that backed off on the first fault would
        // park on the fake-clock delay and this WaitForIdleAsync would time out.
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(logger.Any(LogLevel.Critical)).IsTrue();
        await Assert.That(sink.FaultCount).IsEqualTo(1);
        await Assert.That(fx.Loop.IsFaulted).IsFalse();

        // The next occurrence fires normally after the transient fault clears.
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(dispatcher.FireCount).IsGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Verifies a non-zero backoff genuinely delays the restarts rather than spinning: the
    /// loop cannot give up on the initial advance alone (the backoff parks it after the
    /// second fault, before it can reach the give-up threshold), and give-up is reached only
    /// once the fake clock is driven across the backoffs. The "not yet faulted right after
    /// the initial advance" assertion is the proof the backoff is real — a spin would have
    /// burned through all the restarts and given up before any clock advance.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GiveUp_IsDelayedByBackoff_ThenReached_WhenClockDriven()
    {
        var backoff = TimeSpan.FromSeconds(2);
        var sink = new FaultRecordingEventSink { ThrowOnFiredCount = int.MaxValue };
        await using var fx = await Fixture.StartAsync(
            sink,
            new SchedulerOptions
            {
                RestartBackoff = backoff,
                MaxRestartsInWindow = 3,
                RestartWindow = TimeSpan.FromSeconds(60),
            }).ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(5))).ConfigureAwait(false);

        // Make several occurrences due in one advance. Fault #1 (first in window) does not
        // back off and immediately re-faults into fault #2, which arms the backoff. Give-up
        // needs more than MaxRestartsInWindow (3) faults, i.e. a 4th — which cannot happen
        // until a backoff elapses. So after the initial advance and two faults the loop is
        // parked on the backoff and is NOT yet faulted: that is the backoff doing its job.
        fx.Time.Advance(TimeSpan.FromMinutes(30));
        await sink.WaitForFaultCountAsync(2, TestTimeout).ConfigureAwait(false);
        await Assert.That(fx.Loop.IsFaulted).IsFalse();

        // Now drive the fake clock across each remaining per-restart backoff until the loop
        // gives up. The loop arms each backoff on its own thread, so advance-and-yield until
        // the faulted barrier completes (bounded by TestTimeout) rather than assuming a
        // single advance crosses a backoff that may not be armed yet.
        var faulted = fx.Loop.WaitForFaultedAsync(TestTimeout);
        while (!faulted.IsCompleted)
        {
            fx.Time.Advance(backoff);
            await Task.Yield();
        }

        await faulted.ConfigureAwait(false);
        await Assert.That(fx.Loop.IsFaulted).IsTrue();

        // Faulted: advancing time no longer ticks (no further fires attempted).
        var firesBefore = dispatcher.FireCount;
        fx.Time.Advance(TimeSpan.FromMinutes(30));
        await Assert.That(dispatcher.FireCount).IsEqualTo(firesBefore);
    }

    /// <summary>
    /// Verifies cancelling during a backoff exits cleanly as a normal shutdown: with a long
    /// backoff the loop parks on the backoff timer (the fake clock never advances it), and
    /// <see cref="IHostedService.StopAsync"/> cancels the stopping token, which cancels the
    /// backoff delay. <c>StopAsync</c> returns promptly without the cancellation leaking as a
    /// fault, and the loop is not in its faulted state.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CancellationDuringBackoff_ExitsCleanly()
    {
        // A backoff far longer than the test could ever advance, so once the loop parks on
        // it the ONLY thing that can release it is cancellation.
        var sink = new FaultRecordingEventSink { ThrowOnFiredCount = int.MaxValue };
        var logger = new CapturingLogger<ScheduleTickLoop>();
        var fx = await Fixture.StartAsync(
            sink,
            new SchedulerOptions
            {
                RestartBackoff = TimeSpan.FromHours(1),
                MaxRestartsInWindow = 1000,
                RestartWindow = TimeSpan.FromHours(1),
            },
            logger).ConfigureAwait(false);
        _ = await fx.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(5))).ConfigureAwait(false);

        // Fault #1 (no backoff) re-faults into fault #2, which arms the hour-long backoff.
        fx.Time.Advance(TimeSpan.FromMinutes(30));
        await sink.WaitForFaultCountAsync(2, TestTimeout).ConfigureAwait(false);

        // Stop while the loop is parked on the backoff. StopAsync cancels the stopping
        // token; the backoff delay observes the cancellation, the loop exits cleanly, and
        // StopAsync returns within the real-wall-clock timeout with no exception leak.
        var stop = ((IHostedService)fx.Loop).StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stop, Task.Delay(TestTimeout, TimeProvider.System)).ConfigureAwait(false);
        await Assert.That(completed).IsEqualTo((Task)stop);
        await stop.ConfigureAwait(false); // re-throws if the loop leaked an exception on stop

        // A clean cancellation is not a fault: the loop did not transition to faulted, and
        // nothing logged the loop-gave-up message.
        await Assert.That(fx.Loop.IsFaulted).IsFalse();
        fx.Loop.Dispose();
    }

    /// <summary>
    /// Verifies disposing the registry while the loop is parked on a repeated-fault backoff
    /// wakes the loop promptly — the dispose-as-stop cousin of the HIGH-1 fix. With a backoff
    /// far longer than any clock advance, fault #1 (no backoff) re-faults into fault #2, which
    /// arms the long backoff; the loop is then parked on it. Disposing the registry completes
    /// its command-channel writer — WITHOUT advancing the fake clock past the backoff and
    /// WITHOUT cancelling the stopping token — which the backoff race must observe so
    /// <see cref="BackgroundService.ExecuteTask"/> completes promptly. Without the race the
    /// loop waits out the full (hour-long) backoff that the fake clock never advances, so
    /// <c>ExecuteTask</c> never completes and the bounded wait times out, failing the test.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DisposeRegistryDuringBackoff_WithoutCancel_StopsLoopPromptly()
    {
        // A backoff far longer than the test could ever advance, so once the loop parks on it
        // the only thing that can release it is the registry-dispose channel completion.
        var sink = new FaultRecordingEventSink { ThrowOnFiredCount = int.MaxValue };
        var fx = await Fixture.StartAsync(
            sink,
            new SchedulerOptions
            {
                RestartBackoff = TimeSpan.FromHours(1),
                MaxRestartsInWindow = 1000,
                RestartWindow = TimeSpan.FromHours(1),
            }).ConfigureAwait(false);
        _ = await fx.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(5))).ConfigureAwait(false);

        // Fault #1 (first in window, no backoff) re-faults into fault #2, which arms the
        // hour-long backoff. After two faults the loop is parked on the backoff.
        fx.Time.Advance(TimeSpan.FromMinutes(30));
        await sink.WaitForFaultCountAsync(2, TestTimeout).ConfigureAwait(false);

        var executeTask = ((BackgroundService)fx.Loop).ExecuteTask;
        await Assert.That(executeTask is not null).IsTrue();

        // Dispose the registry WITHOUT cancelling the stopping token and WITHOUT advancing the
        // fake clock past the backoff. Completing the command-channel writer must wake the loop
        // out of the backoff so it re-checks ShouldStop and exits cleanly.
        await fx.Registry.DisposeAsync().ConfigureAwait(false);

        await Assert.That(async () => await executeTask!.WaitAsync(TestTimeout).ConfigureAwait(false))
            .ThrowsNothing();
        await Assert.That(executeTask!.IsCompleted).IsTrue();
        await Assert.That(fx.Loop.IsFaulted).IsFalse();

        fx.Loop.Dispose();
    }

    /// <summary>
    /// A test harness owning a started <see cref="ScheduleTickLoop"/> with a fault-recording
    /// event sink, mirroring the fixture used by the loop's other fault tests.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(FakeTimeProvider time, ScheduleRegistry registry, ScheduleTickLoop loop)
        {
            this.Time = time;
            this.Registry = registry;
            this.Loop = loop;
        }

        public FakeTimeProvider Time { get; }

        public ScheduleRegistry Registry { get; }

        public ScheduleTickLoop Loop { get; }

        public static async Task<Fixture> StartAsync(
            ISchedulerEventSink sink,
            SchedulerOptions options,
            ILogger<ScheduleTickLoop>? logger = null)
        {
            var time = new FakeTimeProvider(Start);
            var store = new InMemoryScheduleStore();
            var registry = new ScheduleRegistry(store, time);
            var router = new JobDispatcherRouter(sink);
            var loop = new ScheduleTickLoop(
                registry, store, time, router, sink,
                logger ?? new CapturingLogger<ScheduleTickLoop>(), options,
                serviceProvider: Services);

            var fx = new Fixture(time, registry, loop);
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            return fx;
        }

        public async Task<CountingDispatcher> RegisterAsync(string name, Cadence cadence)
        {
            var dispatcher = new CountingDispatcher();
            await this.Registry.RegisterAsync(name, cadence, MissedFirePolicy.Coalesce, dispatcher)
                .ConfigureAwait(false);
            await this.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            return dispatcher;
        }

        public async ValueTask DisposeAsync()
        {
            await ((IHostedService)this.Loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            this.Loop.Dispose();
        }
    }

    /// <summary>
    /// An event sink that throws on a configurable number of <see cref="JobFiredEvent"/>
    /// publishes — injecting a fault into the loop's own (non-dispatch) code path — and
    /// records each <see cref="SchedulerFaultedEvent"/> so a test can wait for the Nth fault
    /// without a real sleep.
    /// </summary>
    private sealed class FaultRecordingEventSink : ISchedulerEventSink
    {
        private int firedThrows;
        private int faultCount;

        public int ThrowOnFiredCount { get; init; }

        public int FaultCount => Volatile.Read(ref this.faultCount);

        public void Publish<TEvent>(in TEvent schedulerEvent)
            where TEvent : struct
        {
            if (schedulerEvent is JobFiredEvent && Interlocked.Increment(ref this.firedThrows) <= this.ThrowOnFiredCount)
            {
                throw new InvalidOperationException("injected publish fault");
            }

            if (schedulerEvent is SchedulerFaultedEvent)
            {
                Interlocked.Increment(ref this.faultCount);
            }
        }

        /// <summary>
        /// Waits until at least <paramref name="target"/> faults have been recorded, bounded
        /// by <paramref name="timeout"/> on the real wall clock so a regression fails fast.
        /// Yields between polls — it never sleeps on the fake clock — so it does not itself
        /// advance the loop's time.
        /// </summary>
        /// <param name="target">The fault count to wait for.</param>
        /// <param name="timeout">The real-wall-clock bound on the wait.</param>
        /// <returns>A task that completes when the count is reached.</returns>
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

    /// <summary>A dispatcher that counts fires. Thread-safe: the router dispatches on the pool.</summary>
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
