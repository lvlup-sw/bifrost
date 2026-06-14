// =============================================================================
// <copyright file="GracefulShutdownTests.cs" company="Levelup Software">
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
/// Tests for the <see cref="ScheduleTickLoop"/>'s graceful shutdown (Task 28,
/// DR-10): <c>StopAsync</c> stops scheduling, waits up to
/// <see cref="SchedulerOptions.ShutdownTimeout"/> for in-flight dispatches, abandons
/// any that exceed the window, is idempotent, and never throws when stopped before
/// it fully started.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class GracefulShutdownTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    // A real root provider so each fire opens (and disposes) a genuine per-fire scope
    // (F2/M2) — exercises the scope lifecycle against the in-flight drain path.
    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies <c>StopAsync</c> completes promptly when nothing is in flight.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task NoInFlight_StopCompletesPromptly()
    {
        var fx = await Fixture.StartAsync().ConfigureAwait(false);

        // A bounded wait: StopAsync must return well inside the timeout when nothing
        // is in flight (the assertion is that this does not throw a TimeoutException).
        var stop = ((IHostedService)fx.Loop).StopAsync(CancellationToken.None);
        await Assert.That(async () => await stop.WaitAsync(TestTimeout).ConfigureAwait(false))
            .ThrowsNothing();
        fx.Loop.Dispose();
    }

    /// <summary>
    /// Verifies <c>StopAsync</c> waits for an in-flight dispatch that completes within
    /// the shutdown window.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task InFlightCompletesWithinWindow_StopWaitsForIt()
    {
        var dispatcher = new GatedDispatcher();
        var fx = await Fixture.StartAsync().ConfigureAwait(false);
        await fx.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), dispatcher).ConfigureAwait(false);

        // Make the job due and let the loop hand the (blocking) dispatch to the pool.
        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.WaitUntilEnteredAsync(TestTimeout).ConfigureAwait(false);

        var stop = ((IHostedService)fx.Loop).StopAsync(CancellationToken.None);
        await Assert.That(stop.IsCompleted).IsFalse();

        // Releasing the dispatch lets the in-flight fire complete; StopAsync then returns.
        dispatcher.Release();
        await stop.ConfigureAwait(false);
        await Assert.That(dispatcher.Completed).IsTrue();
        fx.Loop.Dispose();
    }

    /// <summary>
    /// Verifies a dispatch exceeding the shutdown window is abandoned and
    /// <c>StopAsync</c> returns after the timeout.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task InFlightExceedsTimeout_StopReturnsAfterTimeout()
    {
        var dispatcher = new GatedDispatcher();
        var fx = await Fixture.StartAsync(
            new SchedulerOptions { ShutdownTimeout = TimeSpan.FromSeconds(5) }).ConfigureAwait(false);
        await fx.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), dispatcher).ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.WaitUntilEnteredAsync(TestTimeout).ConfigureAwait(false);

        // Stop while the dispatch is still blocked. Advancing past the shutdown timeout
        // makes StopAsync abandon the in-flight dispatch and return.
        var stop = ((IHostedService)fx.Loop).StopAsync(CancellationToken.None);
        await Assert.That(stop.IsCompleted).IsFalse();

        fx.Time.Advance(TimeSpan.FromSeconds(5));
        await stop.ConfigureAwait(false);

        // The dispatch never completed: it was abandoned.
        await Assert.That(dispatcher.Completed).IsFalse();
        dispatcher.Release();
        fx.Loop.Dispose();
    }

    /// <summary>
    /// Verifies no dispatch occurs after <c>StopAsync</c>, even as time advances.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AfterStop_AdvancingTime_NoDispatch()
    {
        var dispatcher = new CountingDispatcher();
        var fx = await Fixture.StartAsync().ConfigureAwait(false);
        await fx.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(5)), dispatcher).ConfigureAwait(false);

        await ((IHostedService)fx.Loop).StopAsync(CancellationToken.None).ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromMinutes(30));
        await Assert.That(dispatcher.FireCount).IsEqualTo(0);
        fx.Loop.Dispose();
    }

    /// <summary>
    /// Verifies <c>StopAsync</c> is idempotent — calling it twice does not throw.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task StopAsync_CalledTwice_IsIdempotent()
    {
        var fx = await Fixture.StartAsync().ConfigureAwait(false);

        await ((IHostedService)fx.Loop).StopAsync(CancellationToken.None).ConfigureAwait(false);

        await Assert.That(async () =>
                await ((IHostedService)fx.Loop).StopAsync(CancellationToken.None).ConfigureAwait(false))
            .ThrowsNothing();
        fx.Loop.Dispose();
    }

    /// <summary>
    /// Verifies <c>StopAsync</c> before <c>StartAsync</c> never throws (NCronJob#172).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task StopBeforeStart_DoesNotThrow()
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

        await Assert.That(async () =>
                await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false))
            .ThrowsNothing();
        loop.Dispose();
    }

    /// <summary>
    /// A test harness owning a started <see cref="ScheduleTickLoop"/>.
    /// </summary>
    private sealed class Fixture
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

        public static async Task<Fixture> StartAsync(SchedulerOptions? options = null)
        {
            var time = new FakeTimeProvider(Start);
            var store = new InMemoryScheduleStore();
            var registry = new ScheduleRegistry(store, time);
            var sink = new RecordingSchedulerEventSink();
            var router = new JobDispatcherRouter(sink);
            var loop = new ScheduleTickLoop(
                registry, store, time, router, sink,
                NullLogger<ScheduleTickLoop>.Instance, options ?? new SchedulerOptions(),
                serviceProvider: Services);

            var fx = new Fixture(time, registry, loop);
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
    }

    /// <summary>
    /// A dispatcher that blocks inside <c>DispatchAsync</c> until released, so a test
    /// can hold a fire "in flight" deterministically.
    /// </summary>
    private sealed class GatedDispatcher : IJobDispatcher
    {
        private readonly TaskCompletionSource entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int completed;

        public bool Completed => Volatile.Read(ref this.completed) == 1;

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Usage",
            "VSTHRD003:Avoid awaiting foreign Tasks",
            Justification = "The release task is an intentional test gate that holds " +
                "the dispatch in flight until the test releases it.")]
        public async ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            this.entered.TrySetResult();
            await this.release.Task.ConfigureAwait(false);
            Volatile.Write(ref this.completed, 1);
        }

        public Task WaitUntilEnteredAsync(TimeSpan timeout) =>
            this.entered.Task.WaitAsync(timeout);

        public void Release() => this.release.TrySetResult();
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
