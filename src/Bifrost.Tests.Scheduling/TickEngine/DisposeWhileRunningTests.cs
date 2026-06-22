// =============================================================================
// <copyright file="DisposeWhileRunningTests.cs" company="Levelup Software">
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
/// Tests for disposing the <see cref="ScheduleRegistry"/> while its
/// <see cref="ScheduleTickLoop"/> is still running (review HIGH-1). Disposing the
/// registry completes its command-channel writer; because that channel is single-reader
/// the loop must observe the completion as a clean-stop signal and exit, rather than
/// re-parking on the already-completed channel and hot-spinning the CPU.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class DisposeWhileRunningTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 9, 0, 0, TimeSpan.Zero);

    // A real root provider so each fire opens (and disposes) a genuine per-fire scope.
    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that disposing the registry while the loop runs over an EMPTY heap — and
    /// WITHOUT cancelling the loop's stopping token — stops the loop. The loop's
    /// <see cref="BackgroundService.ExecuteTask"/> must complete, proving it exited
    /// cleanly on command-channel completion. Without the HIGH-1 fix the empty-heap wait
    /// returns synchronously over the completed channel and the loop re-parks instantly in
    /// a hot spin, so <c>ExecuteTask</c> never completes and the bounded wait times out.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DisposeRegistry_EmptyHeap_WithoutCancel_StopsLoop()
    {
        var (loop, registry) = await StartLoopAsync().ConfigureAwait(false);

        // The loop is parked idle on an empty heap (no jobs, no stopping-token cancel).
        // Dispose the registry: its command-channel writer completes, which the loop must
        // treat as a clean stop.
        await registry.DisposeAsync().ConfigureAwait(false);

        var executeTask = ((BackgroundService)loop).ExecuteTask;
        await Assert.That(executeTask is not null).IsTrue();

        // With the fix the loop exits promptly; without it ExecuteTask never completes
        // (hot spin) and this bounded wait throws TimeoutException, failing the test.
        await Assert.That(async () => await executeTask!.WaitAsync(TestTimeout).ConfigureAwait(false))
            .ThrowsNothing();
        await Assert.That(executeTask!.IsCompleted).IsTrue();

        loop.Dispose();
    }

    /// <summary>
    /// Verifies the same clean stop when the heap is NON-empty (a job armed but not yet
    /// due): disposing the registry without cancelling the stopping token still stops the
    /// loop. This guards that the completion stop-condition is checked on the timed-wait
    /// path too, not only the empty-heap path.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DisposeRegistry_WithArmedJob_WithoutCancel_StopsLoop()
    {
        var (loop, registry) = await StartLoopAsync().ConfigureAwait(false);

        // Arm a job far in the future so it never fires; the loop parks on its timer.
        await registry.RegisterAsync(
                "future", Cadence.Interval(TimeSpan.FromHours(1)), MissedFirePolicy.Coalesce, new NoopDispatcher())
            .ConfigureAwait(false);
        await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await registry.DisposeAsync().ConfigureAwait(false);

        var executeTask = ((BackgroundService)loop).ExecuteTask;
        await Assert.That(executeTask is not null).IsTrue();
        await Assert.That(async () => await executeTask!.WaitAsync(TestTimeout).ConfigureAwait(false))
            .ThrowsNothing();
        await Assert.That(executeTask!.IsCompleted).IsTrue();

        loop.Dispose();
    }

    private static async Task<(ScheduleTickLoop Loop, ScheduleRegistry Registry)> StartLoopAsync()
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

        await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
        await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        return (loop, registry);
    }

    /// <summary>
    /// A dispatcher that does nothing; the armed job in these tests never fires.
    /// </summary>
    private sealed class NoopDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
