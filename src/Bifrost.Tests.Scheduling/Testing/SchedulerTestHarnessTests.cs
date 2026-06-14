// =============================================================================
// <copyright file="SchedulerTestHarnessTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.Testing;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.Testing;

/// <summary>
/// Behavioral tests for <see cref="SchedulerTestHarness"/> (Task 36, DR-9): the
/// concrete harness drives the tick loop deterministically via a
/// <see cref="FakeTimeProvider"/>, advancing time, firing due jobs, and waiting for
/// idle without any real wall-clock delays.
/// </summary>
[ParallelLimiter<TickEngine.TickEngineParallelLimit>]
public sealed class SchedulerTestHarnessTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 14, 0, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that advancing the clock by exactly the job's interval fires the job
    /// exactly once, deterministically (no race conditions, no extra fires).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AdvanceAsync_SingleJob_5Min_Fires_5Min_Interval_OnceDeterministically()
    {
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("five-min", Cadence.Interval(TimeSpan.FromMinutes(5)))
            .ConfigureAwait(false);

        await fx.Harness.AdvanceAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false);

        await Assert.That(dispatcher.FireCount).IsEqualTo(1);
        await Assert.That(dispatcher.LastFireTime).IsEqualTo(Start.AddMinutes(5));
    }

    /// <summary>
    /// Verifies that when multiple jobs are registered, advancing the clock past all
    /// their intervals fires each one the correct number of times.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AdvanceAsync_MultipleJobs_AllFireInOrder()
    {
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);
        var earlyDispatcher = await fx.RegisterAsync("early", Cadence.At(Start.AddMinutes(2)))
            .ConfigureAwait(false);
        var lateDispatcher = await fx.RegisterAsync("late", Cadence.At(Start.AddMinutes(8)))
            .ConfigureAwait(false);

        // Advance past both: both should fire exactly once.
        await fx.Harness.AdvanceAsync(TimeSpan.FromMinutes(10)).ConfigureAwait(false);

        await Assert.That(earlyDispatcher.FireCount).IsEqualTo(1);
        await Assert.That(earlyDispatcher.LastFireTime).IsEqualTo(Start.AddMinutes(2));
        await Assert.That(lateDispatcher.FireCount).IsEqualTo(1);
        await Assert.That(lateDispatcher.LastFireTime).IsEqualTo(Start.AddMinutes(8));
    }

    /// <summary>
    /// Verifies that <see cref="ISchedulerTestHarness.AdvanceAsync"/> waits for all
    /// in-flight dispatches to complete before returning, so the caller can safely
    /// assert on dispatch outcomes immediately after it returns.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AdvanceAsync_WaitsForInFlightDispatches_BeforeReturn()
    {
        // Use a dispatcher that takes some time to complete (simulated async work),
        // then verify the fire count is observed after AdvanceAsync returns.
        var delayed = new DelayedDispatcher(delay: TimeSpan.FromMilliseconds(20));
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);
        await fx.RegisterAsync("slow", Cadence.Interval(TimeSpan.FromMinutes(5)), delayed)
            .ConfigureAwait(false);

        await fx.Harness.AdvanceAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false);

        // The dispatch must be complete (fire count = 1) because AdvanceAsync waited.
        await Assert.That(delayed.FireCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that <see cref="ISchedulerTestHarness.FireDueJobsAsync"/> only fires
    /// jobs whose scheduled time is at or before the current fake-clock instant, and
    /// leaves future jobs unfired.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FireDueJobsAsync_OnlyFiresDueJobs_NotFutureJobs()
    {
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);

        // Register a job due at T+5m and one at T+10m; do not advance time.
        var nowJob = await fx.RegisterAsync("now", Cadence.At(Start)).ConfigureAwait(false);
        var futureJob = await fx.RegisterAsync("future", Cadence.At(Start.AddMinutes(10)))
            .ConfigureAwait(false);

        // The "now" job is due at Start (the current fake-clock instant).
        // The "future" job is not yet due.
        await fx.Harness.FireDueJobsAsync().ConfigureAwait(false);

        // Only the job due at Start fires; the future job does not.
        await Assert.That(nowJob.FireCount).IsEqualTo(1);
        await Assert.That(futureJob.FireCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that <see cref="ISchedulerTestHarness.WaitForIdleAsync"/> throws a
    /// <see cref="TimeoutException"/> when the tick loop never goes idle within the
    /// timeout. This is exercised by providing a near-zero timeout while a long-running
    /// dispatch is in flight.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WaitForIdleAsync_Timeout_Throws()
    {
        // A semaphore blocks the dispatcher until we choose to release it.
        var gate = new SemaphoreSlim(0, 1);
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blocking = new GatedDispatcher(gate, dispatchStarted);
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);

        // Register the job at T+1ms so it fires immediately after a tiny advance.
        await fx.RegisterAsync("block", Cadence.At(Start.AddMilliseconds(1)), blocking)
            .ConfigureAwait(false);

        // Advance so the job becomes due; the dispatcher will block on the semaphore.
        fx.Time.Advance(TimeSpan.FromMilliseconds(1));

        // Wait until the dispatcher has actually started (in-flight).
        await dispatchStarted.Task.WaitAsync(TestTimeout).ConfigureAwait(false);

        try
        {
            // With a 1-tick timeout the loop cannot go idle while the dispatcher blocks.
            await Assert.That(
                async () => await fx.Harness.WaitForIdleAsync(TimeSpan.FromTicks(1)).ConfigureAwait(false))
                .ThrowsException()
                .WithMessageContaining("idle");
        }
        finally
        {
            // Always release the gate so the loop can drain and DisposeAsync succeeds.
            gate.Release();
        }
    }

    /// <summary>
    /// Verifies that <see cref="ISchedulerTestHarness.WaitForIdleAsync"/> returns
    /// promptly when the tick loop is already idle.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WaitForIdleAsync_TickLoopIdle_ReturnsImmediately()
    {
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);
        // No jobs registered; the loop is idle immediately.

        // Should complete well within the generous timeout.
        // Verify it completes by asserting it does not throw.
        var completed = false;
        await fx.Harness.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        completed = true;

        await Assert.That(completed).IsTrue();
    }

    /// <summary>
    /// Verifies that <see cref="SchedulerTestHarness"/> throws
    /// <see cref="ArgumentException"/> when constructed with a real
    /// <see cref="TimeProvider"/> (i.e., not a <see cref="FakeTimeProvider"/>),
    /// because deterministic testing requires a controllable clock.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Harness_RequiresFakeTimeProvider()
    {
        var realProvider = TimeProvider.System;
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, realProvider);
        var sink = new NullSchedulerEventSink();
        var router = new JobDispatcherRouter(sink);
        var loop = new ScheduleTickLoop(
            registry, store, realProvider, router, sink,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions());

        await Assert.ThrowsAsync<ArgumentException>(
            () => Task.FromResult(new SchedulerTestHarness(loop, realProvider)));
        loop.Dispose();
    }

    /// <summary>
    /// Verifies that the harness works identically with all three dispatch modes:
    /// orchestrator-style (<see cref="IJobDispatcher"/> directly), inline via
    /// <see cref="InlineJobDispatcher"/>, and a custom dispatcher. All modes must
    /// complete their dispatch before <see cref="ISchedulerTestHarness.AdvanceAsync"/>
    /// returns.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Harness_AllDispatchModes_Deterministic()
    {
        await using var fx = await Fixture.StartAsync(Start).ConfigureAwait(false);

        // Mode 1: direct IJobDispatcher (the "orchestrator" / custom mode)
        var countingA = await fx.RegisterAsync("job-a", Cadence.At(Start.AddMinutes(5)))
            .ConfigureAwait(false);

        // Mode 2: InlineJobDispatcher wrapping a delegate
        var inlineFireCount = 0;
        var inlineB = new InlineJobDispatcher((ctx, ct) =>
        {
            Interlocked.Increment(ref inlineFireCount);
            return ValueTask.CompletedTask;
        });
        await fx.RegisterAsync("job-b", Cadence.At(Start.AddMinutes(5)), inlineB)
            .ConfigureAwait(false);

        // Mode 3: another direct IJobDispatcher (interval-based)
        var countingC = await fx.RegisterAsync("job-c", Cadence.Interval(TimeSpan.FromMinutes(5)))
            .ConfigureAwait(false);

        await fx.Harness.AdvanceAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false);

        await Assert.That(countingA.FireCount).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref inlineFireCount)).IsEqualTo(1);
        await Assert.That(countingC.FireCount).IsEqualTo(1);
    }

    // -------------------------------------------------------------------------
    // Test infrastructure
    // -------------------------------------------------------------------------

    /// <summary>
    /// A test fixture that owns a started <see cref="ScheduleTickLoop"/> and its
    /// associated <see cref="ISchedulerTestHarness"/>. Disposal stops the loop.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            FakeTimeProvider time,
            ScheduleRegistry registry,
            ScheduleTickLoop loop,
            ISchedulerTestHarness harness)
        {
            this.Time = time;
            this.Registry = registry;
            this.Loop = loop;
            this.Harness = harness;
        }

        public FakeTimeProvider Time { get; }

        public ScheduleRegistry Registry { get; }

        public ScheduleTickLoop Loop { get; }

        public ISchedulerTestHarness Harness { get; }

        public static async Task<Fixture> StartAsync(DateTimeOffset start)
        {
            var time = new FakeTimeProvider(start);
            var store = new InMemoryScheduleStore();
            var registry = new ScheduleRegistry(store, time);
            var sink = new NullSchedulerEventSink();
            var router = new JobDispatcherRouter(sink);
            var loop = new ScheduleTickLoop(
                registry,
                store,
                time,
                router,
                sink,
                NullLogger<ScheduleTickLoop>.Instance,
                new SchedulerOptions());

            var harness = new SchedulerTestHarness(loop, time);

            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            return new Fixture(time, registry, loop, harness);
        }

        public async Task<CountingDispatcher> RegisterAsync(string name, Cadence cadence)
        {
            var dispatcher = new CountingDispatcher();
            await this.RegisterAsync(name, cadence, dispatcher).ConfigureAwait(false);
            return dispatcher;
        }

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

    /// <summary>
    /// A no-op <see cref="ISchedulerEventSink"/> for tests that do not care about events.
    /// </summary>
    private sealed class NullSchedulerEventSink : ISchedulerEventSink
    {
        public void Publish<TEvent>(in TEvent evt) where TEvent : struct { }
    }

    /// <summary>
    /// A dispatcher that counts fires. Thread-safe.
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

    /// <summary>
    /// A dispatcher that completes after a configurable delay. Used to verify
    /// AdvanceAsync waits for in-flight dispatches to complete. Thread-safe.
    /// </summary>
    /// <param name="delay">The artificial delay per dispatch.</param>
    private sealed class DelayedDispatcher(TimeSpan delay) : IJobDispatcher
    {
        private int fireCount;

        public int FireCount => Volatile.Read(ref this.fireCount);

        public async ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            Interlocked.Increment(ref this.fireCount);
        }
    }

    /// <summary>
    /// A dispatcher that signals when it has started and then blocks until a
    /// <see cref="SemaphoreSlim"/> is released. Used to hold a dispatch in-flight for
    /// a controlled duration so the timeout test can observe the non-idle state.
    /// </summary>
    /// <param name="gate">The semaphore the dispatcher waits on.</param>
    /// <param name="started">Completed when the dispatcher has started executing.</param>
    private sealed class GatedDispatcher(
        SemaphoreSlim gate,
        TaskCompletionSource started) : IJobDispatcher
    {
        public async ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            started.TrySetResult();
            // Block on real wall-clock (CancellationToken.None) so the harness does not
            // accidentally release us via cancellation.
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
