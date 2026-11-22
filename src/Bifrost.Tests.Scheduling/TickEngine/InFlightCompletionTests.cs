// =============================================================================
// <copyright file="InFlightCompletionTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Linq;

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// Regression coverage for the in-flight completion single-shot guard (#32, G6a).
/// <see cref="ScheduleTickLoop"/>'s nested <c>InFlightTrackingDispatcher</c> clears the
/// in-flight count and disposes the per-fire scope EXACTLY once, even when a
/// non-conforming <see cref="IJobDispatcherRouter"/> both runs the dispatch (so the
/// decorator's <c>finally</c> completes the fire) and then throws synchronously (so the
/// loop's handoff-fault path also tries to complete it). Without the guard the second
/// completion would underflow the in-flight count to -1 and the idle/drain barrier would
/// never release again.
/// </summary>
/// <remarks>
/// The production guard and a "loop recovers to idle" assertion already live in
/// <c>FireFaultEdgeTests</c>; this file is the explicit, named regression for the
/// double-decrement invariant. The in-flight count is a private field with no test
/// accessor (by design), so the proof is structural: after the double-completion fault,
/// a fresh normal fire must still drain the loop back to idle — which can only happen
/// when the count is exactly zero, never -1. A second drained fire is the assertion the
/// count never went negative.
/// </remarks>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class InFlightCompletionTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Drives a non-conforming router that runs the dispatch to completion AND then throws
    /// synchronously for a single fire, then proves the in-flight count returned to zero
    /// exactly once: the first fire's dispatch ran exactly once, and a SECOND, normally
    /// routed fire afterwards still drains the loop back to idle. A count stranded at -1 by
    /// a double-decrement would never let the idle barrier release on the second fire, so
    /// the second <see cref="ScheduleTickLoop.WaitForIdleAsync"/> returning is the
    /// no-underflow proof.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first fire goes through the non-conforming router (complete, then throw). After the loop
    /// recovers from the fault and restarts, the router routes normally, so the second fire can
    /// drain. Both fires exercise the same <c>InFlightTrackingDispatcher</c> completion path.
    /// </para>
    /// <para>
    /// On fire #1, the router runs the dispatch. The <c>finally</c> of the decorator completes the
    /// fire, and in-flight goes to 0. Then the router throws, and the handoff-fault path attempts a
    /// second completion. The single-shot guard makes the second completion a no-op, so the count is 0,
    /// not -1. The loop faults on the router throw, restarts, and reaches idle.
    /// </para>
    /// <para>
    /// Fire #2 is routed normally. If fire #1 had underflowed the count to -1, this increment and
    /// decrement would settle at -1. The idle barrier of the loop releases only when in-flight ==
    /// 0, so it would never complete, and this <c>WaitForIdleAsync</c> would time out. Its return
    /// is the exact-once, no-underflow proof.
    /// </para>
    /// <para>
    /// Both fires published their handoff signal. The faulted first fire is also observable. The
    /// dispatcher itself does not throw, so the dispatch publishes no <see
    /// cref="JobFireFailedEvent"/>: the fault is the synchronous throw of the router.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RouterCompletesThenThrows_InFlightReturnsToZeroExactlyOnce_SubsequentFireDrains()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        var sink = new RecordingSchedulerEventSink();

        var router = new CompleteThenThrowOnceRouter(new JobDispatcherRouter(sink));
        var dispatcher = new CountingDispatcher();
        var loop = new ScheduleTickLoop(
            registry, store, time, router, sink,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions());

        try
        {
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await registry.RegisterAsync(
                "job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, dispatcher)
                .ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            time.Advance(TimeSpan.FromMinutes(1));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await Assert.That(dispatcher.FireCount).IsEqualTo(1);

            time.Advance(TimeSpan.FromMinutes(1));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await Assert.That(dispatcher.FireCount).IsEqualTo(2);

            await Assert.That(sink.Published.OfType<JobFiredEvent>().Count()).IsEqualTo(2);
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
        }
    }

    /// <summary>
    /// A non-conforming router that, on its FIRST dispatch only, runs the dispatch
    /// synchronously to completion and then throws — exercising both the decorator's
    /// completion <c>finally</c> and the loop's handoff-fault completion for one fire.
    /// Every subsequent dispatch is delegated to the inner (conforming) router so the loop
    /// can recover and a later fire can drain normally.
    /// </summary>
    /// <param name="inner">The conforming router used after the first (faulting) dispatch.</param>
    private sealed class CompleteThenThrowOnceRouter(IJobDispatcherRouter inner) : IJobDispatcherRouter
    {
        private int dispatchCount;

        // On the first fire, drives the synchronous dispatch to completion, so the finally of the
        // decorator runs its completion. Then it throws, so the catch of the loop attempts a
        // second completion. The test dispatcher completes synchronously, so the sync wait
        // (VSTHRD002, intentional) never blocks.
        public void Dispatch(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct)
        {
            if (Interlocked.Increment(ref this.dispatchCount) > 1)
            {
                inner.Dispatch(dispatcher, context, ct);
                return;
            }

#pragma warning disable VSTHRD002
            dispatcher.DispatchAsync(context, ct).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            throw new InvalidOperationException("Router threw after starting the dispatch (test).");
        }
    }

    /// <summary>A dispatcher that counts how many times it ran. Thread-safe (pool dispatch).</summary>
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
