// =============================================================================
// <copyright file="DispatchFailureTests.cs" company="Levelup Software">
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
/// Tests for dispatch-failure isolation in the <see cref="ScheduleTickLoop"/> (Task
/// 30, DR-4/DR-10): a failed dispatch surfaces as a
/// <see cref="JobFireFailedEvent"/> without crashing the loop, the failed job's next
/// fire is still scheduled, and a checkpoint (<c>RecordFiredAsync</c>) failure is
/// logged and swallowed without losing the next fire.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class DispatchFailureTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    // A real root provider so each fire opens (and disposes) a genuine per-fire scope
    // (F2/M2) — exercises the per-fire scope lifecycle in this fixture's tick loop.
    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies a throwing dispatch surfaces a <see cref="JobFireFailedEvent"/>, does
    /// not crash the loop (a healthy job still fires), and the failed job's next fire
    /// is still scheduled (it fires on its next occurrence once it stops throwing).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchFailure_PublishesFailedEvent_LoopContinues_NextFireScheduled()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);
        var failing = new ThrowingThenCountingDispatcher(throwCount: 1);
        var healthy = new CountingDispatcher();
        await fx.RegisterAsync("failing", Cadence.Interval(TimeSpan.FromMinutes(5)), failing).ConfigureAwait(false);
        await fx.RegisterAsync("healthy", Cadence.Interval(TimeSpan.FromMinutes(5)), healthy).ConfigureAwait(false);

        // First occurrence: the failing dispatch throws (isolated as JobFireFailedEvent),
        // the healthy one fires.
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(fx.Events.OfType<JobFireFailedEvent>().Any()).IsTrue();
        await Assert.That(healthy.FireCount).IsEqualTo(1);

        // The failing job's next fire was still scheduled: on its next occurrence it
        // dispatches again (now succeeding).
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(failing.SuccessCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies a <c>RecordFiredAsync</c> checkpoint failure after a successful
    /// dispatch is logged and swallowed (best-effort), and the next fire is still
    /// scheduled and fires.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CheckpointFailure_LoggedAndSwallowed_NextFireScheduled()
    {
        var logger = new CapturingLogger<ScheduleTickLoop>();
        await using var fx = await Fixture.StartAsync(
            store: new ThrowingCheckpointStore(), logger: logger).ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(5)))
            .ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // The fire happened despite the checkpoint throwing; the failure was logged.
        await Assert.That(dispatcher.FireCount).IsEqualTo(1);
        await Assert.That(logger.Any(LogLevel.Warning)).IsTrue();
        await Assert.That(fx.Loop.IsFaulted).IsFalse();

        // The next fire is still scheduled and fires on the next occurrence.
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(dispatcher.FireCount).IsEqualTo(2);
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

        public static async Task<Fixture> StartAsync(
            IScheduleStore? store = null,
            ILogger<ScheduleTickLoop>? logger = null)
        {
            var time = new FakeTimeProvider(Start);
            var effectiveStore = store ?? new InMemoryScheduleStore();
            var registry = new ScheduleRegistry(effectiveStore, time);
            var sink = new RecordingSchedulerEventSink();
            var router = new JobDispatcherRouter(sink);
            var loop = new ScheduleTickLoop(
                registry, effectiveStore, time, router, sink,
                logger ?? new CapturingLogger<ScheduleTickLoop>(), new SchedulerOptions(),
                serviceProvider: Services);

            var fx = new Fixture(time, registry, loop, sink);
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            return fx;
        }

        public async Task RegisterAsync(string name, Cadence cadence, IJobDispatcher dispatcher)
        {
            await this.Registry.RegisterAsync(name, cadence, MissedFirePolicy.Coalesce, dispatcher)
                .ConfigureAwait(false);
            await this.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        }

        public async Task<CountingDispatcher> RegisterAsync(string name, Cadence cadence)
        {
            var dispatcher = new CountingDispatcher();
            await this.RegisterAsync(name, cadence, dispatcher).ConfigureAwait(false);
            return dispatcher;
        }

        public async ValueTask DisposeAsync()
        {
            await ((IHostedService)this.Loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            this.Loop.Dispose();
        }
    }

    /// <summary>
    /// A dispatcher that throws on its first <paramref name="throwCount"/> fires then
    /// succeeds, counting the successful ones. Thread-safe.
    /// </summary>
    /// <param name="throwCount">The number of initial fires that throw.</param>
    private sealed class ThrowingThenCountingDispatcher(int throwCount) : IJobDispatcher
    {
        private int attempts;
        private int successCount;

        public int SuccessCount => Volatile.Read(ref this.successCount);

        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            if (Interlocked.Increment(ref this.attempts) <= throwCount)
            {
                throw new InvalidOperationException("injected dispatch failure");
            }

            Interlocked.Increment(ref this.successCount);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A store whose <c>RecordFiredAsync</c> always throws, to exercise checkpoint
    /// failure isolation. All other operations are no-ops.
    /// </summary>
    private sealed class ThrowingCheckpointStore : IScheduleStore
    {
        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<JobRecord>>([]);

        public ValueTask SaveAsync(JobRecord job, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask RecordFiredAsync(
            string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct) =>
            throw new InvalidOperationException("injected checkpoint failure");

        public ValueTask DeleteAsync(string jobName, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A dispatcher that counts fires. Thread-safe: the router dispatches on the pool.
    /// </summary>
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
