// =============================================================================
// <copyright file="SchedulerEventsPublicationTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Observability;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.Observability;

/// <summary>
/// Tests for the concrete <see cref="SchedulerEventStream"/> sink and the
/// publication wiring (Task 32, DR-8): every scheduler lifecycle and fire event is
/// observable to a subscriber of the concrete sink. The registry publishes the
/// lifecycle events (register / unregister / pause / resume) and the tick loop
/// publishes the fire, failure, missed-fire, and fault events; both share one sink.
/// </summary>
public sealed class SchedulerEventsPublicationTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies registering a job publishes a <see cref="JobRegisteredEvent"/>
    /// observable to a subscriber of the concrete sink.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Register_PublishesJobRegisteredEvent()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);
        using var sub = fx.Subscribe<JobRegisteredEvent>();

        await fx.RegisterAsync("alpha", Interval()).ConfigureAwait(false);

        var evt = await sub.NextAsync().ConfigureAwait(false);
        await Assert.That(evt.JobName).IsEqualTo("alpha");
    }

    /// <summary>
    /// Verifies unregistering a job publishes a <see cref="JobUnregisteredEvent"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Unregister_PublishesJobUnregisteredEvent()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);
        await fx.RegisterAsync("alpha", Interval()).ConfigureAwait(false);
        using var sub = fx.Subscribe<JobUnregisteredEvent>();

        await fx.Registry.UnregisterAsync("alpha").ConfigureAwait(false);

        var evt = await sub.NextAsync().ConfigureAwait(false);
        await Assert.That(evt.JobName).IsEqualTo("alpha");
    }

    /// <summary>
    /// Verifies pausing a job publishes a <see cref="JobPausedEvent"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Pause_PublishesJobPausedEvent()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);
        await fx.RegisterAsync("alpha", Interval()).ConfigureAwait(false);
        using var sub = fx.Subscribe<JobPausedEvent>();

        await fx.Registry.PauseAsync("alpha").ConfigureAwait(false);

        var evt = await sub.NextAsync().ConfigureAwait(false);
        await Assert.That(evt.JobName).IsEqualTo("alpha");
    }

    /// <summary>
    /// Verifies resuming a paused job publishes a <see cref="JobResumedEvent"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Resume_PublishesJobResumedEvent()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);
        await fx.RegisterAsync("alpha", Interval()).ConfigureAwait(false);
        await fx.Registry.PauseAsync("alpha").ConfigureAwait(false);
        using var sub = fx.Subscribe<JobResumedEvent>();

        await fx.Registry.ResumeAsync("alpha").ConfigureAwait(false);

        var evt = await sub.NextAsync().ConfigureAwait(false);
        await Assert.That(evt.JobName).IsEqualTo("alpha");
    }

    /// <summary>
    /// Verifies a successful fire publishes a <see cref="JobFiredEvent"/> through the
    /// concrete sink the tick loop shares with the registry.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FireSuccess_PublishesJobFiredEvent()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);
        await fx.RegisterAsync("nightly", Interval()).ConfigureAwait(false);
        using var sub = fx.Subscribe<JobFiredEvent>();

        fx.Time.Advance(TimeSpan.FromHours(1));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        var evt = await sub.NextAsync().ConfigureAwait(false);
        await Assert.That(evt.JobName).IsEqualTo("nightly");
    }

    /// <summary>
    /// Verifies a throwing dispatch publishes a <see cref="JobFireFailedEvent"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FireFailure_PublishesJobFireFailedEvent()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);
        await fx.RegisterAsync("boom", Interval(), new ThrowingDispatcher()).ConfigureAwait(false);
        using var sub = fx.Subscribe<JobFireFailedEvent>();

        fx.Time.Advance(TimeSpan.FromHours(1));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        var evt = await sub.NextAsync().ConfigureAwait(false);
        await Assert.That(evt.JobName).IsEqualTo("boom");
        await Assert.That(evt.Exception).IsNotNull();
    }

    /// <summary>
    /// Verifies a missed-fire reconciliation at startup publishes a
    /// <see cref="JobMissedFireEvent"/> through the shared sink.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task MissedFire_PublishesJobMissedFireEvent()
    {
        using var events = new SchedulerEventStream();
        using var sub = new Subscription<JobMissedFireEvent>(events);

        var time = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var cadence = Interval();
        var registry = new ScheduleRegistry(store, time, events: events);

        await registry.RegisterAsync("stale", cadence, MissedFirePolicy.Coalesce, new NoopDispatcher())
            .ConfigureAwait(false);
        await store.SaveAsync(
            new JobRecord(
                "stale", cadence, MissedFirePolicy.Coalesce, JobState.Running,
                LastFiredAt: Start, NextFireAt: null, DispatchKind: "custom",
                DispatcherTypeName: typeof(NoopDispatcher).FullName,
                Metadata: new Dictionary<string, string>()),
            CancellationToken.None).ConfigureAwait(false);
        time.Advance(TimeSpan.FromHours(5));

        var router = new JobDispatcherRouter(events);
        var loop = new ScheduleTickLoop(
            registry, store, time, router, events,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions());

        await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
        await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        var evt = await sub.NextAsync().ConfigureAwait(false);
        await Assert.That(evt.JobName).IsEqualTo("stale");
        await Assert.That(evt.MissedCount).IsGreaterThanOrEqualTo(1);

        await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
        loop.Dispose();
    }

    /// <summary>
    /// Verifies a scheduler fault publishes a <see cref="SchedulerFaultedEvent"/>
    /// observable to a subscriber of the concrete sink, exercising direct publication
    /// rather than driving a real crash loop.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Faulted_PublishesSchedulerFaultedEvent()
    {
        using var events = new SchedulerEventStream();
        using var sub = new Subscription<SchedulerFaultedEvent>(events);

        ((ISchedulerEventSink)events).Publish(
            new SchedulerFaultedEvent(new InvalidOperationException("loop crashed"), Start));

        var evt = await sub.NextAsync().ConfigureAwait(false);
        await Assert.That(evt.Exception).IsTypeOf<InvalidOperationException>();
    }

    private static IntervalCadence Interval() => new(TimeSpan.FromHours(1));

    private sealed class NoopDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
            => ValueTask.CompletedTask;
    }

    private sealed class ThrowingDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    /// <summary>
    /// A single-event subscription over a <see cref="SchedulerEventStream"/>, wrapping
    /// the async enumerable so a test can await the next event of a type with a timeout.
    /// </summary>
    /// <typeparam name="TEvent">The scheduler event type observed.</typeparam>
    private sealed class Subscription<TEvent> : IDisposable
        where TEvent : struct
    {
        private readonly CancellationTokenSource cts = new();
        private readonly IAsyncEnumerator<TEvent> enumerator;

        public Subscription(SchedulerEventStream stream)
            => this.enumerator = stream.Subscribe<TEvent>(this.cts.Token).GetAsyncEnumerator(this.cts.Token);

        public async Task<TEvent> NextAsync()
        {
            using var timeoutCts = new CancellationTokenSource(TestTimeout);
            await using var reg = timeoutCts.Token.Register(this.cts.Cancel).ConfigureAwait(false);

            if (!await this.enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                throw new InvalidOperationException("The scheduler event stream completed before an event arrived.");
            }

            return this.enumerator.Current;
        }

        public void Dispose()
        {
            this.cts.Cancel();
            this.cts.Dispose();
        }
    }

    /// <summary>
    /// A started tick-loop harness whose registry and tick loop share one concrete
    /// <see cref="SchedulerEventStream"/> sink, mirroring the production wiring.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SchedulerEventStream events;

        private Fixture(
            FakeTimeProvider time,
            ScheduleRegistry registry,
            ScheduleTickLoop loop,
            SchedulerEventStream events)
        {
            this.Time = time;
            this.Registry = registry;
            this.Loop = loop;
            this.events = events;
        }

        public FakeTimeProvider Time { get; }

        public ScheduleRegistry Registry { get; }

        public ScheduleTickLoop Loop { get; }

        public static async Task<Fixture> StartAsync()
        {
            var time = new FakeTimeProvider(Start);
            var store = new InMemoryScheduleStore();
            var events = new SchedulerEventStream();
            var registry = new ScheduleRegistry(store, time, events: events);
            var router = new JobDispatcherRouter(events);
            var loop = new ScheduleTickLoop(
                registry, store, time, router, events,
                NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions());

            var fx = new Fixture(time, registry, loop, events);
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            return fx;
        }

        public Subscription<TEvent> Subscribe<TEvent>()
            where TEvent : struct
            => new(this.events);

        public async Task RegisterAsync(string name, Cadence cadence)
            => await this.RegisterAsync(name, cadence, new NoopDispatcher()).ConfigureAwait(false);

        public async Task RegisterAsync(string name, Cadence cadence, IJobDispatcher dispatcher)
        {
            await this.Registry.RegisterAsync(name, cadence, MissedFirePolicy.Coalesce, dispatcher)
                .ConfigureAwait(false);
            await this.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await ((IHostedService)this.Loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            this.Loop.Dispose();
        }
    }
}
