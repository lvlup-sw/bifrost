// =============================================================================
// <copyright file="JobDispatcherRouterTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Dispatch;

namespace Bifrost.Tests.Scheduling.Dispatch;

/// <summary>
/// Tests for <see cref="JobDispatcherRouter"/> (Task 22): the dispatch executor
/// that hands a fire off to a pool thread, invokes the job's
/// <see cref="IJobDispatcher"/>, and isolates a throwing dispatcher as a
/// <see cref="JobFireFailedEvent"/> so it cannot crash the tick loop.
/// </summary>
public sealed class JobDispatcherRouterTests
{
    private static readonly DateTimeOffset FireTime =
        new(2026, 6, 13, 2, 0, 0, TimeSpan.Zero);

    private static JobFireContext Context(string jobName = "job") =>
        new(jobName, FireTime, FireTime.AddHours(1), new StubServiceProvider());

    /// <summary>
    /// Waits until <paramref name="condition"/> holds or the timeout elapses,
    /// polling so a pool-thread dispatch can be observed without a fixed sleep.
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return condition();
    }

    /// <summary>
    /// Verifies the router invokes the provided dispatcher's
    /// <see cref="IJobDispatcher.DispatchAsync"/> with the given context.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Dispatch_InvokesDispatcher_WithGivenContext()
    {
        var sink = new RecordingSchedulerEventSink();
        var dispatcher = new RecordingDispatcher();
        var router = new JobDispatcherRouter(sink);
        var ctx = Context("nightly");

        router.Dispatch(dispatcher, ctx, CancellationToken.None);

        var observed = await WaitUntilAsync(() => dispatcher.Invocations > 0).ConfigureAwait(false);
        await Assert.That(observed).IsTrue();
        await Assert.That(dispatcher.LastContext).IsEqualTo(ctx);
    }

    /// <summary>
    /// Verifies the dispatcher runs on a thread other than the caller's: the
    /// router hands off to the pool so the tick thread never awaits user code.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Dispatch_RunsDispatcherOnPoolThread_NotCallingThread()
    {
        var sink = new RecordingSchedulerEventSink();
        var dispatcher = new RecordingDispatcher();
        var router = new JobDispatcherRouter(sink);

        var callerThreadId = Environment.CurrentManagedThreadId;
        router.Dispatch(dispatcher, Context(), CancellationToken.None);

        var observed = await WaitUntilAsync(() => dispatcher.Invocations > 0).ConfigureAwait(false);
        await Assert.That(observed).IsTrue();
        await Assert.That(dispatcher.RanOnThreadId).IsNotEqualTo(callerThreadId);
        await Assert.That(dispatcher.RanOnPoolThread).IsTrue();
    }

    /// <summary>
    /// Verifies a throwing dispatcher is surfaced as a
    /// <see cref="JobFireFailedEvent"/> carrying the exception, and is not
    /// propagated to the caller (so it cannot crash the tick loop).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Dispatch_DispatcherThrows_PublishesJobFireFailedEvent_DoesNotPropagate()
    {
        var sink = new RecordingSchedulerEventSink();
        var boom = new InvalidOperationException("dispatch boom");
        var dispatcher = new ThrowingDispatcher(boom);
        var router = new JobDispatcherRouter(sink);

        // Must not throw to the caller (the tick thread).
        router.Dispatch(dispatcher, Context("crasher"), CancellationToken.None);

        var observed = await WaitUntilAsync(() => sink.Any<JobFireFailedEvent>()).ConfigureAwait(false);
        await Assert.That(observed).IsTrue();

        var failed = sink.Single<JobFireFailedEvent>();
        await Assert.That(failed.JobName).IsEqualTo("crasher");
        await Assert.That(failed.Exception).IsSameReferenceAs(boom);
        await Assert.That(failed.FiredAt).IsEqualTo(FireTime);
    }

    /// <summary>
    /// Verifies distinct <see cref="IJobDispatcher"/> implementations are each
    /// invoked correctly by the same router.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Dispatch_DistinctDispatcherImplementations_AreEachInvoked()
    {
        var sink = new RecordingSchedulerEventSink();
        var router = new JobDispatcherRouter(sink);

        var first = new RecordingDispatcher();
        var second = new RecordingDispatcher();

        router.Dispatch(first, Context("a"), CancellationToken.None);
        router.Dispatch(second, Context("b"), CancellationToken.None);

        var observed = await WaitUntilAsync(
            () => first.Invocations > 0 && second.Invocations > 0).ConfigureAwait(false);

        await Assert.That(observed).IsTrue();
        await Assert.That(first.LastContext.JobName).IsEqualTo("a");
        await Assert.That(second.LastContext.JobName).IsEqualTo("b");
    }

    /// <summary>
    /// A minimal non-null <see cref="IServiceProvider"/> to carry through the
    /// fire context.
    /// </summary>
    private sealed class StubServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>
    /// Records each dispatch, the context, and the thread it ran on.
    /// </summary>
    private sealed class RecordingDispatcher : IJobDispatcher
    {
        private int invocations;

        public int Invocations => Volatile.Read(ref this.invocations);

        public JobFireContext LastContext { get; private set; }

        public int RanOnThreadId { get; private set; }

        public bool RanOnPoolThread { get; private set; }

        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            this.LastContext = context;
            this.RanOnThreadId = Environment.CurrentManagedThreadId;
            this.RanOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            Interlocked.Increment(ref this.invocations);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A dispatcher that always throws, to exercise failure isolation.
    /// </summary>
    private sealed class ThrowingDispatcher(Exception toThrow) : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) =>
            throw toThrow;
    }
}
