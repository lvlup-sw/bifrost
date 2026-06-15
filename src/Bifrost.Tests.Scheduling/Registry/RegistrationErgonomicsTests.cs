// =============================================================================
// <copyright file="RegistrationErgonomicsTests.cs" company="Levelup Software">
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

namespace Bifrost.Tests.Scheduling.Registry;

/// <summary>
/// Tests for R10 registration ergonomics (Task 51, DR-1): a past
/// <see cref="OneShotCadence"/> is rejected at registration, mutating operations
/// (re-register, update) never fire as a side effect, and
/// <see cref="IScheduleRegistry.TriggerAsync"/> is the only fire-on-demand API.
/// References: quartznet#636/#2180, Hangfire#1637, quartznet#1545.
/// </summary>
[ParallelLimiter<TickEngine.TickEngineParallelLimit>]
public sealed class RegistrationErgonomicsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that registering a <see cref="Cadence.At(DateTimeOffset)"/> cadence
    /// whose instant is in the past throws at registration — never a silent immediate
    /// fire (quartznet#636/#2180, Hangfire#1637, R10).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RegisterAsync_AtCadenceInPast_Throws()
    {
        var fakeTime = new FakeTimeProvider(Now);
        var registry = new ScheduleRegistry(new InMemoryScheduleStore(), fakeTime);
        var pastCadence = Cadence.At(Now - TimeSpan.FromSeconds(1));

        await Assert.That(async () =>
                await registry.RegisterAsync(
                        "past-job", pastCadence, MissedFirePolicy.Coalesce, new NoopDispatcher())
                    .ConfigureAwait(false))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Verifies that re-registering a job with an unchanged schedule (via
    /// unregister + re-register) causes ZERO dispatches before the next legitimate
    /// occurrence, not an immediate fire (quartznet#1545).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ReRegister_UnchangedSchedule_NeverFiresImmediately()
    {
        var fakeTime = new FakeTimeProvider(Now);
        await using var fx = await Fixture.StartAsync(fakeTime).ConfigureAwait(false);
        var cadence = Cadence.At(Now.AddMinutes(10));

        // Register the job initially.
        var dispatcher = new CountingDispatcher();
        await fx.Registry.RegisterAsync("reregister-job", cadence, MissedFirePolicy.Coalesce, dispatcher)
            .ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // Unregister then re-register with the same cadence (unchanged schedule).
        await fx.Registry.UnregisterAsync("reregister-job").ConfigureAwait(false);
        await fx.Registry.RegisterAsync("reregister-job", cadence, MissedFirePolicy.Coalesce, dispatcher)
            .ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // No fires should have occurred yet — the next legitimate fire is at T+10m.
        await Assert.That(dispatcher.FireCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that updating a job's cadence to a future schedule via
    /// <see cref="IScheduleRegistry.UpdateAsync"/> never fires as a side effect —
    /// <see cref="IScheduleRegistry.TriggerAsync"/> is the ONLY fire-on-demand API
    /// (R10).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Update_FutureSchedule_NeverFiresImmediately()
    {
        var fakeTime = new FakeTimeProvider(Now);
        await using var fx = await Fixture.StartAsync(fakeTime).ConfigureAwait(false);
        var initialCadence = Cadence.Interval(TimeSpan.FromHours(1));
        var futureCadence = Cadence.At(Now.AddMinutes(30));

        var dispatcher = new CountingDispatcher();
        await fx.Registry.RegisterAsync("update-job", initialCadence, MissedFirePolicy.Coalesce, dispatcher)
            .ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // Update to a future cadence — must not fire immediately.
        await fx.Registry.UpdateAsync("update-job", futureCadence, MissedFirePolicy.Coalesce)
            .ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // No fires should have occurred — the updated cadence fires at T+30m.
        await Assert.That(dispatcher.FireCount).IsEqualTo(0);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(ScheduleRegistry registry, ScheduleTickLoop loop)
        {
            this.Registry = registry;
            this.Loop = loop;
        }

        public ScheduleRegistry Registry { get; }

        public ScheduleTickLoop Loop { get; }

        public static async Task<Fixture> StartAsync(FakeTimeProvider fakeTime)
        {
            var store = new InMemoryScheduleStore();
            var registry = new ScheduleRegistry(store, fakeTime);
            var sink = new NullSchedulerEventSink();
            var router = new JobDispatcherRouter(sink);
            var loop = new ScheduleTickLoop(
                registry,
                store,
                fakeTime,
                router,
                sink,
                NullLogger<ScheduleTickLoop>.Instance,
                new SchedulerOptions());

            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            return new Fixture(registry, loop);
        }

        public async ValueTask DisposeAsync()
        {
            await ((IHostedService)this.Loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            this.Loop.Dispose();
        }
    }

    private sealed class NoopDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) => ValueTask.CompletedTask;
    }

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

    private sealed class NullSchedulerEventSink : ISchedulerEventSink
    {
        public void Publish<TEvent>(in TEvent evt) where TEvent : struct { }
    }
}
