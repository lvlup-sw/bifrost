// =============================================================================
// <copyright file="EventOrderingTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
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
/// Tests for the fire-event timeline (core-1): the dispatch-handoff signal
/// <see cref="JobFiredEvent"/> must always precede any per-fire outcome event for the
/// same occurrence — including a <see cref="JobFireFailedEvent"/> from a synchronously
/// failing dispatcher. A subscriber correlating on <c>(JobName, FireTime)</c> must never
/// observe the failure before the handoff.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class EventOrderingTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that when a dispatcher fails synchronously (publishing a
    /// <see cref="JobFireFailedEvent"/> from inside <c>DispatchAsync</c> on the pool
    /// thread before any await), the loop's <see cref="JobFiredEvent"/> handoff signal
    /// is still observed FIRST on the timeline — the failure cannot precede the handoff
    /// for the same occurrence.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SynchronousFailure_JobFiredEvent_PrecedesJobFireFailedEvent()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        var sink = new OrderRecordingSink();

        // A router that runs the dispatch INLINE (synchronously) within Dispatch, so the
        // ordering is deterministic and not subject to a pool-thread scheduling race: a
        // synchronous failure is published while still inside router.Dispatch. With the
        // buggy order (router.Dispatch before the JobFiredEvent publish) this guarantees
        // the failure precedes the handoff; the fix publishes JobFiredEvent first.
        var router = new InlineRouter();
        var services = new ServiceCollection().BuildServiceProvider();
        var loop = new ScheduleTickLoop(
            registry, store, time, router, sink,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions(),
            serviceProvider: services);

        try
        {
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // A dispatcher that publishes a JobFireFailedEvent synchronously (as the
            // orchestrator does on an admission rejection) — no throw, no await.
            var dispatcher = new SyncFailingDispatcher(sink);
            await registry.RegisterAsync(
                "job", Cadence.Interval(TimeSpan.FromMinutes(5)), MissedFirePolicy.Coalesce, dispatcher)
                .ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            time.Advance(TimeSpan.FromMinutes(5));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            var firedIndex = sink.FirstIndexOf<JobFiredEvent>();
            var failedIndex = sink.FirstIndexOf<JobFireFailedEvent>();

            await Assert.That(firedIndex).IsGreaterThanOrEqualTo(0);
            await Assert.That(failedIndex).IsGreaterThanOrEqualTo(0);

            // The handoff signal must come first on the timeline.
            await Assert.That(firedIndex).IsLessThan(failedIndex);
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
            await services.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A router that runs the dispatch synchronously and inline within
    /// <see cref="Dispatch"/>, rather than handing it to the pool. This removes the
    /// pool-thread scheduling race so the relative order of a synchronous failure event
    /// and the loop's handoff event is deterministic. The dispatcher under test
    /// completes synchronously, so the inline await never blocks a real wait.
    /// </summary>
    private sealed class InlineRouter : IJobDispatcherRouter
    {
        public void Dispatch(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct)
        {
            // The dispatcher under test completes synchronously, so the fire (and its
            // synchronous failure publication) is fully observed by the time Dispatch
            // returns — no blocking wait is needed.
            var pending = dispatcher.DispatchAsync(context, ct);
            if (!pending.IsCompletedSuccessfully)
            {
                // Defensive: a synchronous test dispatcher should never reach here.
                throw new InvalidOperationException(
                    "InlineRouter expected the dispatch to complete synchronously.");
            }
        }
    }

    /// <summary>
    /// An event sink that records the global publication order of every event so a test
    /// can assert relative ordering across the tick thread and the pool dispatch thread.
    /// </summary>
    private sealed class OrderRecordingSink : ISchedulerEventSink
    {
        private readonly object gate = new();
        private readonly List<object> published = [];

        public void Publish<TEvent>(in TEvent schedulerEvent)
            where TEvent : struct
        {
            lock (this.gate)
            {
                this.published.Add(schedulerEvent);
            }
        }

        /// <summary>
        /// Returns the publication index of the first event of the given type, or -1.
        /// </summary>
        /// <typeparam name="TEvent">The event type to find.</typeparam>
        /// <returns>The zero-based publication index, or -1 if none.</returns>
        public int FirstIndexOf<TEvent>()
            where TEvent : struct
        {
            lock (this.gate)
            {
                for (var i = 0; i < this.published.Count; i++)
                {
                    if (this.published[i] is TEvent)
                    {
                        return i;
                    }
                }

                return -1;
            }
        }
    }

    /// <summary>
    /// A dispatcher that mimics a synchronous admission rejection: it publishes a
    /// <see cref="JobFireFailedEvent"/> from inside <c>DispatchAsync</c> (no throw) and
    /// returns a completed task, the way the orchestrator dispatcher does on a
    /// <c>Rejected</c> result.
    /// </summary>
    private sealed class SyncFailingDispatcher(ISchedulerEventSink sink) : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            sink.Publish(new JobFireFailedEvent(
                context.JobName,
                context.FireTime,
                Exception: null,
                Reason: "Synchronous admission rejection (test)."));
            return ValueTask.CompletedTask;
        }
    }
}
