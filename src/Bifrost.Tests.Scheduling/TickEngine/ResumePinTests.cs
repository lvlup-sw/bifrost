// =============================================================================
// <copyright file="ResumePinTests.cs" company="Levelup Software">
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
/// Tests for the resume missed-fire policy in the <see cref="ScheduleTickLoop"/>
/// (Task 48, DR-10): pausing a job across N occurrences and then resuming must
/// produce ZERO catch-up fires — the missed-fire catch-up logic applies only at
/// startup from durable store recovery, not on a live resume.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class ResumePinTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that pausing a job, advancing past N occurrences, and then resuming
    /// produces exactly zero catch-up fires; the next fire is the next natural
    /// occurrence of the cadence relative to now (DR-10: resume does not apply
    /// missed-fire policies, only startup-from-store recovery does).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Resume_AfterNMissedOccurrences_FiresNothing_NextFireIsNaturalOccurrence()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("five-min", Cadence.Interval(TimeSpan.FromMinutes(5)))
            .ConfigureAwait(false);

        // Pause the job before any occurrence.
        await fx.Registry.PauseAsync("five-min").ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // Advance past 5 occurrences while paused.
        fx.Time.Advance(TimeSpan.FromMinutes(25));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // Nothing should have fired while paused.
        await Assert.That(dispatcher.FireCount).IsEqualTo(0);

        // No missed-fire events should have been published (no startup recovery here).
        await Assert.That(fx.Events.OfType<JobMissedFireEvent>().Any()).IsFalse();

        // Resume: zero catch-up fires expected.
        await fx.Registry.ResumeAsync("five-min").ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // Immediately after resume: still zero fires (the next occurrence is in the future).
        await Assert.That(dispatcher.FireCount).IsEqualTo(0);

        // No missed-fire events should have been published by the resume path.
        await Assert.That(fx.Events.OfType<JobMissedFireEvent>().Any()).IsFalse();

        // Advance to exactly the next natural occurrence (Start + 30 min). After
        // resuming at Start + 25 min, the next fire is Start + 30 min. We advance only
        // 5 minutes so we cross exactly that one future occurrence — not multiple.
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // Exactly one fire: the next natural occurrence, not a catch-up backlog.
        await Assert.That(dispatcher.FireCount).IsEqualTo(1);
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
                NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions());

            var fx = new Fixture(time, registry, loop, sink);
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            return fx;
        }

        public async Task<CountingDispatcher> RegisterAsync(string name, Cadence cadence)
        {
            var dispatcher = new CountingDispatcher();
            await this.Registry.RegisterAsync(name, cadence, MissedFirePolicy.SkipMissed, dispatcher)
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
