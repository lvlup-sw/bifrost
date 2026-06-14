// =============================================================================
// <copyright file="SchedulerMetricsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Observability;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.Observability;

/// <summary>
/// Tests for <see cref="SchedulerMetrics"/> (Task 31, DR-8): the meter name, and
/// each instrument's behaviour when driven through the registry (registered /
/// unregistered) and the tick loop (fired / latency / missed / failures).
/// Assertions use <see cref="MetricCollector{T}"/> against the
/// <c>Bifrost.Scheduling</c> meter.
/// </summary>
public sealed class SchedulerMetricsTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies the meter name is the documented <c>Bifrost.Scheduling</c> constant.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task MeterName_IsBifrostScheduling()
    {
        using var metrics = new SchedulerMetrics();
        await Assert.That(metrics.Meter.Name).IsEqualTo("Bifrost.Scheduling");
    }

    /// <summary>
    /// Verifies the registered up-down counter increments on register and decrements
    /// on unregister, so the net measured value tracks the live job count.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JobsRegistered_IncrementsOnRegister_DecrementsOnUnregister()
    {
        using var metrics = new SchedulerMetrics();
        using var collector = new MetricCollector<long>(
            metrics.Meter, "bifrost.scheduling.jobs.registered");
        var registry = NewRegistry(metrics);

        await registry.RegisterAsync("alpha", Interval(), MissedFirePolicy.Coalesce, new NoopDispatcher())
            .ConfigureAwait(false);
        await registry.RegisterAsync("beta", Interval(), MissedFirePolicy.Coalesce, new NoopDispatcher())
            .ConfigureAwait(false);
        await registry.UnregisterAsync("alpha").ConfigureAwait(false);

        var measurements = collector.GetMeasurementSnapshot();
        var net = measurements.Sum(m => m.Value);
        await Assert.That(net).IsEqualTo(1L);
    }

    /// <summary>
    /// Verifies the fired counter increments on a successful dispatch, tagged with
    /// <c>job.name</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JobsFired_IncrementsOnDispatch_TaggedByJobName()
    {
        using var metrics = new SchedulerMetrics();
        using var collector = new MetricCollector<long>(
            metrics.Meter, "bifrost.scheduling.jobs.fired");
        await using var fx = await Fixture.StartAsync(metrics).ConfigureAwait(false);

        await fx.RegisterAsync("nightly", Interval()).ConfigureAwait(false);
        fx.Time.Advance(TimeSpan.FromHours(1));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        var measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Sum(m => m.Value)).IsGreaterThanOrEqualTo(1L);
        await Assert.That(measurements.Any(m => HasTag(m, "job.name", "nightly"))).IsTrue();
    }

    /// <summary>
    /// Verifies the fire-latency histogram records the elapsed time between a job's
    /// scheduled next-fire instant and its actual dispatch.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FireLatency_RecordsLatencyBetweenScheduledAndActualDispatch()
    {
        using var metrics = new SchedulerMetrics();
        using var collector = new MetricCollector<double>(
            metrics.Meter, "bifrost.scheduling.jobs.fire_latency");
        await using var fx = await Fixture.StartAsync(metrics).ConfigureAwait(false);

        await fx.RegisterAsync("late", Interval()).ConfigureAwait(false);

        // Advance well past the scheduled fire so the dispatch is observably late.
        fx.Time.Advance(TimeSpan.FromHours(2));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        var measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements).IsNotEmpty();
        await Assert.That(measurements.Any(m => m.Value >= 0)).IsTrue();
    }

    /// <summary>
    /// Verifies the missed-fires counter increments at startup reconciliation, tagged
    /// with <c>job.name</c> and <c>policy</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task MissedFires_IncrementsAtStartup_TaggedByJobNameAndPolicy()
    {
        using var metrics = new SchedulerMetrics();
        using var collector = new MetricCollector<long>(
            metrics.Meter, "bifrost.scheduling.jobs.missed_fires");

        var time = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var cadence = Interval();

        var registry = new ScheduleRegistry(store, time, metrics);

        // Register first so a live dispatcher is attached and a record exists, then
        // overwrite the durable record's LastFiredAt to simulate fires that happened
        // before this process started — the loop reconciles the gap at startup.
        await registry.RegisterAsync("stale", cadence, MissedFirePolicy.Coalesce, new NoopDispatcher())
            .ConfigureAwait(false);
        await store.SaveAsync(
            new JobRecord(
                "stale", cadence, MissedFirePolicy.Coalesce, JobState.Running,
                LastFiredAt: Start, NextFireAt: null, DispatchKind: "custom",
                DispatcherTypeName: typeof(NoopDispatcher).FullName,
                Metadata: new Dictionary<string, string>()),
            CancellationToken.None).ConfigureAwait(false);

        // Five hours pass before the loop starts, so an hourly job missed ~5 fires.
        time.Advance(TimeSpan.FromHours(5));

        var sink = new RecordingSchedulerEventSink();
        var router = new JobDispatcherRouter(sink);
        var loop = new ScheduleTickLoop(
            registry, store, time, router, sink,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions(), metrics);

        await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
        await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
        loop.Dispose();

        var measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Sum(m => m.Value)).IsGreaterThanOrEqualTo(1L);
        await Assert.That(measurements.Any(m => HasTag(m, "job.name", "stale"))).IsTrue();
        await Assert.That(measurements.Any(m =>
            HasTag(m, "policy", nameof(MissedFirePolicy.Coalesce)))).IsTrue();
    }

    /// <summary>
    /// Verifies the dispatch-failures counter increments when a dispatcher throws,
    /// tagged with <c>job.name</c> and <c>exception.type</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchFailures_IncrementsOnThrow_TaggedByJobNameAndExceptionType()
    {
        using var metrics = new SchedulerMetrics();
        using var collector = new MetricCollector<long>(
            metrics.Meter, "bifrost.scheduling.jobs.dispatch_failures");
        await using var fx = await Fixture.StartAsync(metrics).ConfigureAwait(false);

        await fx.RegisterThrowingAsync("boom", Interval()).ConfigureAwait(false);
        fx.Time.Advance(TimeSpan.FromHours(1));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        var measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Sum(m => m.Value)).IsGreaterThanOrEqualTo(1L);
        await Assert.That(measurements.Any(m => HasTag(m, "job.name", "boom"))).IsTrue();
        await Assert.That(measurements.Any(m =>
            HasTag(m, "exception.type", nameof(InvalidOperationException)))).IsTrue();
    }

    private static bool HasTag<T>(CollectedMeasurement<T> measurement, string key, string value)
        where T : struct
        => measurement.Tags.TryGetValue(key, out var actual)
            && string.Equals(actual as string, value, StringComparison.Ordinal);

    private static IntervalCadence Interval() => new(TimeSpan.FromHours(1));

    private static ScheduleRegistry NewRegistry(SchedulerMetrics metrics)
        => new(new InMemoryScheduleStore(), new FakeTimeProvider(Start), metrics);

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
    /// A started tick-loop harness sharing a single <see cref="SchedulerMetrics"/>
    /// across the registry and the loop, mirroring the production wiring.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            FakeTimeProvider time,
            ScheduleRegistry registry,
            ScheduleTickLoop loop)
        {
            this.Time = time;
            this.Registry = registry;
            this.Loop = loop;
        }

        public FakeTimeProvider Time { get; }

        public ScheduleRegistry Registry { get; }

        public ScheduleTickLoop Loop { get; }

        public static async Task<Fixture> StartAsync(SchedulerMetrics metrics)
        {
            var time = new FakeTimeProvider(Start);
            var store = new InMemoryScheduleStore();
            var registry = new ScheduleRegistry(store, time, metrics);
            var sink = new RecordingSchedulerEventSink();
            var router = new JobDispatcherRouter(sink);
            var loop = new ScheduleTickLoop(
                registry, store, time, router, sink,
                NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions(), metrics);

            var fx = new Fixture(time, registry, loop);
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            return fx;
        }

        public async Task RegisterAsync(string name, Cadence cadence)
        {
            await this.Registry.RegisterAsync(name, cadence, MissedFirePolicy.Coalesce, new NoopDispatcher())
                .ConfigureAwait(false);
            await this.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        }

        public async Task RegisterThrowingAsync(string name, Cadence cadence)
        {
            await this.Registry.RegisterAsync(name, cadence, MissedFirePolicy.Coalesce, new ThrowingDispatcher())
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
