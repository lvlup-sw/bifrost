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
        await using var fx = await Fixture.StartAsync(Start);
        var dispatcher = await fx.RegisterAsync("five-min", Cadence.Interval(TimeSpan.FromMinutes(5)));

        await fx.Harness.AdvanceAsync(TimeSpan.FromMinutes(5));

        await Assert.That(dispatcher.FireCount).IsEqualTo(1);
        await Assert.That(dispatcher.LastFireTime).IsEqualTo(Start.AddMinutes(5));
    }

    /// <summary>
    /// Verifies that when multiple jobs are registered, advancing the clock past all
    /// their intervals fires each one the correct number of times.
    /// </summary>
    /// <remarks>
    /// The advance passes both jobs, so each must fire exactly once.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AdvanceAsync_MultipleJobs_AllFireInOrder()
    {
        await using var fx = await Fixture.StartAsync(Start);
        var earlyDispatcher = await fx.RegisterAsync("early", Cadence.At(Start.AddMinutes(2)));
        var lateDispatcher = await fx.RegisterAsync("late", Cadence.At(Start.AddMinutes(8)));

        await fx.Harness.AdvanceAsync(TimeSpan.FromMinutes(10));

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
    /// <remarks>
    /// The dispatcher takes some time to complete (simulated asynchronous work of 20 ms). The fire
    /// count must be 1 after <c>AdvanceAsync</c> returns, because <c>AdvanceAsync</c> waited for
    /// the dispatch.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AdvanceAsync_WaitsForInFlightDispatches_BeforeReturn()
    {
        var delayed = new DelayedDispatcher(delay: TimeSpan.FromMilliseconds(20));
        await using var fx = await Fixture.StartAsync(Start);
        await fx.RegisterAsync("slow", Cadence.Interval(TimeSpan.FromMinutes(5)), delayed);

        await fx.Harness.AdvanceAsync(TimeSpan.FromMinutes(5));

        await Assert.That(delayed.FireCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that <see cref="ISchedulerTestHarness.FireDueJobsAsync"/> only fires
    /// jobs whose scheduled time is at or before the current fake-clock instant, and
    /// leaves future jobs unfired.
    /// </summary>
    /// <remarks>
    /// The test registers one job due at Start (the current fake-clock instant) and one job due at
    /// T+10m, and does not advance time. Only the job due at Start fires. The future job is not yet
    /// due and does not fire.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FireDueJobsAsync_OnlyFiresDueJobs_NotFutureJobs()
    {
        await using var fx = await Fixture.StartAsync(Start);

        var nowJob = await fx.RegisterAsync("now", Cadence.At(Start));
        var futureJob = await fx.RegisterAsync("future", Cadence.At(Start.AddMinutes(10)));

        await fx.Harness.FireDueJobsAsync();

        await Assert.That(nowJob.FireCount).IsEqualTo(1);
        await Assert.That(futureJob.FireCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that <see cref="ISchedulerTestHarness.WaitForIdleAsync"/> throws a
    /// <see cref="TimeoutException"/> when the tick loop never goes idle within the
    /// timeout. This is exercised by providing a near-zero timeout while a long-running
    /// dispatch is in flight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A semaphore blocks the dispatcher until the test releases it. The job is due at T+1ms, so it
    /// fires after a tiny advance, and then the dispatcher blocks on the semaphore. The test waits
    /// until the dispatcher has actually started (in flight). With a timeout of 1 tick, the loop
    /// cannot go idle while the dispatcher blocks.
    /// </para>
    /// <para>
    /// The <c>finally</c> block always releases the gate, so the loop can drain and
    /// <c>DisposeAsync</c> succeeds.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WaitForIdleAsync_Timeout_Throws()
    {
        var gate = new SemaphoreSlim(0, 1);
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blocking = new GatedDispatcher(gate, dispatchStarted);
        await using var fx = await Fixture.StartAsync(Start);

        await fx.RegisterAsync("block", Cadence.At(Start.AddMilliseconds(1)), blocking);

        fx.Time.Advance(TimeSpan.FromMilliseconds(1));

        await dispatchStarted.Task.WaitAsync(TestTimeout);

        try
        {
            await Assert.That(
                async () => await fx.Harness.WaitForIdleAsync(TimeSpan.FromTicks(1)))
                .ThrowsException()
                .WithMessageContaining("idle");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Verifies that <see cref="ISchedulerTestHarness.WaitForIdleAsync"/> returns
    /// promptly when the tick loop is already idle.
    /// </summary>
    /// <remarks>
    /// No job is registered, so the loop is idle immediately. The wait must complete well within
    /// the generous timeout, and the test shows this by asserting that it does not throw.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WaitForIdleAsync_TickLoopIdle_ReturnsImmediately()
    {
        await using var fx = await Fixture.StartAsync(Start);

        var completed = false;
        await fx.Harness.WaitForIdleAsync(TestTimeout);
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
    /// <remarks>
    /// Mode 1 (<c>job-a</c>) is a direct <see cref="IJobDispatcher"/>, the "orchestrator" or custom
    /// mode. Mode 2 (<c>job-b</c>) is an <see cref="InlineJobDispatcher"/> that wraps a delegate.
    /// Mode 3 (<c>job-c</c>) is another direct <see cref="IJobDispatcher"/>, with an interval
    /// cadence.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Harness_AllDispatchModes_Deterministic()
    {
        await using var fx = await Fixture.StartAsync(Start);

        var countingA = await fx.RegisterAsync("job-a", Cadence.At(Start.AddMinutes(5)));

        var inlineFireCount = 0;
        var inlineB = new InlineJobDispatcher((ctx, ct) =>
        {
            Interlocked.Increment(ref inlineFireCount);
            return ValueTask.CompletedTask;
        });
        await fx.RegisterAsync("job-b", Cadence.At(Start.AddMinutes(5)), inlineB);

        var countingC = await fx.RegisterAsync("job-c", Cadence.Interval(TimeSpan.FromMinutes(5)));

        await fx.Harness.AdvanceAsync(TimeSpan.FromMinutes(5));

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

            await ((IHostedService)loop).StartAsync(CancellationToken.None);
            await loop.WaitForIdleAsync(TestTimeout);

            return new Fixture(time, registry, loop, harness);
        }

        public async Task<CountingDispatcher> RegisterAsync(string name, Cadence cadence)
        {
            var dispatcher = new CountingDispatcher();
            await this.RegisterAsync(name, cadence, dispatcher);
            return dispatcher;
        }

        public async Task RegisterAsync(string name, Cadence cadence, IJobDispatcher dispatcher)
        {
            await this.Registry.RegisterAsync(name, cadence, MissedFirePolicy.Coalesce, dispatcher);
            await this.Loop.WaitForIdleAsync(TestTimeout);
        }

        public async ValueTask DisposeAsync()
        {
            await ((IHostedService)this.Loop).StopAsync(CancellationToken.None);
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
            await Task.Delay(delay, ct);
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
        // Blocks on the gate without a cancellation token (CancellationToken.None), so the harness
        // cannot release the dispatcher by accident through cancellation.
        public async ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            started.TrySetResult();
            await gate.WaitAsync(CancellationToken.None);
        }
    }
}
