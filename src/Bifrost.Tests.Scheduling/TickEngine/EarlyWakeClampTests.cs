// =============================================================================
// <copyright file="EarlyWakeClampTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
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
/// Tests for the early-wake clamp in the <see cref="ScheduleTickLoop"/> (Task 47,
/// R2/DR-7): when the loop is woken before a job's <c>NextFireAt</c> it must not
/// dispatch the job early, and when the timer fires slightly early (NCronJob#327 class)
/// it must dispatch exactly once per occurrence. The loop must also compute the next
/// occurrence from the scheduled occurrence time, never from a wall-clock read taken
/// before that instant.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class EarlyWakeClampTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    // A real root provider so each fire opens (and disposes) a genuine per-fire scope
    // (F2/M2) — exercises the per-fire scope lifecycle in this fixture's tick loop.
    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that when the loop is woken by a command-channel nudge while
    /// <c>now &lt; NextFireAt</c>, it performs zero dispatches and re-arms until the
    /// scheduled occurrence comes due (R2/DR-7). This codifies the early-wake clamp:
    /// a false-positive wake must not cause an early dispatch.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TickLoop_WakesBeforeNextFireAt_ReSleeps_NoDispatch()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync(
            "five-min", Cadence.Interval(TimeSpan.FromMinutes(5))).ConfigureAwait(false);

        // Fire is due at Start + 5 min. Advance only 3 min — job is not due.
        fx.Time.Advance(TimeSpan.FromMinutes(3));

        // Poke the loop with a barrier request (simulates a command-channel nudge wake).
        // The loop wakes, sees now < NextFireAt, and goes back to sleep without firing.
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // Zero dispatches: the early wake must not fire.
        await Assert.That(dispatcher.FireCount).IsEqualTo(0);

        // Now advance to the actual scheduled time; the job should fire exactly once.
        fx.Time.Advance(TimeSpan.FromMinutes(2));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(dispatcher.FireCount).IsEqualTo(1);
    }

    /// <summary>
    /// Simulates the NCronJob#327 class of timer over-fire: advances the fake clock to
    /// 20ms before the scheduled occurrence, waking the loop, then advances to the
    /// exact scheduled instant. The loop must deliver exactly one dispatch per
    /// occurrence — no early dispatch and no missed dispatch.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TickLoop_TimerFires20msEarly_ExactlyOneDispatchPerOccurrence()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync(
            "one-min", Cadence.Interval(TimeSpan.FromMinutes(1))).ConfigureAwait(false);

        // Scheduled occurrence: Start + 60 s.
        // Advance to 59.98 s (20 ms early) — the loop wakes but the job is not yet due.
        fx.Time.Advance(TimeSpan.FromMilliseconds(59_980));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // Still zero: the clamp must re-sleep.
        await Assert.That(dispatcher.FireCount).IsEqualTo(0);

        // Advance the remaining 20 ms to reach the exact scheduled instant.
        fx.Time.Advance(TimeSpan.FromMilliseconds(20));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // Exactly one dispatch per occurrence — no duplicate, no miss.
        await Assert.That(dispatcher.FireCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that after a fire, the next occurrence is computed from the scheduled
    /// occurrence instant (the heap key), never from a wall-clock reading that predates
    /// it. Concretely, after an early wake + clamp + re-arm cycle, the job's next
    /// scheduled occurrence is exactly <c>occurrence + interval</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TickLoop_NextFire_ComputedFromScheduledOccurrenceTime()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync(
            "five-min", Cadence.Interval(TimeSpan.FromMinutes(5))).ConfigureAwait(false);

        // Scheduled occurrence: Start + 5 min (= Start + 300 s).
        // Advance to the exact occurrence so the first fire is dispatched.
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(dispatcher.FireCount).IsEqualTo(1);

        // The first fire time must equal the scheduled occurrence.
        await Assert.That(dispatcher.LastFireTime).IsEqualTo(Start.AddMinutes(5));

        // The NEXT scheduled occurrence must be exactly occurrence + interval,
        // i.e., Start + 10 min. Advance there and confirm exactly one more fire.
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(dispatcher.FireCount).IsEqualTo(2);
        await Assert.That(dispatcher.LastFireTime).IsEqualTo(Start.AddMinutes(10));
    }

    /// <summary>
    /// A test harness owning a started <see cref="ScheduleTickLoop"/>.
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

            var fx = new Fixture(time, registry, loop);
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            return fx;
        }

        public async Task<RecordingDispatcher> RegisterAsync(string name, Cadence cadence)
        {
            var dispatcher = new RecordingDispatcher();
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
    /// A dispatcher that counts fires and records the fire time of the most recent
    /// dispatch. Thread-safe: the router dispatches on the pool.
    /// </summary>
    private sealed class RecordingDispatcher : IJobDispatcher
    {
        private int fireCount;
        private long lastFireTimeTicks;

        public int FireCount => Volatile.Read(ref this.fireCount);

        public DateTimeOffset LastFireTime =>
            new(Interlocked.Read(ref this.lastFireTimeTicks), TimeSpan.Zero);

        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            Interlocked.Exchange(ref this.lastFireTimeTicks, context.FireTime.UtcTicks);
            Interlocked.Increment(ref this.fireCount);
            return ValueTask.CompletedTask;
        }
    }
}
