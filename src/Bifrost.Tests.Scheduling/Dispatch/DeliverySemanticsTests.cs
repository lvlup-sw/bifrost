// =============================================================================
// <copyright file="DeliverySemanticsTests.cs" company="Levelup Software">
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

using Bifrost.Tests.Scheduling.TickEngine;

namespace Bifrost.Tests.Scheduling.Dispatch;

/// <summary>
/// Tests for DR-12 delivery semantics (Task 49): <see cref="JobFireContext.FireTime"/>
/// is the scheduled occurrence time — stable, deterministic, and never derived from the
/// wall-clock dispatch instant. An at-least-once delivery contract applies: a crash
/// between dispatch handoff and <c>RecordFiredAsync</c> may produce a
/// <see cref="MissedFirePolicy.Coalesce"/> re-fire for the same occurrence, but the
/// re-fire presents the identical <c>(JobName, FireTime)</c> pair so handlers can
/// deduplicate by that key. On-demand triggers use the trigger instant as
/// <c>FireTime</c> since no scheduled occurrence exists.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class DeliverySemanticsTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    // A real root provider so the loop opens (and disposes) a genuine per-fire scope on
    // every fire (F2/M2). Empty: these tests assert the FireTime invariants, not scoped
    // resolution, so the scope path is exercised end-to-end without extra services.
    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that after a late wake (the loop woke 20 ms after the scheduled
    /// occurrence), <c>FireTime</c> in the <see cref="JobFireContext"/> equals the
    /// scheduled occurrence instant — never the wall-clock instant the dispatch
    /// actually ran (DR-12).
    /// </summary>
    /// <remarks>
    /// The scheduled occurrence is Start + 60 s. The test simulates a late wake of 20 ms: it
    /// advances the clock to Start + 60 s + 20 ms, and the loop dispatches the job. <c>FireTime</c>
    /// must be the scheduled occurrence (Start + 60 s), not the dispatch instant (Start + 60.020
    /// s).
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JobFireContext_FireTime_IsScheduledOccurrenceTime_NotWallClock()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)))
            .ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromSeconds(60).Add(TimeSpan.FromMilliseconds(20)));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(dispatcher.FireCount).IsEqualTo(1);

        var expectedOccurrence = Start.AddSeconds(60);
        await Assert.That(dispatcher.LastFireContext!.Value.FireTime).IsEqualTo(expectedOccurrence);
    }

    /// <summary>
    /// Verifies the at-least-once / Coalesce re-fire contract: a
    /// <see cref="MissedFirePolicy.Coalesce"/> catch-up fire for a missed occurrence
    /// presents the IDENTICAL <c>(JobName, FireTime)</c> pair as the original
    /// occurrence, so a handler can deduplicate by that key (DR-12).
    /// </summary>
    /// <remarks>
    /// This simulates a crash between dispatch handoff and <c>RecordFiredAsync</c>:
    /// at restart, the loop sees the occurrence as missed (durable state was not
    /// updated) and re-fires it via <see cref="MissedFirePolicy.Coalesce"/>. The
    /// test uses a <see cref="SeedingStore"/> that returns a pre-seeded record from
    /// <c>LoadAllAsync</c> (representing the stale durable state after the crash)
    /// while delegating all writes to an inner <see cref="InMemoryScheduleStore"/>.
    /// <para>
    /// The seeded record says that the job last fired at Start - 2 min and that the next occurrence
    /// was exactly Start. The process crashed before <c>RecordFiredAsync</c> ran, so the store
    /// still shows <c>lastFiredAt</c> = Start - 2 min. At restart, the loop detects the missed
    /// occurrence and re-fires it with Coalesce.
    /// </para>
    /// <para>
    /// The test calls <c>RegisterAsync</c> so that the loop can resolve the dispatcher at dispatch
    /// time. <c>SeedingStore</c> ignores that <c>SaveAsync</c> call and keeps the stale record for
    /// <c>LoadAllAsync</c>, so <c>SeedAsync</c> sees the pre-crash <c>LastFiredAt</c>. The Coalesce
    /// re-fire must use the original missed occurrence as <c>FireTime</c>, so the <c>(JobName,
    /// FireTime)</c> pair is stable across a re-fire.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JobFireContext_FireTime_StableAcrossReFire()
    {
        var missedOccurrence = Start;
        var lastFiredAt = Start.AddMinutes(-2);

        var dispatcher = new RecordingDispatcher();

        var staleDurableRecord = new JobRecord(
            Name: "job",
            Cadence: Cadence.Interval(TimeSpan.FromMinutes(2)),
            MissedFirePolicy: MissedFirePolicy.Coalesce,
            State: JobState.Running,
            LastFiredAt: lastFiredAt,
            NextFireAt: missedOccurrence,
            DispatchKind: "custom",
            DispatcherTypeName: null,
            Metadata: new Dictionary<string, string>());

        var store = new SeedingStore(staleDurableRecord);

        var time = new FakeTimeProvider(Start);
        var registry = new ScheduleRegistry(store, time);
        var sink = new RecordingSchedulerEventSink();
        var router = new JobDispatcherRouter(sink);
        var loop = new ScheduleTickLoop(
            registry, store, time, router, sink,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions(),
            serviceProvider: Services);

        await registry.RegisterAsync(
            "job",
            Cadence.Interval(TimeSpan.FromMinutes(2)),
            MissedFirePolicy.Coalesce,
            dispatcher).ConfigureAwait(false);

        await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
        await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
        loop.Dispose();

        await Assert.That(dispatcher.FireCount).IsGreaterThanOrEqualTo(1);

        var context = dispatcher.LastFireContext!.Value;
        await Assert.That(context.JobName).IsEqualTo("job");
        await Assert.That(context.FireTime).IsEqualTo(missedOccurrence);
    }

    /// <summary>
    /// A <see cref="IScheduleStore"/> that always returns a fixed pre-seeded record
    /// from <see cref="LoadAllAsync"/> — regardless of subsequent
    /// <see cref="SaveAsync"/> calls — to simulate the durable state a process would
    /// see on restart after a crash before <c>RecordFiredAsync</c> completed.
    /// All write operations are silently accepted (no-op) so the test can call
    /// <see cref="ScheduleRegistry.RegisterAsync"/> without overwriting the seeded state.
    /// </summary>
    private sealed class SeedingStore(JobRecord seededRecord) : IScheduleStore
    {
        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<JobRecord>>([seededRecord]);

        public ValueTask SaveAsync(JobRecord job, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask RecordFiredAsync(
            string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteAsync(string jobName, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Verifies that on-demand triggers (<see cref="IScheduleRegistry.TriggerAsync"/>)
    /// use the trigger instant as <c>FireTime</c>, since no scheduled occurrence exists
    /// for the fire (DR-12).
    /// </summary>
    /// <remarks>
    /// The test advances the clock 30 minutes to a specific trigger instant. Then it triggers the
    /// job out of band, before the schedule makes it due. <c>FireTime</c> must be the trigger
    /// instant (now, when <c>TriggerAsync</c> fired), not the next scheduled occurrence, which is
    /// 30 minutes in the future.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TriggerAsync_FireTime_IsTriggerInstant()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("job", Cadence.Interval(TimeSpan.FromHours(1)))
            .ConfigureAwait(false);

        var triggerInstant = Start.AddMinutes(30);
        fx.Time.Advance(TimeSpan.FromMinutes(30));

        await fx.Registry.TriggerAsync("job").ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(dispatcher.FireCount).IsEqualTo(1);

        await Assert.That(dispatcher.LastFireContext!.Value.FireTime).IsEqualTo(triggerInstant);
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
    /// A dispatcher that records the most recent <see cref="JobFireContext"/> and counts
    /// fires. Thread-safe: the router dispatches on the pool.
    /// </summary>
    private sealed class RecordingDispatcher : IJobDispatcher
    {
        private readonly object gate = new();
        private int fireCount;
        private JobFireContext? lastFireContext;

        public int FireCount => Volatile.Read(ref this.fireCount);

        public JobFireContext? LastFireContext
        {
            get
            {
                lock (this.gate)
                {
                    return this.lastFireContext;
                }
            }
        }

        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            lock (this.gate)
            {
                this.lastFireContext = context;
            }

            Interlocked.Increment(ref this.fireCount);
            return ValueTask.CompletedTask;
        }
    }
}
