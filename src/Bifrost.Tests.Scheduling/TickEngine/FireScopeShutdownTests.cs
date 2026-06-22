// =============================================================================
// <copyright file="FireScopeShutdownTests.cs" company="Levelup Software">
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
/// Regression coverage for the orphan-fire / stranded-in-flight invariant when a per-fire
/// <see cref="IServiceScope"/> cannot be created mid-shutdown (#32, G6b, U-5). The per-fire
/// path in <see cref="ScheduleTickLoop"/> publishes <see cref="JobFiredEvent"/> BEFORE
/// opening the scope; when <c>CreateScope()</c> faults (only possible once the root provider
/// is disposed, e.g. mid-shutdown) the loop publishes a compensating
/// <see cref="JobFireFailedEvent"/>, leaving a complete Fired→Failed timeline rather than an
/// orphan Fired. Because the in-flight count is incremented only AFTER the scope is created,
/// a scope fault strands nothing — the loop stays idle-reachable and unfaulted.
/// </summary>
/// <remarks>
/// The production seam and a single-fire assertion already live in <c>FireFaultEdgeTests</c>;
/// this file is the explicit, named regression for the two named invariants — every
/// published <see cref="JobFiredEvent"/> gets a terminal event, and the in-flight count is
/// never stranded — proven across more than one fire and a subsequent successful drain.
/// </remarks>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class FireScopeShutdownTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Forces <c>CreateScope</c> to fault for the first two fires (the mid-shutdown race) and
    /// then succeed, proving: (1) every <see cref="JobFiredEvent"/> is paired with a terminal
    /// <see cref="JobFireFailedEvent"/> (no orphan Fired); (2) the dispatcher never runs while
    /// the scope is faulting; (3) the loop is never faulted by the benign shutdown race; and
    /// (4) the in-flight count is never stranded — once the scope succeeds, the fire drains
    /// the loop back to idle and the dispatcher runs, which a stranded (&gt; 0) count would
    /// prevent the idle barrier from ever observing.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CreateScopeFaultsMidShutdown_EveryFiredGetsTerminalEvent_InFlightNotStranded()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        var sink = new RecordingSchedulerEventSink();
        var router = new JobDispatcherRouter(sink);
        var scopeProvider = new ToggleableScopeProvider { ThrowCount = 2 };
        var dispatcher = new CountingDispatcher();
        var loop = new ScheduleTickLoop(
            registry, store, time, router, sink,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions(),
            serviceProvider: scopeProvider);

        try
        {
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await registry.RegisterAsync(
                "job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, dispatcher)
                .ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Fires #1 and #2: scope creation faults. Each must publish Fired then a
            // compensating Failed, run no dispatcher, strand no in-flight count, and not
            // fault the loop. The loop reaching idle each time proves nothing is stranded.
            time.Advance(TimeSpan.FromMinutes(1));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            time.Advance(TimeSpan.FromMinutes(1));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await Assert.That(dispatcher.FireCount).IsEqualTo(0);
            await Assert.That(loop.IsFaulted).IsFalse();

            // Fire #3: scope creation now succeeds; the dispatcher runs and the fire drains
            // the loop back to idle. A count stranded by either prior scope-fault fire would
            // leave in-flight > 0 and the idle barrier would never release here.
            time.Advance(TimeSpan.FromMinutes(1));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await Assert.That(dispatcher.FireCount).IsEqualTo(1);

            // Three fires published their handoff signal; the two scope-fault fires each got a
            // compensating terminal event, so every Fired has a matching outcome (U-5).
            var fired = sink.Published.OfType<JobFiredEvent>().Count();
            var failed = sink.Published.OfType<JobFireFailedEvent>().Count();
            await Assert.That(fired).IsEqualTo(3);
            await Assert.That(failed).IsEqualTo(2);
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
        }
    }

    /// <summary>
    /// An <see cref="IServiceProvider"/> that hands back itself as the scope factory and
    /// throws on the first <see cref="ThrowCount"/> scope requests (simulating a root provider
    /// disposed mid-shutdown), then creates real scopes from an empty provider so a later fire
    /// can run. Models the transient mid-shutdown window where the first fires race a disposed
    /// provider while a subsequent fire (or a differently-timed instance) succeeds.
    /// </summary>
    private sealed class ToggleableScopeProvider : IServiceProvider, IServiceScopeFactory
    {
        private readonly ServiceProvider real = new ServiceCollection().BuildServiceProvider();
        private int scopeRequests;

        public int ThrowCount { get; init; }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceScopeFactory) ? this : null;

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref this.scopeRequests) <= this.ThrowCount)
            {
                throw new InvalidOperationException("Per-fire scope creation failed mid-shutdown (test).");
            }

            return this.real.GetRequiredService<IServiceScopeFactory>().CreateScope();
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
