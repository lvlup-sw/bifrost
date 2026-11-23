// =============================================================================
// <copyright file="UncappedMissedFireStartupTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Observability;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// Startup missed-fire reconciliation must stay bounded and must observe host shutdown.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MissedFirePolicy.Coalesce"/> is the default policy. A zero-jitter interval and a cron
/// expression take a constant-time path. Every other cadence, including an interval with jitter, is
/// walked one occurrence at a time from the stored last fire up to the startup clock. That walk has
/// no occurrence cap and does not read the host stopping token. The tick loop does not dispatch any
/// job until the walk returns.
/// </para>
/// <para>
/// These tests are the fix spec. They fail while that walk is uncapped or ignores cancellation.
/// A fix passes them when all of the following hold. Each startup walk stops at the same occurrence
/// cap already used for <see cref="MissedFirePolicy.FireAllMissed"/> (100), and it stops when the
/// startup cancellation token fires. A jittered interval still coalesces to the latest missed
/// occurrence, computed without a step per missed interval. A cadence that advances one tick at a
/// time is exhausted inside that same cap. Coalesce still schedules one catch-up fire.
/// </para>
/// </remarks>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class UncappedMissedFireStartupTests
{
    private const int FireAllMissedCatchUpCap = 100;

    private const int StartupWalkCallBudget = (FireAllMissedCatchUpCap * 2) + 32;

    private static readonly DateTimeOffset Start = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan StartupBound = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// A jittered one-millisecond interval that has already fired must not stall startup across
    /// hours of downtime, and the single catch-up must be the latest missed occurrence.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JitteredMillisecondInterval_AfterRecordedFire_StartupStaysBoundedAndCoalescesLatest()
    {
        var interval = TimeSpan.FromMilliseconds(1);
        await using var session = await RestartAfterFire.StartSecondLoopAsync(
            new IntervalCadence(interval, 0.05),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(30_000_000)).ConfigureAwait(false);

        await AssertStartupBoundedAndLatestCatchUpAsync(session, interval).ConfigureAwait(false);
    }

    /// <summary>
    /// A one-tick interval with jitter is a supported cadence. Hours are not required: a short
    /// downtime is one step per tick, and startup must still coalesce the latest occurrence.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JitteredOneTickInterval_AfterRecordedFire_StartupStaysBoundedAndCoalescesLatest()
    {
        var interval = TimeSpan.FromTicks(1);
        await using var session = await RestartAfterFire.StartSecondLoopAsync(
            new IntervalCadence(interval, 0.05),
            TimeSpan.FromTicks(5),
            TimeSpan.FromTicks(30_000_000)).ConfigureAwait(false);

        await AssertStartupBoundedAndLatestCatchUpAsync(session, interval).ConfigureAwait(false);
    }

    /// <summary>
    /// A cadence that advances one tick at a time must not be enumerated once per missed tick.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task OneTickCadence_StartupWalk_StopsWithinCatchUpCap()
    {
        var now = Start;
        var time = new FakeTimeProvider(now);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        var cadence = new OneTickCadence();
        var stall = new RecordingDispatcher();
        var sibling = new RecordingDispatcher();
        var missedTicks = StartupWalkCallBudget * 40;

        await RestoreRunningAsync(
            registry,
            store,
            "stall",
            cadence,
            now.AddTicks(-missedTicks),
            stall).ConfigureAwait(false);
        await RestoreRunningAsync(
            registry,
            store,
            "sibling",
            new OneShotCadence(now),
            lastFiredAt: null,
            sibling).ConfigureAwait(false);

        var loop = CreateLoop(registry, store, time);
        try
        {
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            await Assert.That(cadence.Calls).IsLessThanOrEqualTo(StartupWalkCallBudget);
            await Assert.That(sibling.Count).IsEqualTo(1);
            await Assert.That(stall.Count).IsLessThanOrEqualTo(1);
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
        }
    }

    /// <summary>
    /// A backlog shorter than the catch-up cap must still stop when the host token fires.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task StartupMissedFireWalk_StopsWhenHostCancellationFires()
    {
        const int naturalSteps = 40;
        var now = Start;
        var time = new FakeTimeProvider(now);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        using var shutdown = new CancellationTokenSource();
        var cadence = new CancellingStepCadence(TimeSpan.FromMinutes(1), shutdown);
        var dispatcher = new RecordingDispatcher();

        await RestoreRunningAsync(
            registry,
            store,
            "stall",
            cadence,
            now.AddMinutes(-naturalSteps),
            dispatcher).ConfigureAwait(false);

        var loop = CreateLoop(registry, store, time);
        try
        {
            await ((IHostedService)loop).StartAsync(shutdown.Token).ConfigureAwait(false);

            var waited = Stopwatch.StartNew();
            while (cadence.Calls < naturalSteps
                && loop.ExecuteTask is { IsCompleted: false }
                && waited.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await Assert.That(cadence.Calls).IsGreaterThan(0);
            await Assert.That(cadence.Calls).IsLessThanOrEqualTo(10);
            await Assert.That(loop.ExecuteTask!.IsCompleted).IsTrue();
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
        }
    }

    /// <summary>
    /// <see cref="ScheduleTickLoop.StopAsync"/> is the host shutdown path. It must finish while a
    /// jittered coalesce walk would otherwise still be stepping.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JitteredMillisecondInterval_StopAsync_FinishesDuringStartupWalk()
    {
        var now = Start;
        var time = new FakeTimeProvider(now);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        var cadence = new IntervalCadence(TimeSpan.FromMilliseconds(1), 0.05);
        var dispatcher = new RecordingDispatcher();

        await RestoreRunningAsync(
            registry,
            store,
            "stall",
            cadence,
            now.AddMilliseconds(-30_000_000),
            dispatcher).ConfigureAwait(false);

        var loop = CreateLoop(registry, store, time);
        try
        {
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, CancellationToken.None)
                .ConfigureAwait(false);

            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await loop.StopAsync(stop.Token).ConfigureAwait(false);

            var execute = loop.ExecuteTask;
            await Assert.That(execute is not null).IsTrue();
            await Assert.That(execute!.IsCompleted).IsTrue();
            await Assert.That(execute.IsFaulted).IsFalse();
        }
        finally
        {
            var pending = loop.ExecuteTask;
            if (pending is not null)
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            loop.Dispose();
        }
    }

    private static async Task AssertStartupBoundedAndLatestCatchUpAsync(RestartAfterFire session, TimeSpan interval)
    {
        await Assert.That(session.Sibling.Count).IsEqualTo(1);
        await Assert.That(session.Stall.Count - session.FiresBeforeRestart).IsEqualTo(1);

        var caughtUp = false;
        var window = TimeSpan.FromTicks(Math.Max(interval.Ticks * 2, 2));
        foreach (var fired in session.Stall.Times)
        {
            if (fired <= session.Now && fired >= session.Now - window)
            {
                caughtUp = true;
                break;
            }
        }

        await Assert.That(caughtUp).IsTrue();
        await Assert.That(session.Elapsed).IsLessThan(StartupBound);
    }

    private static async Task RestoreRunningAsync(
        ScheduleRegistry registry,
        IScheduleStore store,
        string name,
        Cadence cadence,
        DateTimeOffset? lastFiredAt,
        IJobDispatcher dispatcher)
    {
        var record = new JobRecord(
            name,
            cadence,
            MissedFirePolicy.Coalesce,
            JobState.Running,
            lastFiredAt,
            NextFireAt: null,
            DispatchKind: "custom",
            DispatcherTypeName: dispatcher.GetType().FullName,
            Metadata: new Dictionary<string, string>());
        await store.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
        await registry.RestoreAsync(record, dispatcher, CancellationToken.None).ConfigureAwait(false);
    }

    private static ScheduleTickLoop CreateLoop(
        ScheduleRegistry registry,
        IScheduleStore store,
        TimeProvider time)
        => new(
            registry,
            store,
            time,
            new JobDispatcherRouter(NullSchedulerEventSink.Instance),
            NullSchedulerEventSink.Instance,
            NullLogger<ScheduleTickLoop>.Instance,
            new SchedulerOptions());

    private static async Task<DateTimeOffset> WaitForCheckpointAsync(IScheduleStore store, string name)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(5))
        {
            var records = await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var record in records)
            {
                if (record.Name == name && record.LastFiredAt is { } fired)
                {
                    return fired;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, CancellationToken.None)
                .ConfigureAwait(false);
        }

        throw new TimeoutException($"Job '{name}' did not record a last-fire time.");
    }

    /// <summary>
    /// Runs one fire through the public registry, then starts a new tick loop after downtime.
    /// </summary>
    private sealed class RestartAfterFire : IAsyncDisposable
    {
        private RestartAfterFire(
            ScheduleTickLoop loop,
            RecordingDispatcher stall,
            RecordingDispatcher sibling,
            TimeSpan elapsed,
            DateTimeOffset now,
            int firesBeforeRestart)
        {
            this.Loop = loop;
            this.Stall = stall;
            this.Sibling = sibling;
            this.Elapsed = elapsed;
            this.Now = now;
            this.FiresBeforeRestart = firesBeforeRestart;
        }

        public ScheduleTickLoop Loop { get; }

        public RecordingDispatcher Stall { get; }

        public RecordingDispatcher Sibling { get; }

        public TimeSpan Elapsed { get; }

        public DateTimeOffset Now { get; }

        public int FiresBeforeRestart { get; }

        public static async Task<RestartAfterFire> StartSecondLoopAsync(
            IntervalCadence cadence,
            TimeSpan firstAdvance,
            TimeSpan downtime)
        {
            var time = new FakeTimeProvider(Start);
            var store = new InMemoryScheduleStore();
            var registry = new ScheduleRegistry(store, time);
            var stall = new RecordingDispatcher();
            var sibling = new RecordingDispatcher();

            await registry.RegisterAsync("stall", cadence, MissedFirePolicy.Coalesce, stall, CancellationToken.None)
                .ConfigureAwait(false);

            var first = CreateLoop(registry, store, time);
            try
            {
                await ((IHostedService)first).StartAsync(CancellationToken.None).ConfigureAwait(false);
                await first.WaitForIdleAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                time.Advance(firstAdvance);
                await first.WaitForIdleAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await WaitForCheckpointAsync(store, "stall").ConfigureAwait(false);
            }
            finally
            {
                await ((IHostedService)first).StopAsync(CancellationToken.None).ConfigureAwait(false);
                first.Dispose();
            }

            var firesBeforeRestart = stall.Count;
            time.Advance(downtime);
            var now = time.GetUtcNow();
            await RestoreRunningAsync(
                registry,
                store,
                "sibling",
                new OneShotCadence(now),
                lastFiredAt: null,
                sibling).ConfigureAwait(false);

            var second = CreateLoop(registry, store, time);
            try
            {
                var watch = Stopwatch.StartNew();
                await ((IHostedService)second).StartAsync(CancellationToken.None).ConfigureAwait(false);
                await second.WaitForIdleAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                watch.Stop();
                return new RestartAfterFire(second, stall, sibling, watch.Elapsed, now, firesBeforeRestart);
            }
            catch (Exception)
            {
                await ((IHostedService)second).StopAsync(CancellationToken.None).ConfigureAwait(false);
                second.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ((IHostedService)this.Loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            this.Loop.Dispose();
        }
    }

    /// <summary>Counts <see cref="Cadence.ComputeNextFire"/> and always advances one tick.</summary>
    private sealed record OneTickCadence : Cadence
    {
        private int calls;

        public int Calls => Volatile.Read(ref this.calls);

        /// <inheritdoc/>
        public override DateTimeOffset? ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)
        {
            Interlocked.Increment(ref this.calls);
            var cursor = lastFiredAt ?? now;
            var next = cursor.AddTicks(1);
            return next > cursor ? next : null;
        }
    }

    /// <summary>
    /// Advances by a fixed step and cancels the host token on the first startup evaluation.
    /// </summary>
    /// <param name="Step">The advance applied to each cursor.</param>
    /// <param name="Shutdown">The token source passed to <see cref="IHostedService.StartAsync"/>.</param>
    private sealed record CancellingStepCadence(TimeSpan Step, CancellationTokenSource Shutdown) : Cadence
    {
        private int calls;

        public int Calls => Volatile.Read(ref this.calls);

        /// <inheritdoc/>
        public override DateTimeOffset? ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)
        {
            var call = Interlocked.Increment(ref this.calls);
            if (call == 1)
            {
                this.Shutdown.Cancel();
            }

            var cursor = lastFiredAt ?? now;
            return cursor + this.Step;
        }
    }

    /// <summary>Records each scheduled fire time.</summary>
    private sealed class RecordingDispatcher : IJobDispatcher
    {
        private readonly ConcurrentQueue<DateTimeOffset> times = new();

        public IReadOnlyCollection<DateTimeOffset> Times => this.times;

        public int Count => this.times.Count;

        /// <inheritdoc/>
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            this.times.Enqueue(context.FireTime);
            return ValueTask.CompletedTask;
        }
    }
}
