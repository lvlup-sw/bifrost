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
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ThrowingSink_DispatcherFailure_DoesNotFault_RouterRemainsUsable()
    {
        var sink = new ThrowingSink();
        var router = new JobDispatcherRouter(sink);

        // Surface any escaped fire-and-forget task fault as an observed test failure:
        // the broken sink must NOT produce an unhandled/faulted task from the router.
        var unobserved = new TaskCompletionSource();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            e.SetObserved();
            unobserved.TrySetResult();
        };
        TaskScheduler.UnobservedTaskException += handler;

        try
        {
            // A dispatcher that throws: the router catches it and calls sink.Publish,
            // which itself throws. The fix wraps that Publish in its own swallow.
            var firstRan = new TaskCompletionSource();
            var throwing = new DelegateDispatcher(_ =>
            {
                firstRan.TrySetResult();
                throw new InvalidOperationException("injected dispatch failure");
            });

            router.Dispatch(throwing, Context("first"), CancellationToken.None);
            await firstRan.Task.WaitAsync(TestTimeout).ConfigureAwait(false);

            // The router is still usable: a subsequent dispatch's dispatcher runs to
            // completion. If the catch-time Publish throw had escaped, the pool task
            // would have faulted; the router instance itself is stateless, so the proof
            // of "remains usable" is that a fresh dispatch still works end to end.
            var secondRan = new TaskCompletionSource();
            var working = new DelegateDispatcher(_ => secondRan.TrySetResult());
            router.Dispatch(working, Context("second"), CancellationToken.None);
            await secondRan.Task.WaitAsync(TestTimeout).ConfigureAwait(false);

            // Force any faulted-but-unobserved task to be collected and its event raised.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // No unobserved task exception should have been raised by the router's
            // fire-and-forget path. (Give the finalizer a brief grace window.)
            var raised = await Task.WhenAny(unobserved.Task, Task.Delay(TimeSpan.FromMilliseconds(250)))
                .ConfigureAwait(false);
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
