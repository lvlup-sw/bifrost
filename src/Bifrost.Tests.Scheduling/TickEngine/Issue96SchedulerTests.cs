// =============================================================================
// <copyright file="Issue96SchedulerTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost;
using Bifrost.Core;
using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Observability;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;
using Bifrost.Tests.Scheduling.Dispatch;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// Regressions for the scheduler findings tracked by issue 96.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class Issue96SchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    private static TimeSpan Timeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// A schedule beyond the timer limit must not fault the loop.
    /// </summary>
    [Test]
    public async Task DistantOneShot_DoesNotFaultTheLoop()
    {
        await using var fx = await LoopFixture.StartAsync(Start).ConfigureAwait(false);
        await fx.RegisterAsync("distant", Cadence.At(Start.AddDays(60)), new CountingDispatcher())
            .ConfigureAwait(false);

        await fx.Loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);
        await Assert.That(fx.Loop.IsFaulted).IsFalse();
    }

    /// <summary>
    /// Pause posted during a catch-up burst is applied before the backlog is exhausted.
    /// </summary>
    [Test]
    public async Task TinyIntervalCatchUp_HonorsPauseWithinBudget()
    {
        var budget = ScheduleTickLoop.LiveCatchUpDispatchBudget;
        await using var fx = await LoopFixture.StartAsync(Start, new PauseAtBudgetRouter())
            .ConfigureAwait(false);
        var router = (PauseAtBudgetRouter)fx.Router;
        await fx.RegisterAsync("burst", Cadence.Interval(TimeSpan.FromTicks(1)), new CountingDispatcher())
            .ConfigureAwait(false);

        var advance = Task.Run(() => fx.Time.Advance(TimeSpan.FromTicks(budget + 40)));
        if (!router.ReachedBudget.Wait(Timeout))
        {
            throw new TimeoutException("The catch-up did not reach the dispatch budget.");
        }

        await fx.Registry.PauseAsync("burst").ConfigureAwait(false);
        router.Hold.Set();
        await advance.WaitAsync(Timeout).ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);

        await Assert.That(router.Calls).IsEqualTo(budget);
    }

    /// <summary>
    /// Repeated wakes with a distant job do not retain a timer per wake.
    /// </summary>
    [Test]
    public async Task RepeatedWakes_DoNotRetainSupersededTimers()
    {
        var fake = new FakeTimeProvider(Start);
        var clock = new CountingTimeProvider(fake);
        await using var fx = await LoopFixture.StartAsync(clock, fake).ConfigureAwait(false);
        await fx.RegisterAsync("hourly", Cadence.Interval(TimeSpan.FromHours(1)), new CountingDispatcher())
            .ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);

        for (var i = 0; i < 25; i++)
        {
            await fx.Registry.TriggerAsync("hourly").ConfigureAwait(false);
            await fx.Loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);
        }

        await Assert.That(clock.LiveTimers).IsLessThanOrEqualTo(2);
    }

    /// <summary>
    /// A blocked dispatcher cannot accumulate more in-flight fires than the budget.
    /// </summary>
    [Test]
    public async Task SaturatedDispatches_StayWithinBudget()
    {
        var dispatcher = new BlockingDispatcher();
        await using var fx = await LoopFixture.StartAsync(Start).ConfigureAwait(false);
        await fx.RegisterAsync("minute", Cadence.Interval(TimeSpan.FromMinutes(1)), dispatcher)
            .ConfigureAwait(false);

        var budget = ScheduleTickLoop.MaxOutstandingDispatches;
        fx.Time.Advance(TimeSpan.FromMinutes(budget + 8));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (dispatcher.Entered < budget && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        await Task.Delay(100).ConfigureAwait(false);
        await Assert.That(dispatcher.Entered).IsEqualTo(budget);
    }

    /// <summary>
    /// Rejected admission is a failed fire, and the durable checkpoint waits for the dispatcher.
    /// </summary>
    [Test]
    public async Task RejectedAdmission_IsAFailure_AndCheckpointFollowsAcknowledgement()
    {
        var monitor = new TickHealthMonitor();
        var store = new SignalingStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new GateDispatcher(entered, gate);

        await using var fx = await LoopFixture.StartAsync(Start, health: monitor, store: store)
            .ConfigureAwait(false);
        await fx.RegisterAsync("held", Cadence.At(Start.AddMinutes(5)), blocking)
            .ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await entered.Task.WaitAsync(Timeout).ConfigureAwait(false);
        await Task.Delay(50).ConfigureAwait(false);
        await Assert.That(store.Recorded.Task.IsCompleted).IsFalse();

        gate.TrySetResult();
        await store.Recorded.Task.WaitAsync(Timeout).ConfigureAwait(false);

        var orchestrator = new WorkOrchestrator<string>(
            new NoopHandler(),
            Options.Create(new WorkOrchestratorOptions
            {
                Capacity = 1,
                WorkerCount = 0,
                FullMode = QueueFullMode.Reject,
            }),
            NullLogger<WorkOrchestrator<string>>.Instance);
        await using (orchestrator)
        {
            await orchestrator.EnqueueAsync("full").ConfigureAwait(false);
            var rejecting = new OrchestratorJobDispatcher<string>(
                static _ => "scheduled",
                orchestrator,
                WorkClass.Batch,
                fx.Sink);
            await fx.RegisterAsync("rejected", Cadence.At(Start.AddMinutes(10)), rejecting)
                .ConfigureAwait(false);
            var before = monitor.RecentFireCount;
            var checkpoints = store.Checkpoints;
            fx.Time.Advance(TimeSpan.FromMinutes(5));
            await fx.Loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);

            await Assert.That(monitor.RecentFireCount).IsEqualTo(before + 1);
            await Assert.That(monitor.FailureRate).IsGreaterThan(0);
            await Assert.That(store.Checkpoints).IsEqualTo(checkpoints);
        }
    }

    /// <summary>
    /// A scoped custom dispatcher is resolved from each fire scope.
    /// </summary>
    [Test]
    public async Task ScopedDispatcher_IsResolvedPerFireScope()
    {
        ScopedFire.Seen.Clear();
        var services = new ServiceCollection();
        services.AddScoped<Probe>();
        services.AddScoped<ScopedFire>();
        var job = new JobBuilder<string>("job", services);
        job.Every(TimeSpan.FromMinutes(1)).DispatchVia<ScopedFire>();
        var definition = job.Build();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        var dispatcher = definition.DispatcherFactory(provider);
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var now = Start;
        await dispatcher.DispatchAsync(new JobFireContext("job", now, now.AddMinutes(1), first.ServiceProvider), CancellationToken.None)
            .ConfigureAwait(false);
        await dispatcher.DispatchAsync(new JobFireContext("job", now.AddMinutes(1), null, second.ServiceProvider), CancellationToken.None)
            .ConfigureAwait(false);

        await Assert.That(ScopedFire.Seen).Count().IsEqualTo(2);
        await Assert.That(ScopedFire.Seen[0]).IsNotSameReferenceAs(ScopedFire.Seen[1]);
    }

    /// <summary>
    /// A later success does not record the job past an earlier fire that threw.
    /// </summary>
    [Test]
    public async Task LaterSuccess_DoesNotCheckpointPastAFailedFire()
    {
        var store = new SignalingStore();
        var dispatcher = new FailOnceDispatcher();
        await using var fx = await LoopFixture.StartAsync(Start, new TickHealthMonitor(), store)
            .ConfigureAwait(false);
        await fx.RegisterAsync("gap", Cadence.Interval(TimeSpan.FromMinutes(1)), dispatcher)
            .ConfigureAwait(false);

        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await fx.Loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);
        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await fx.Loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);

        await Assert.That(dispatcher.Calls).IsEqualTo(2);
        await Assert.That(store.Checkpoints).IsEqualTo(0);
    }

    /// <summary>
    /// A full dispatch budget is healthy at first and unhealthy after the stall window.
    /// </summary>
    [Test]
    public async Task CapacityWait_PastTheStaleWindow_IsUnhealthy()
    {
        var monitor = new TickHealthMonitor();
        var dispatcher = new BlockingDispatcher();
        await using var fx = await LoopFixture.StartAsync(Start, monitor, new InMemoryScheduleStore())
            .ConfigureAwait(false);
        await fx.RegisterAsync("minute", Cadence.Interval(TimeSpan.FromMinutes(1)), dispatcher)
            .ConfigureAwait(false);

        var budget = ScheduleTickLoop.MaxOutstandingDispatches;
        fx.Time.Advance(TimeSpan.FromMinutes(budget + 2));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (dispatcher.Entered < budget && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        await Assert.That(dispatcher.Entered).IsEqualTo(budget);
        var seen = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < seen && monitor.IdleWait.Deadline is null)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        await Assert.That(monitor.IdleWait.IsWaiting).IsTrue();
        await Assert.That(monitor.IdleWait.Deadline).IsNotNull();

        var health = new SchedulerHealthCheck(monitor, fx.Loop, fx.Time, TimeSpan.FromMinutes(1));
        var before = await health.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        await Assert.That(before.Status).IsEqualTo(HealthStatus.Healthy);

        fx.Time.Advance(TimeSpan.FromMinutes(4));
        var after = await health.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        await Assert.That(after.Status).IsEqualTo(HealthStatus.Unhealthy);
    }

    /// <summary>
    /// A dispatch that triggers again does not wait for a slot held by its own in-flight work.
    /// </summary>
    [Test]
    public async Task DispatchThatTriggers_DoesNotWaitForItsOwnAdmissionSlot()
    {
        await using var fx = await LoopFixture.StartAsync(Start).ConfigureAwait(false);
        var dispatcher = new RetriggerDispatcher(fx.Registry);
        await fx.RegisterAsync("held", Cadence.Interval(TimeSpan.FromMinutes(1)), dispatcher)
            .ConfigureAwait(false);

        var budget = ScheduleTickLoop.MaxOutstandingDispatches;
        fx.Time.Advance(TimeSpan.FromMinutes(budget + 2));
        var deadline = DateTime.UtcNow + Timeout;
        while (dispatcher.Completed < budget && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        await Assert.That(dispatcher.Completed).IsGreaterThanOrEqualTo(budget);
        await Assert.That(dispatcher.Rejected).IsGreaterThan(0);
        await fx.Loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// A new registration does not keep a checkpoint hole from the previous job of that name.
    /// </summary>
    [Test]
    public async Task Replacement_CheckpointsAfterTheOldHoleIsGone()
    {
        var store = new SignalingStore();
        await using var fx = await LoopFixture.StartAsync(Start, new TickHealthMonitor(), store)
            .ConfigureAwait(false);
        await fx.RegisterAsync("gap", Cadence.Interval(TimeSpan.FromMinutes(1)), new FailOnceDispatcher())
            .ConfigureAwait(false);
        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await fx.Loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);
        await Assert.That(store.Checkpoints).IsEqualTo(0);

        await fx.Registry.UnregisterAsync("gap").ConfigureAwait(false);
        await fx.Loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);
        var replacement = new CallCountingDispatcher();
        await fx.RegisterAsync("gap", Cadence.Interval(TimeSpan.FromMinutes(1)), replacement)
            .ConfigureAwait(false);
        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await fx.Loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);

        await Assert.That(replacement.Calls).IsGreaterThan(0);
        await Assert.That(store.Checkpoints).IsGreaterThan(0);
    }

    /// <summary>
    /// A trigger retained for one registration does not run on the job that replaces it.
    /// </summary>
    [Test]
    public async Task RetainedTrigger_AfterReplacement_DoesNotFireTheNewJob()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = new HoldDispatcher(release);
        var replacement = new CallCountingDispatcher();
        await using var fx = await LoopFixture.StartAsync(Start).ConfigureAwait(false);
        try
        {
            await fx.RegisterAsync("held", Cadence.Interval(TimeSpan.FromMinutes(1)), blocker)
                .ConfigureAwait(false);
            var budget = ScheduleTickLoop.MaxOutstandingDispatches;
            fx.Time.Advance(TimeSpan.FromMinutes(budget + 2));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (blocker.Entered < budget && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }

            await Assert.That(blocker.Entered).IsEqualTo(budget);
            await fx.Registry.TriggerAsync("held").ConfigureAwait(false);
            await fx.Registry.UnregisterAsync("held").ConfigureAwait(false);
            await fx.Registry.RegisterAsync(
                "held",
                Cadence.At(Start.AddDays(1)),
                MissedFirePolicy.SkipMissed,
                replacement).ConfigureAwait(false);
            release.TrySetResult();
            await fx.Loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);
            await Assert.That(replacement.Calls).IsEqualTo(0);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class Probe : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class ScopedFire(Probe probe) : IJobDispatcher
    {
        public static List<Probe> Seen { get; } = [];

        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            Seen.Add(probe);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopHandler : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class FailOnceDispatcher : IJobDispatcher
    {
        private int calls;

        public int Calls => Volatile.Read(ref this.calls);

        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            if (Interlocked.Increment(ref this.calls) == 1)
            {
                throw new InvalidOperationException("The first fire failed.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class RetriggerDispatcher(IScheduleRegistry registry) : IJobDispatcher
    {
        private int started;
        private int completed;
        private int rejected;

        public int Completed => Volatile.Read(ref this.completed);

        public int Rejected => Volatile.Read(ref this.rejected);

        public async ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref this.started);
            if (call <= ScheduleTickLoop.MaxOutstandingDispatches)
            {
                await this.TryTriggerAsync(context.JobName).ConfigureAwait(false);
                await this.TryTriggerAsync(context.JobName).ConfigureAwait(false);
            }

            Interlocked.Increment(ref this.completed);
        }

        private async Task TryTriggerAsync(string name)
        {
            try
            {
                await registry.TriggerAsync(name).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref this.rejected);
            }
        }
    }

    private sealed class CallCountingDispatcher : IJobDispatcher
    {
        private int calls;

        public int Calls => Volatile.Read(ref this.calls);

        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref this.calls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HoldDispatcher(TaskCompletionSource release) : IJobDispatcher
    {
        private int entered;

        public int Entered => Volatile.Read(ref this.entered);

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Usage",
            "VSTHRD003:Avoid awaiting foreign Tasks",
            Justification = "The release task is an intentional test gate.")]
        public async ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref this.entered);
            await release.Task.ConfigureAwait(false);
        }
    }

    private sealed class BlockingDispatcher : IJobDispatcher
    {
        private int entered;

        public int Entered => Volatile.Read(ref this.entered);

        public async ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref this.entered);
            await Task.Delay(System.Threading.Timeout.Infinite, ct).ConfigureAwait(false);
        }
    }

    private sealed class GateDispatcher(TaskCompletionSource entered, TaskCompletionSource gate) : IJobDispatcher
    {
        public async ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            await Task.Yield();
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class PauseAtBudgetRouter : IJobDispatcherRouter
    {
        private readonly JobDispatcherRouter inner = new(new RecordingSchedulerEventSink());
        private int calls;

        public int Calls => Volatile.Read(ref this.calls);

        public ManualResetEventSlim ReachedBudget { get; } = new(false);

        public ManualResetEventSlim Hold { get; } = new(false);

        public void Dispatch(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref this.calls);
            if (n == ScheduleTickLoop.LiveCatchUpDispatchBudget)
            {
                this.ReachedBudget.Set();
                if (!this.Hold.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The catch-up hold was not released.");
                }
            }

            this.inner.Dispatch(dispatcher, context, ct);
        }
    }

    private sealed class SignalingStore : IScheduleStore
    {
        private readonly InMemoryScheduleStore inner = new();

        public TaskCompletionSource Recorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct) => this.inner.LoadAllAsync(ct);

        public ValueTask SaveAsync(JobRecord job, CancellationToken ct) => this.inner.SaveAsync(job, ct);

        public int Checkpoints { get; private set; }

        public ValueTask RecordFiredAsync(string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct)
        {
            this.Checkpoints++;
            this.Recorded.TrySetResult();
            return this.inner.RecordFiredAsync(jobName, firedAt, nextFireAt, ct);
        }

        public ValueTask DeleteAsync(string jobName, CancellationToken ct) => this.inner.DeleteAsync(jobName, ct);
    }

    private sealed class CountingTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider inner;
        private int liveTimers;

        public CountingTimeProvider(FakeTimeProvider inner) => this.inner = inner;

        public int LiveTimers => Volatile.Read(ref this.liveTimers);

        public override DateTimeOffset GetUtcNow() => this.inner.GetUtcNow();

        public override long GetTimestamp() => this.inner.GetTimestamp();

        public override long TimestampFrequency => this.inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref this.liveTimers);
            var timer = this.inner.CreateTimer(callback, state, dueTime, period);
            return new CountingTimer(timer, () => Interlocked.Decrement(ref this.liveTimers));
        }

        private sealed class CountingTimer(ITimer inner, Action onDispose) : ITimer
        {
            private int disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref this.disposed, 1) != 0)
                {
                    return;
                }

                inner.Dispose();
                onDispose();
            }

            public ValueTask DisposeAsync()
            {
                this.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class LoopFixture : IAsyncDisposable
    {
        private LoopFixture(
            FakeTimeProvider clock,
            ScheduleRegistry registry,
            ScheduleTickLoop loop,
            IJobDispatcherRouter router,
            RecordingSchedulerEventSink sink)
        {
            this.Time = clock;
            this.Registry = registry;
            this.Loop = loop;
            this.Router = router;
            this.Sink = sink;
        }

        public FakeTimeProvider Time { get; }

        public ScheduleRegistry Registry { get; }

        public ScheduleTickLoop Loop { get; }

        public IJobDispatcherRouter Router { get; }

        public RecordingSchedulerEventSink Sink { get; }

        public static Task<LoopFixture> StartAsync(DateTimeOffset start)
        {
            var clock = new FakeTimeProvider(start);
            return StartAsync(clock, clock, new JobDispatcherRouter(new RecordingSchedulerEventSink()), new TickHealthMonitor(), new InMemoryScheduleStore());
        }

        public static Task<LoopFixture> StartAsync(DateTimeOffset start, ITickHealthMonitor health, IScheduleStore store)
        {
            var clock = new FakeTimeProvider(start);
            return StartAsync(clock, clock, new JobDispatcherRouter(new RecordingSchedulerEventSink()), health, store);
        }

        public static Task<LoopFixture> StartAsync(DateTimeOffset start, IJobDispatcherRouter router)
        {
            var clock = new FakeTimeProvider(start);
            return StartAsync(clock, clock, router, new TickHealthMonitor(), new InMemoryScheduleStore());
        }

        public static Task<LoopFixture> StartAsync(CountingTimeProvider time, FakeTimeProvider clock)
            => StartAsync(time, clock, new JobDispatcherRouter(new RecordingSchedulerEventSink()), new TickHealthMonitor(), new InMemoryScheduleStore());

        public static async Task<LoopFixture> StartAsync(
            TimeProvider time,
            FakeTimeProvider clock,
            IJobDispatcherRouter router,
            ITickHealthMonitor health,
            IScheduleStore store)
        {
            var registry = new ScheduleRegistry(store, time);
            var sink = new RecordingSchedulerEventSink();
            var loop = new ScheduleTickLoop(
                registry,
                store,
                time,
                router,
                sink,
                NullLogger<ScheduleTickLoop>.Instance,
                new SchedulerOptions(),
                healthMonitor: health);
            var fx = new LoopFixture(clock, registry, loop, router, sink);
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);
            return fx;
        }

        public async Task RegisterAsync(string name, Cadence cadence, IJobDispatcher dispatcher)
        {
            await this.Registry.RegisterAsync(name, cadence, MissedFirePolicy.FireAllMissed, dispatcher)
                .ConfigureAwait(false);
            await this.Loop.WaitForIdleAsync(Timeout).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            // StopAsync arms the shutdown deadline before its first await. Advance it
            // so a dispatcher blocked on the dispatch token can leave during dispose.
            var stop = ((IHostedService)this.Loop).StopAsync(CancellationToken.None);
            this.Time.Advance(TimeSpan.FromSeconds(30));
            await stop.ConfigureAwait(false);
            this.Loop.Dispose();
            if (this.Router is PauseAtBudgetRouter pausing)
            {
                pausing.Hold.Set();
                pausing.ReachedBudget.Dispose();
                pausing.Hold.Dispose();
            }
        }
    }
}
