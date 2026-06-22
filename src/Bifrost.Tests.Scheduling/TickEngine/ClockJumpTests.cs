// =============================================================================
// <copyright file="ClockJumpTests.cs" company="Levelup Software">
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
/// Tests for backward-clock-jump re-arm in the <see cref="ScheduleTickLoop"/>
/// (Task 48, DR-10, quartznet#1508/#2034): when the clock jumps backward mid-wait,
/// the loop must never sleep on a stale absolute deadline, and must re-arm its delay
/// within one wait cycle.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class ClockJumpTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    // A real root provider so each fire opens (and disposes) a genuine per-fire scope
    // (F2/M2) — exercises the per-fire scope lifecycle in this fixture's tick loop.
    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that when the clock jumps backward by days mid-wait, the loop
    /// re-arms within one wait cycle and does not sleep on the stale absolute
    /// deadline (quartznet#1508/#2034).
    /// </summary>
    /// <remarks>
    /// The <see cref="FakeTimeProvider"/> does not support moving backwards, so a
    /// <see cref="SkewableTimeProvider"/> wrapper is used to inject a negative skew
    /// into <c>GetUtcNow()</c> while leaving timer creation (and thus
    /// <c>FakeTimeProvider.Advance</c>) on the underlying fake clock. After the
    /// skew is cleared the loop must re-arm at the correct delay against the
    /// (un-skewed) current time, so a subsequent advance fires it exactly once.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task TickLoop_BackwardClockJump_ReArmsWithinOneWaitCycle()
    {
        var time = new FakeTimeProvider(Start);
        var skewable = new SkewableTimeProvider(time);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, skewable);
        var sink = new RecordingSchedulerEventSink();
        var router = new JobDispatcherRouter(sink);
        var loop = new ScheduleTickLoop(
            registry, store, skewable, router, sink,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions(),
            serviceProvider: Services);

        await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Register a job due in 5 minutes.
            var dispatcher = new CountingDispatcher();
            await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(5)), MissedFirePolicy.Coalesce, dispatcher)
                .ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Inject a backward skew of 3 days. The loop's next GetUtcNow() read sees a
            // time far in the past. The loop must detect the regression (log warning) and
            // re-arm against the skewed time (extending the delay). It must NOT hang on a
            // stale timer that will never fire.
            skewable.SkewBy(TimeSpan.FromDays(-3));

            // Wake the loop (idle barrier) so it re-evaluates with the backward clock.
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Clear the skew and let the loop settle.
            skewable.SkewBy(TimeSpan.Zero);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Now advance past the original scheduled instant: must fire exactly once.
            time.Advance(TimeSpan.FromMinutes(5));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            await Assert.That(dispatcher.FireCount).IsEqualTo(1);
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
        }
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> that wraps a <see cref="FakeTimeProvider"/> and
    /// can apply a signed skew to <c>GetUtcNow()</c> while delegating timer creation
    /// to the inner provider, so <c>FakeTimeProvider.Advance</c> still fires timers.
    /// </summary>
    private sealed class SkewableTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private long skewTicks;

        public override DateTimeOffset GetUtcNow() =>
            inner.GetUtcNow() + TimeSpan.FromTicks(Volatile.Read(ref this.skewTicks));

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);

        public void SkewBy(TimeSpan skew) => Volatile.Write(ref this.skewTicks, skew.Ticks);
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
}
