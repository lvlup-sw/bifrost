// =============================================================================
// <copyright file="JobFireScopeTests.cs" company="Levelup Software">
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
/// Tests for the real per-fire DI scope behind <see cref="JobFireContext.Services"/>
/// (F2/M2): each fire gets a live <see cref="IServiceScope"/> from the root provider, a
/// dispatcher resolves scoped services from it, each fire receives a fresh per-fire
/// instance, and the scope is disposed once the fire completes.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class JobFireScopeTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies a dispatcher can resolve a scoped service via
    /// <c>ctx.Services.GetRequiredService&lt;T&gt;()</c>, that two fires receive two
    /// distinct per-fire instances, and that each per-fire scope is disposed after the
    /// fire completes (the scoped service's disposal is recorded).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Fire_ResolvesScopedService_PerFireInstance_AndDisposesScope()
    {
        var tracker = new ScopeTracker();
        var services = new ServiceCollection()
            .AddSingleton(tracker)
            .AddScoped<ScopedProbe>()
            .BuildServiceProvider();

        var time = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        var sink = new RecordingSchedulerEventSink();
        var router = new JobDispatcherRouter(sink);
        var loop = new ScheduleTickLoop(
            registry, store, time, router, sink,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions(),
            serviceProvider: services);

        try
        {
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            var dispatcher = new ScopeResolvingDispatcher();
            await registry.RegisterAsync(
                "job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, dispatcher)
                .ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // First fire.
            time.Advance(TimeSpan.FromMinutes(1));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Second fire.
            time.Advance(TimeSpan.FromMinutes(1));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await Assert.That(dispatcher.FireCount).IsEqualTo(2);

            // The dispatcher resolved a non-null scoped probe on each fire.
            await Assert.That(dispatcher.ResolvedNonNullCount).IsEqualTo(2);

            // Each fire got a distinct per-fire instance (scoped, not singleton).
            await Assert.That(tracker.CreatedCount).IsEqualTo(2);
            await Assert.That(dispatcher.DistinctInstanceCount).IsEqualTo(2);

            // Both per-fire scopes were disposed after their fire completed.
            await Assert.That(tracker.DisposedCount).IsEqualTo(2);
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
            await services.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A singleton that counts how many <see cref="ScopedProbe"/> instances were created
    /// and disposed, so a test can assert per-fire instancing and scope disposal.
    /// </summary>
    private sealed class ScopeTracker
    {
        private int created;
        private int disposed;

        public int CreatedCount => Volatile.Read(ref this.created);

        public int DisposedCount => Volatile.Read(ref this.disposed);

        public void OnCreated() => Interlocked.Increment(ref this.created);

        public void OnDisposed() => Interlocked.Increment(ref this.disposed);
    }

    /// <summary>
    /// A scoped service that records its creation and disposal against the shared
    /// <see cref="ScopeTracker"/>. Implements <see cref="IAsyncDisposable"/> so the
    /// loop's preferred async-disposal path is exercised.
    /// </summary>
    private sealed class ScopedProbe : IAsyncDisposable
    {
        private readonly ScopeTracker tracker;

        public ScopedProbe(ScopeTracker tracker)
        {
            this.tracker = tracker;
            tracker.OnCreated();
        }

        public ValueTask DisposeAsync()
        {
            this.tracker.OnDisposed();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A dispatcher that resolves a <see cref="ScopedProbe"/> from the fire's per-fire
    /// service provider and tracks how many distinct instances it saw across fires.
    /// </summary>
    private sealed class ScopeResolvingDispatcher : IJobDispatcher
    {
        private readonly object gate = new();
        private readonly HashSet<ScopedProbe> seen = new(ReferenceEqualityComparer.Instance);
        private int fireCount;
        private int resolvedNonNullCount;

        public int FireCount => Volatile.Read(ref this.fireCount);

        public int ResolvedNonNullCount => Volatile.Read(ref this.resolvedNonNullCount);

        public int DistinctInstanceCount
        {
            get
            {
                lock (this.gate)
                {
                    return this.seen.Count;
                }
            }
        }

        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            var probe = context.Services.GetRequiredService<ScopedProbe>();
            if (probe is not null)
            {
                Interlocked.Increment(ref this.resolvedNonNullCount);
                lock (this.gate)
                {
                    this.seen.Add(probe);
                }
            }

            Interlocked.Increment(ref this.fireCount);
            return ValueTask.CompletedTask;
        }
    }
}
