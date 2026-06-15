// =============================================================================
// <copyright file="FireFaultEdgeTests.cs" company="Levelup Software">
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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// Edge-case fault tests for the fire path hardened in the review fix cycle:
/// a per-fire scope-creation fault is surfaced as a compensating failed fire rather
/// than an orphan <see cref="JobFiredEvent"/> (no loop fault), and a non-conforming
/// router that both runs the dispatch and then throws cannot double-decrement the
/// in-flight count (the idle barrier never strands).
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class FireFaultEdgeTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that when the per-fire <see cref="IServiceScope"/> cannot be created
    /// (e.g. the root provider is disposed mid-shutdown), the loop publishes a
    /// compensating <see cref="JobFireFailedEvent"/> after the already-published
    /// <see cref="JobFiredEvent"/> — a complete Fired→Failed timeline, not an orphan
    /// Fired — the dispatcher is never invoked, and the loop reaches idle (no hang, no
    /// stranded in-flight count, no loop fault).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CreateScopeThrows_PublishesFiredThenFailed_DispatcherNotRun_LoopContinues()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        var sink = new RecordingSchedulerEventSink();
        var router = new JobDispatcherRouter(sink);
        var loop = new ScheduleTickLoop(
            registry, store, time, router, sink,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions(),
            serviceProvider: new ThrowingScopeProvider());

        try
        {
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            var dispatcher = new CountingDispatcher();
            await registry.RegisterAsync(
                "job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, dispatcher)
                .ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Make the job due; the per-fire scope creation faults. This must NOT hang.
            time.Advance(TimeSpan.FromMinutes(1));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // The handoff Fired event was published, then a compensating Failed event —
            // a complete timeline rather than an orphan Fired.
            await Assert.That(sink.Published.OfType<JobFiredEvent>().Count()).IsEqualTo(1);
            await Assert.That(sink.Published.OfType<JobFireFailedEvent>().Count()).IsEqualTo(1);

            // The dispatcher never ran (the scope failed before the handoff).
            await Assert.That(dispatcher.FireCount).IsEqualTo(0);
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
        }
    }

    /// <summary>
    /// Verifies that a non-conforming <see cref="IJobDispatcherRouter"/> that runs the
    /// dispatch to completion (so the decorator's <c>finally</c> completes the fire) and
    /// then throws synchronously (so the loop's handoff-fault path also tries to complete
    /// it) decrements the in-flight count exactly once. If the single-shot guard were
    /// missing the count would underflow to -1 and the idle barrier would never release,
    /// so the loop reaching idle is the assertion.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RouterCompletesDispatchThenThrows_InFlightBalanced_LoopRecoversToIdle()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        var sink = new RecordingSchedulerEventSink();
        var dispatcher = new CountingDispatcher();
        var loop = new ScheduleTickLoop(
            registry, store, time, new CompleteThenThrowRouter(),
            sink, NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions());

        try
        {
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await registry.RegisterAsync(
                "job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, dispatcher)
                .ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // One due fire: the router runs the dispatch (in-flight -> finally completes)
            // then throws (handoff-fault path attempts a second completion). The loop
            // faults and restarts; it must reach idle again — which requires the in-flight
            // count to be exactly zero, not -1.
            time.Advance(TimeSpan.FromMinutes(1));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // The dispatch actually ran exactly once.
            await Assert.That(dispatcher.FireCount).IsEqualTo(1);
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
        }
    }

    /// <summary>
    /// An <see cref="IServiceProvider"/> that hands back itself as the scope factory and
    /// always throws when a scope is requested — simulating a root provider disposed
    /// while a fire is in progress.
    /// </summary>
    private sealed class ThrowingScopeProvider : IServiceProvider, IServiceScopeFactory
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceScopeFactory) ? this : null;

        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("Per-fire scope creation failed (test).");
    }

    /// <summary>
    /// A non-conforming router that runs the dispatch synchronously to completion and then
    /// throws, exercising both the decorator's completion <c>finally</c> and the loop's
    /// handoff-fault completion for a single fire.
    /// </summary>
    private sealed class CompleteThenThrowRouter : IJobDispatcherRouter
    {
        public void Dispatch(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct)
        {
            // Drive the (synchronous) dispatch to completion so the decorator's finally
            // runs its completion, then throw so the loop's catch attempts a second one.
            // The test dispatcher completes synchronously, so this never actually blocks.
#pragma warning disable VSTHRD002 // Intentional sync wait: the test dispatcher is synchronous.
            dispatcher.DispatchAsync(context, ct).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            throw new InvalidOperationException("Router threw after starting the dispatch (test).");
        }
    }

    /// <summary>A dispatcher that counts how many times it ran.</summary>
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
