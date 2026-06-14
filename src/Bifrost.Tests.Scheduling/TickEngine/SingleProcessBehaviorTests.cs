// =============================================================================
// <copyright file="SingleProcessBehaviorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// Tests for the <see cref="ScheduleTickLoop"/>'s always-leader single-process
/// behaviour and multi-instance warning (Task 27, DR-6): the loop unconditionally
/// ticks every registered job, and warns prominently at startup when the host opts
/// into multi-instance against the default non-exclusive store.
/// </summary>
public sealed class SingleProcessBehaviorTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies the default single-process loop ticks every registered job.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SingleProcessDefault_TicksEveryRegisteredJob()
    {
        await using var fx = await Fixture.StartAsync(new SchedulerOptions()).ConfigureAwait(false);
        var a = await fx.RegisterAsync("a", Cadence.Interval(TimeSpan.FromMinutes(5))).ConfigureAwait(false);
        var b = await fx.RegisterAsync("b", Cadence.Interval(TimeSpan.FromMinutes(5))).ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(a.FireCount).IsEqualTo(1);
        await Assert.That(b.FireCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies opting into multi-instance with the default store logs a prominent
    /// startup warning that duplicate fires occur.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task MultiInstanceExpected_LogsStartupWarning()
    {
        var logger = new CapturingLogger<ScheduleTickLoop>();
        await using var fx = await Fixture.StartAsync(
            new SchedulerOptions { MultiInstanceExpected = true }, logger).ConfigureAwait(false);

        await Assert.That(logger.Any(LogLevel.Warning)).IsTrue();
        await Assert.That(logger.Contains(LogLevel.Warning, "duplicate")).IsTrue();
    }

    /// <summary>
    /// Verifies the default options log no multi-instance warning.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DefaultOptions_LogsNoMultiInstanceWarning()
    {
        var logger = new CapturingLogger<ScheduleTickLoop>();
        await using var fx = await Fixture.StartAsync(new SchedulerOptions(), logger).ConfigureAwait(false);

        await Assert.That(logger.Contains(LogLevel.Warning, "duplicate")).IsFalse();
    }

    /// <summary>
    /// A test harness owning a started <see cref="ScheduleTickLoop"/> with a
    /// configurable <see cref="SchedulerOptions"/> and optional capturing logger.
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
            SchedulerOptions options,
            ILogger<ScheduleTickLoop>? logger = null)
        {
            var time = new FakeTimeProvider(Start);
            var store = new InMemoryScheduleStore();
            var registry = new ScheduleRegistry(store, time);
            var sink = new RecordingSchedulerEventSink();
            var router = new JobDispatcherRouter(sink);
            var loop = new ScheduleTickLoop(
                registry, store, time, router, sink,
                logger ?? new CapturingLogger<ScheduleTickLoop>(), options);

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
