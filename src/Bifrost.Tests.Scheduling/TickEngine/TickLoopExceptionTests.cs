// =============================================================================
// <copyright file="TickLoopExceptionTests.cs" company="Levelup Software">
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// Tests for the <see cref="ScheduleTickLoop"/>'s own-code fault recovery and clock
/// skew handling (Task 29, DR-10): a fault in the loop's own code logs critical,
/// publishes <see cref="SchedulerFaultedEvent"/>, and restarts; repeated restarts in
/// the window transition to the faulted state; and a non-monotonic clock reading
/// warns and continues without a lost or duplicate fire.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class TickLoopExceptionTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    // A real root provider so each fire opens (and disposes) a genuine per-fire scope
    // (F2/M2) — exercises the per-fire scope lifecycle in this fixture's tick loop.
    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies a fault in the loop's own code (a throwing event publish on a fire)
    /// logs critical, publishes <see cref="SchedulerFaultedEvent"/>, and the loop
    /// restarts and recovers — the job fires once the fault clears.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task OwnCodeFault_LogsCritical_PublishesFaultedEvent_AndRestarts()
    {
        var sink = new FaultInjectingEventSink { ThrowOnFiredCount = 1 };
        var logger = new CapturingLogger<ScheduleTickLoop>();
        await using var fx = await Fixture.StartAsync(sink, new SchedulerOptions(), logger).ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(5))).ConfigureAwait(false);

        // First fire: the publish throws once, faulting the loop body. The loop logs
        // critical, publishes a SchedulerFaultedEvent, and restarts.
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(logger.Any(LogLevel.Critical)).IsTrue();
        await Assert.That(sink.Any<SchedulerFaultedEvent>()).IsTrue();
        await Assert.That(fx.Loop.IsFaulted).IsFalse();

        // After the fault clears, the next occurrence fires normally.
        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(dispatcher.FireCount).IsGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Verifies three consecutive restart failures within the window transition the
    /// scheduler to its faulted state and stop ticking.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ThreeConsecutiveFaultsInWindow_TransitionsToFaulted_StopsTicking()
    {
        // Throw on every fire publish so each restart re-faults immediately.
        var sink = new FaultInjectingEventSink { ThrowOnFiredCount = int.MaxValue };
        var options = new SchedulerOptions
        {
            MaxRestartsInWindow = 3,
            RestartWindow = TimeSpan.FromSeconds(60),

            // Disable the inter-restart backoff so this test exercises the give-up
            // transition in isolation: every queued occurrence re-faults on the single
            // advance below, as before the backoff was added. The backoff itself — the
            // delay between restarts, that give-up is still reached WITH a backoff, and
            // clean cancellation during a backoff — is covered by BackoffBetweenRestartsTests.
            RestartBackoff = TimeSpan.Zero,
        };
        await using var fx = await Fixture.StartAsync(sink, options).ConfigureAwait(false);
        var dispatcher = await fx.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(5))).ConfigureAwait(false);

        // Advance far enough that several interval occurrences are due at once. Each
        // due fire's publish throws, faulting the loop body; the loop restarts, finds
        // the next still-due occurrence, and re-faults. After more than
        // MaxRestartsInWindow faults the loop transitions to Faulted and stops.
        fx.Time.Advance(TimeSpan.FromMinutes(30));
        await fx.Loop.WaitForFaultedAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(fx.Loop.IsFaulted).IsTrue();

        // Faulted: advancing time no longer ticks (no further fires attempted).
        var firesBefore = dispatcher.FireCount;
        fx.Time.Advance(TimeSpan.FromMinutes(30));
        await Assert.That(dispatcher.FireCount).IsEqualTo(firesBefore);
    }

    /// <summary>
    /// Verifies a non-monotonic clock reading (the clock moving backwards) logs a
    /// warning and the loop continues without crashing or losing a fire. The
    /// <see cref="FakeTimeProvider"/> refuses to move backwards, so the test wraps it
    /// in a provider that can inject a one-time backwards skew on the next reading.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ClockSkewBackwards_LogsWarning_AndContinues()
    {
        var sink = new FaultInjectingEventSink();
        var logger = new CapturingLogger<ScheduleTickLoop>();
        await using var fx = await Fixture.StartSkewableAsync(sink, logger).ConfigureAwait(false);

        // The skewable fixture always supplies a skew provider; bind it once.
        await Assert.That(fx.Skew).IsNotNull();
        var skew = fx.Skew!;

        var dispatcher = await fx.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(5))).ConfigureAwait(false);

        // Inject a backwards skew, then wake the loop. The loop reads the earlier time,
        // detects the regression, warns, and re-arms against the current time rather
        // than sleeping on a stale absolute deadline.
        skew.SkewBy(TimeSpan.FromMinutes(-10));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await Assert.That(logger.Any(LogLevel.Warning)).IsTrue();
        await Assert.That(fx.Loop.IsFaulted).IsFalse();

        // Clear the skew and let the loop re-arm at the correct delay against the
        // (un-skewed) current time, then advance the underlying clock to the
        // occurrence: the loop still fires it exactly once (no lost or duplicate fire).
        skew.SkewBy(TimeSpan.Zero);
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(dispatcher.FireCount).IsEqualTo(1);
    }

    /// <summary>
    /// A test harness owning a started <see cref="ScheduleTickLoop"/> with a
    /// fault-injecting event sink.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            FakeTimeProvider time,
            SkewableTimeProvider? skew,
            ScheduleRegistry registry,
            ScheduleTickLoop loop)
        {
            this.Time = time;
            this.Skew = skew;
            this.Registry = registry;
            this.Loop = loop;
        }

        public FakeTimeProvider Time { get; }

        public SkewableTimeProvider? Skew { get; }

        public ScheduleRegistry Registry { get; }

        public ScheduleTickLoop Loop { get; }

        public static Task<Fixture> StartAsync(
            ISchedulerEventSink sink,
            SchedulerOptions options,
            ILogger<ScheduleTickLoop>? logger = null) =>
            StartCoreAsync(sink, options, logger, useSkew: false);

        public static Task<Fixture> StartSkewableAsync(
            ISchedulerEventSink sink,
            ILogger<ScheduleTickLoop>? logger = null) =>
            StartCoreAsync(sink, new SchedulerOptions(), logger, useSkew: true);

        private static async Task<Fixture> StartCoreAsync(
            ISchedulerEventSink sink,
            SchedulerOptions options,
            ILogger<ScheduleTickLoop>? logger,
            bool useSkew)
        {
            var time = new FakeTimeProvider(Start);
            var skew = useSkew ? new SkewableTimeProvider(time) : null;
            TimeProvider effective = (TimeProvider?)skew ?? time;
            var store = new InMemoryScheduleStore();
            var registry = new ScheduleRegistry(store, effective);
            var router = new JobDispatcherRouter(sink);
            var loop = new ScheduleTickLoop(
                registry, store, effective, router, sink,
                logger ?? new CapturingLogger<ScheduleTickLoop>(), options,
                serviceProvider: Services);

            var fx = new Fixture(time, skew, registry, loop);
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            return fx;
        }

        public async Task<CountingDispatcher> RegisterAsync(string name, Cadence cadence)
        {
            var dispatcher = new CountingDispatcher();
            await this.Registry.RegisterAsync(name, cadence, MissedFirePolicy.Coalesce, dispatcher)
                .ConfigureAwait(false);
            await this.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            return dispatcher;
        }

        public async ValueTask DisposeAsync()
        {
            await ((IHostedService)this.Loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            this.Loop.Dispose();
        }
    }

    /// <summary>
    /// An event sink that throws on a configurable number of
    /// <see cref="JobFiredEvent"/> publishes — injecting a fault into the loop's own
    /// (non-dispatch) code path — while recording all other events.
    /// </summary>
    private sealed class FaultInjectingEventSink : ISchedulerEventSink
    {
        private readonly object gate = new();
        private readonly List<object> published = [];
        private int firedThrows;

        public int ThrowOnFiredCount { get; init; }

        public void Publish<TEvent>(in TEvent schedulerEvent)
            where TEvent : struct
        {
            if (schedulerEvent is JobFiredEvent && Interlocked.Increment(ref this.firedThrows) <= this.ThrowOnFiredCount)
            {
                throw new InvalidOperationException("injected publish fault");
            }

            lock (this.gate)
            {
                this.published.Add(schedulerEvent);
            }
        }

        public bool Any<TEvent>()
            where TEvent : struct
        {
            lock (this.gate)
            {
                return this.published.OfType<TEvent>().Any();
            }
        }
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> that wraps a <see cref="FakeTimeProvider"/> and can
    /// apply a one-shot skew offset to <see cref="GetUtcNow"/>, simulating a
    /// non-monotonic clock the <see cref="FakeTimeProvider"/> (which forbids moving
    /// backwards) cannot. Timer creation delegates to the inner provider, so the test's
    /// <c>Advance</c> still fires the loop's timers.
    /// </summary>
    /// <param name="inner">The underlying fake clock.</param>
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

    /// <summary>
    /// A dispatcher that counts fires. Thread-safe: the router dispatches on the pool.
    /// </summary>
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
