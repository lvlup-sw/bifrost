// =============================================================================
// <copyright file="PrFixRouterPublishGuardTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Dispatch;

namespace Bifrost.Tests.Scheduling.Dispatch;

/// <summary>
/// Regression tests for PR #25 CodeRabbit FIX B2: the failure-event
/// <see cref="ISchedulerEventSink.Publish{TEvent}(in TEvent)"/> inside
/// <see cref="JobDispatcherRouter"/>'s dispatcher-failure <c>catch</c> runs on a
/// fire-and-forget pool task. If <c>Publish</c> itself throws, that task must not
/// fault: dispatcher-failure isolation must hold even when the sink is broken, and
/// the router must remain usable for subsequent dispatches.
/// </summary>
[ParallelLimiter<TickEngine.TickEngineParallelLimit>]
public sealed class PrFixRouterPublishGuardTests
{
    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset FireTime =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies that when the event sink throws from <c>Publish</c> while the router is
    /// isolating a throwing dispatcher, the router swallows the publish fault: no
    /// unobserved/faulted task surfaces, the router is still usable, and a later
    /// dispatch with a working code path still completes (its dispatcher runs).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test registers a <c>TaskScheduler.UnobservedTaskException</c> handler, so any
    /// fire-and-forget task fault that escapes becomes an observed test failure. The broken sink
    /// must not cause an unhandled or faulted task from the router.
    /// </para>
    /// <para>
    /// The first dispatcher throws. The router catches the exception and calls <c>sink.Publish</c>,
    /// which also throws. The fix wraps that <c>Publish</c> call in its own swallow.
    /// </para>
    /// <para>
    /// If the <c>Publish</c> throw at catch time had escaped, the pool task would have faulted. The
    /// router instance is stateless, so the proof that it remains usable is that a new dispatch
    /// (<c>working</c>) still runs to completion.
    /// </para>
    /// <para>
    /// The <c>GC.Collect</c>, <c>GC.WaitForPendingFinalizers</c>, <c>GC.Collect</c> sequence forces
    /// the runtime to collect any faulted but unobserved task and raise its event. The finalizer
    /// gets a short grace window (250 ms). No unobserved task exception must come from the
    /// fire-and-forget path of the router.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ThrowingSink_DispatcherFailure_DoesNotFault_RouterRemainsUsable()
    {
        var sink = new ThrowingSink();
        var router = new JobDispatcherRouter(sink);

        var unobserved = new TaskCompletionSource();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            e.SetObserved();
            unobserved.TrySetResult();
        };
        TaskScheduler.UnobservedTaskException += handler;

        try
        {
            var firstRan = new TaskCompletionSource();
            var throwing = new DelegateDispatcher(_ =>
            {
                firstRan.TrySetResult();
                throw new InvalidOperationException("injected dispatch failure");
            });

            router.Dispatch(throwing, Context("first"), CancellationToken.None);
            await firstRan.Task.WaitAsync(TestTimeout);

            var secondRan = new TaskCompletionSource();
            var working = new DelegateDispatcher(_ => secondRan.TrySetResult());
            router.Dispatch(working, Context("second"), CancellationToken.None);
            await secondRan.Task.WaitAsync(TestTimeout);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var raised = await Task.WhenAny(unobserved.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
            await Assert.That(raised == unobserved.Task).IsFalse();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    private static JobFireContext Context(string jobName) =>
        new(jobName, FireTime, FireTime.AddMinutes(1), EmptyProvider.Instance);

    /// <summary>An event sink whose <c>Publish</c> always throws.</summary>
    private sealed class ThrowingSink : ISchedulerEventSink
    {
        public void Publish<TEvent>(in TEvent schedulerEvent)
            where TEvent : struct =>
            throw new InvalidOperationException("injected sink Publish failure");
    }

    /// <summary>A dispatcher that runs a supplied action on dispatch.</summary>
    private sealed class DelegateDispatcher(Action<JobFireContext> onDispatch) : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            onDispatch(context);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyProvider : IServiceProvider
    {
        public static readonly EmptyProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
