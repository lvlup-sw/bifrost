// =============================================================================
// <copyright file="CommandWakeTests.cs" company="Levelup Software">
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
/// No-lost-wakeup tests for the cached command-readiness wait (core-2): the loop
/// reuses a single <c>WaitToReadAsync</c> task across non-command wakes to avoid a
/// per-wake allocation. These tests prove a registry command still wakes the loop in
/// the situations the reuse must handle — when the loop is parked on a delay timer
/// (heap non-empty) and across a sequence of commands that each complete-and-recreate
/// the cached wait.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class CommandWakeTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    // A real root provider so each fire opens a genuine per-fire scope (F2/M2).
    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies a registry command (a second registration) wakes the loop even while it
    /// is parked on a delay timer for a previously-armed job — the cached command wait,
    /// pending from the first job's park, must complete on the new command. The newly
    /// registered job then fires at its occurrence.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CommandArrivesWhileParkedOnTimer_WakesLoop_NoLostWakeup()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);

        // First job: arms a far-future timer, so the loop parks on the delay task while
        // the cached command wait stays pending.
        var first = await fx.RegisterAsync("first", Cadence.Interval(TimeSpan.FromHours(1))).ConfigureAwait(false);

        // Second registration is a registry command that must wake the loop off its
        // delay park via the (reused) command wait.
        var second = await fx.RegisterAsync("second", Cadence.Interval(TimeSpan.FromMinutes(1))).ConfigureAwait(false);

        // The second job's occurrence comes due first; it must fire (proving the command
        // armed it and the loop re-parked on the nearer timer).
        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(second.FireCount).IsEqualTo(1);
        await Assert.That(first.FireCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies a sequence of registry commands each wakes the loop. After the first
    /// command completes the cached wait, the loop must recreate a fresh wait that the
    /// next command completes in turn — exercising the complete-then-recreate path.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SequentialCommands_EachWakesLoop_NoLostWakeup()
    {
        await using var fx = await Fixture.StartAsync().ConfigureAwait(false);

        // Three registrations in a row; each is a distinct command wake. The fixture's
        // RegisterAsync waits for idle after each, so a lost wakeup would hang the test.
        var a = await fx.RegisterAsync("a", Cadence.Interval(TimeSpan.FromMinutes(1))).ConfigureAwait(false);
        var b = await fx.RegisterAsync("b", Cadence.Interval(TimeSpan.FromMinutes(1))).ConfigureAwait(false);
        var c = await fx.RegisterAsync("c", Cadence.Interval(TimeSpan.FromMinutes(1))).ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(a.FireCount).IsEqualTo(1);
        await Assert.That(b.FireCount).IsEqualTo(1);
        await Assert.That(c.FireCount).IsEqualTo(1);
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
