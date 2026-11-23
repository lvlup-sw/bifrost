// =============================================================================
// <copyright file="JitteredCoalesceStartupTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;
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
/// Startup coalesce for a jittered interval, and for any other cadence that misses the
/// constant-time branches, must stay inside the catch-up cap and must observe shutdown.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class JitteredCoalesceStartupTests
{
    private const int CatchUpCap = 100;
    private const long RestartBacklogTicks = 1_500_000_000;

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A one-tick interval with jitter uses the default coalesce policy. After one fire
    /// is stored, a later startup must arm every job without walking the whole gap.
    /// </summary>
    /// <remarks>
    /// The acceptable catch-up is the same instant zero-jitter coalesce computes, or the
    /// latest instant inside the existing catch-up cap of 100. The other job has one
    /// missed minute and must still fire that occurrence.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RestartAfterJitteredTickInterval_ArmsEveryJobWithinStartupBudget()
    {
        var store = new InMemoryScheduleStore();
        var time = new FakeTimeProvider(Epoch);
        var firstFires = new ConcurrentQueue<DateTimeOffset>();
        await using var first = await StartHostAsync(store, time, firstFires).ConfigureAwait(false);
        await first.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        time.Advance(TimeSpan.FromTicks(1));
        await first.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var saved = (await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false))
            .Single(static job => job.Name == "fast");
        await Assert.That(firstFires.Count).IsEqualTo(1);
        await Assert.That(saved.MissedFirePolicy).IsEqualTo(MissedFirePolicy.Coalesce);
        await Assert.That(saved.LastFiredAt.HasValue).IsTrue();
        await Assert.That(saved.Cadence).IsAssignableTo<IntervalCadence>();
        await Assert.That(((IntervalCadence)saved.Cadence).Jitter).IsGreaterThan(0);
        await first.DisposeAsync().ConfigureAwait(false);

        var last = saved.LastFiredAt.GetValueOrDefault();
        var restartAt = last.AddTicks(RestartBacklogTicks);
        var otherSaved = (await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false))
            .Single(static job => job.Name == "other");
        await store.SaveAsync(
            otherSaved with { LastFiredAt = restartAt.AddMinutes(-1), NextFireAt = restartAt },
            CancellationToken.None).ConfigureAwait(false);
        time.AdjustTime(restartAt);
        var restartFires = new ConcurrentQueue<DateTimeOffset>();
        var otherFires = new ConcurrentQueue<DateTimeOffset>();
        await using var restart = await StartHostAsync(store, time, restartFires, otherFires).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        await restart.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        watch.Stop();

        var now = time.GetUtcNow();
        var fire = restartFires.Single();
        var steps = (fire - last).Ticks;
        var fullGap = (now - last).Ticks;
        var bounded = steps == fullGap || steps == CatchUpCap;
        await Assert.That(bounded).IsTrue();
        await Assert.That(otherFires.ToArray()).IsEquivalentTo(new[] { restartAt });
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// A saved cadence wins over the cadence configured at startup, and its coalesce
    /// walk still stops at the catch-up cap.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task StoredSteppingCadence_CoalesceWalkStaysWithinCatchUpCap()
    {
        var now = Epoch.AddHours(2);
        var stepping = new SteppingCadence(now, StopAfter: 5_000, Gate: null);
        var store = new InMemoryScheduleStore();
        await store.SaveAsync(
            new JobRecord(
                "job",
                stepping,
                MissedFirePolicy.Coalesce,
                JobState.Running,
                now.AddTicks(-1_000_000),
                NextFireAt: null,
                DispatchKind: "inline",
                DispatcherTypeName: null,
                Metadata: new Dictionary<string, string>()),
            CancellationToken.None).ConfigureAwait(false);
        await using var host = await StartHostAsync(
            store,
            new FakeTimeProvider(now),
            fires: null,
            configure: static builder => builder.AddInlineJob("job").Every(TimeSpan.FromHours(1))
                .Run(static (_, _) => ValueTask.CompletedTask)).ConfigureAwait(false);
        await host.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(host.Registry.GetJob("job")!.Value.Cadence).IsSameReferenceAs(stepping);
        await Assert.That(stepping.WalkCalls).IsLessThanOrEqualTo(CatchUpCap * 2);
    }

    /// <summary>
    /// Shutdown during the coalesce walk must stop that walk before the catch-up cap,
    /// including the missed-count walk that follows it.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task StoppingTokenDuringCoalesceWalk_InterruptsBeforeCatchUpCap()
    {
        var now = Epoch.AddHours(3);
        var gate = new ManualResetEventSlim(initialState: false);
        var stepping = new SteppingCadence(now, StopAfter: 40, Gate: gate);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        await registry.RegisterAsync("job", stepping, MissedFirePolicy.Coalesce, new NopDispatcher()).ConfigureAwait(false);
        await store.SaveAsync(
            new JobRecord(
                "job",
                stepping,
                MissedFirePolicy.Coalesce,
                JobState.Running,
                now.AddHours(-1),
                NextFireAt: null,
                DispatchKind: "custom",
                DispatcherTypeName: null,
                Metadata: new Dictionary<string, string>()),
            CancellationToken.None).ConfigureAwait(false);
        var loop = new ScheduleTickLoop(
            registry,
            store,
            time,
            new JobDispatcherRouter(new RecordingSchedulerEventSink()),
            new RecordingSchedulerEventSink(),
            NullLogger<ScheduleTickLoop>.Instance,
            new SchedulerOptions());
        try
        {
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await stepping.Started.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var stopping = ((IHostedService)loop).StopAsync(CancellationToken.None);
            stepping.Release();
            await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await Assert.That(stepping.WalkCalls).IsLessThanOrEqualTo(8);
        }
        finally
        {
            stepping.Release();
            gate.Dispose();
            if (loop.ExecuteTask is { IsCompleted: false } pending)
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }

            loop.Dispose();
        }
    }

    private static async Task<RunningHost> StartHostAsync(
        IScheduleStore store,
        FakeTimeProvider time,
        ConcurrentQueue<DateTimeOffset>? fires,
        ConcurrentQueue<DateTimeOffset>? otherFires = null,
        Action<ISchedulerBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddSingleton<TimeProvider>(time);
        services.AddScheduler(configure ?? (builder =>
        {
            var fast = builder.AddInlineJob("fast")
                .Every(TimeSpan.FromTicks(1))
                .WithJitter(0.25);
            if (fires is null)
            {
                fast.Run(static (_, _) => ValueTask.CompletedTask);
            }
            else
            {
                fast.Run((context, _) =>
                {
                    fires.Enqueue(context.FireTime);
                    return ValueTask.CompletedTask;
                });
            }

            var other = builder.AddInlineJob("other").Every(TimeSpan.FromMinutes(1));
            if (otherFires is null)
            {
                other.Run(static (_, _) => ValueTask.CompletedTask);
            }
            else
            {
                other.Run((context, _) =>
                {
                    otherFires.Enqueue(context.FireTime);
                    return ValueTask.CompletedTask;
                });
            }
        }));
        var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return new RunningHost(
            provider,
            provider.GetRequiredService<ScheduleRegistry>(),
            provider.GetRequiredService<ScheduleTickLoop>(),
            hosted);
    }

    private sealed class RunningHost(
        ServiceProvider provider,
        ScheduleRegistry registry,
        ScheduleTickLoop loop,
        IHostedService[] hosted) : IAsyncDisposable
    {
        public ScheduleRegistry Registry { get; } = registry;

        public ScheduleTickLoop Loop { get; } = loop;

        public async ValueTask DisposeAsync()
        {
            foreach (var service in hosted.Reverse())
            {
                await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class NopDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Advances one tick per walk call. The walk passes the previous occurrence as the
    /// clock, so those calls are the ones the coalesce loop makes.
    /// </summary>
    private sealed record SteppingCadence(DateTimeOffset End, int StopAfter, ManualResetEventSlim? Gate) : Cadence
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int walkCalls;

        public int WalkCalls => Volatile.Read(ref this.walkCalls);

        public Task Started => this.started.Task;

        public void Release() => this.Gate?.Set();

        /// <inheritdoc/>
        public override DateTimeOffset? ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)
        {
            if (lastFiredAt is { } last && now == last)
            {
                var call = Interlocked.Increment(ref this.walkCalls);
                if (call == 1 && this.Gate is not null)
                {
                    this.started.TrySetResult();
                    if (!this.Gate.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("The coalesce walk was not released.");
                    }
                }

                if (call >= this.StopAfter)
                {
                    return this.End.AddTicks(1);
                }

                return last.AddTicks(1);
            }

            return this.End.AddTicks(1);
        }
    }
}
