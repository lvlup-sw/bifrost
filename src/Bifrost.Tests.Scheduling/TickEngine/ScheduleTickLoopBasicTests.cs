// =============================================================================
// <copyright file="ScheduleTickLoopBasicTests.cs" company="Levelup Software">
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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// Tests for the basic <see cref="ScheduleTickLoop"/> fire loop (Task 25, DR-7):
/// the min-heap tick engine fires due jobs in next-fire order, recurs intervals,
/// drops one-shots, honours pause/resume, and fires triggers out of band — all on
/// a <see cref="FakeTimeProvider"/> with no real wall-clock waits.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class ScheduleTickLoopBasicTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies an empty registry never dispatches even after time advances.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task NoJobs_AfterAdvance_NoDispatch()
    {
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromHours(1));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // No job registered: nothing fired.
        await Assert.That(fx.Events.OfType<JobFiredEvent>().Any()).IsFalse();
    }

    /// <summary>
    /// Verifies a single interval job fires once when its interval elapses.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task IntervalJob_FiresOnce_AtInterval()
    {
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("five-min", Cadence.Interval(TimeSpan.FromMinutes(5)))
            .ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(dispatcher.FireCount).IsEqualTo(1);
        await Assert.That(dispatcher.LastFireTime).IsEqualTo(Start.AddMinutes(5));
    }

    /// <summary>
    /// Verifies advancing past several intervals fires the job once per interval.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task IntervalJob_Advance15Min_Fires3Times()
    {
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("five-min", Cadence.Interval(TimeSpan.FromMinutes(5)))
            .ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromMinutes(15));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(dispatcher.FireCount).IsEqualTo(3);
    }

    /// <summary>
    /// Verifies a one-shot job fires once at its instant then leaves the heap.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task OneShotJob_FiresOnce_ThenLeavesHeap()
    {
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("once", Cadence.At(Start.AddMinutes(10)))
            .ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromMinutes(10));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(dispatcher.FireCount).IsEqualTo(1);

        // Advancing far past the one-shot must not re-fire it.
        fx.Time.Advance(TimeSpan.FromHours(1));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(dispatcher.FireCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies multiple jobs fire in ascending next-fire order: advancing past only
    /// the earlier job's instant fires it alone, and advancing past the later job's
    /// instant then fires it too. (Order is asserted via which job is due when, not
    /// the pool-thread completion order, which races between two fire-and-forget
    /// dispatches.)
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task MultipleJobs_FireInNextFireOrder()
    {
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);
        var late = await fx.RegisterAsync("late", Cadence.At(Start.AddMinutes(10))).ConfigureAwait(false);
        var early = await fx.RegisterAsync("early", Cadence.At(Start.AddMinutes(2))).ConfigureAwait(false);

        // Advance past only the early job: it fires, the late one does not.
        fx.Time.Advance(TimeSpan.FromMinutes(2));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(early.FireCount).IsEqualTo(1);
        await Assert.That(late.FireCount).IsEqualTo(0);
        await Assert.That(early.LastFireTime).IsEqualTo(Start.AddMinutes(2));

        // Advance past the late job: it now fires too.
        fx.Time.Advance(TimeSpan.FromMinutes(8));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(late.FireCount).IsEqualTo(1);
        await Assert.That(late.LastFireTime).IsEqualTo(Start.AddMinutes(10));
    }

    /// <summary>
    /// Verifies a paused job does not fire.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task PausedJob_DoesNotFire()
    {
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("five-min", Cadence.Interval(TimeSpan.FromMinutes(5)))
            .ConfigureAwait(false);
        await fx.Registry.PauseAsync("five-min").ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromMinutes(15));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(dispatcher.FireCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies a resumed job fires again on its cadence.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ResumedJob_FiresAgain()
    {
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("five-min", Cadence.Interval(TimeSpan.FromMinutes(5)))
            .ConfigureAwait(false);
        await fx.Registry.PauseAsync("five-min").ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await fx.Registry.ResumeAsync("five-min").ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(dispatcher.FireCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies a trigger command fires the job immediately, regardless of schedule.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TriggerCommand_FiresImmediately_RegardlessOfSchedule()
    {
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);

        // Cadence is an hour out, but the trigger fires now without advancing time.
        var dispatcher = await fx.RegisterAsync("hourly", Cadence.Interval(TimeSpan.FromHours(1)))
            .ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await fx.Registry.TriggerAsync("hourly").ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(dispatcher.FireCount).IsEqualTo(1);
    }

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// A test harness owning a started <see cref="ScheduleTickLoop"/>, its registry,
    /// store, and fake clock. Disposal stops the loop.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly RecordingSchedulerEventSink sink;

        private Fixture(
            FakeTimeProvider time,
            ScheduleRegistry registry,
            InMemoryScheduleStore store,
            ScheduleTickLoop loop,
            RecordingSchedulerEventSink sink)
        {
            this.Time = time;
            this.Registry = registry;
            this.Store = store;
            this.Loop = loop;
            this.sink = sink;
        }

        public FakeTimeProvider Time { get; }

        public ScheduleRegistry Registry { get; }

        public InMemoryScheduleStore Store { get; }

        public ScheduleTickLoop Loop { get; }

        public IReadOnlyList<object> Events => this.sink.Published;

        public static async Task<Fixture> StartAsync(DateTimeOffset start)
        {
            var time = new FakeTimeProvider(start);
            var store = new InMemoryScheduleStore();
            var registry = new ScheduleRegistry(store, time);
            var sink = new RecordingSchedulerEventSink();
            var router = new JobDispatcherRouter(sink);
            var loop = new ScheduleTickLoop(
                registry,
                store,
                time,
                router,
                sink,
                NullLogger<ScheduleTickLoop>.Instance,
                new SchedulerOptions());

            var fx = new Fixture(time, registry, store, loop, sink);
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
    /// A dispatcher that counts fires and records the scheduled occurrence time of the
    /// most recent fire. Thread-safe: the router dispatches on a pool thread.
    /// </summary>
    private sealed class CountingDispatcher : IJobDispatcher
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
