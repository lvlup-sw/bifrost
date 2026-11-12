#pragma warning disable VSTHRD111, CS1591, SA1600, SA1633, CA2007

using System.Collections.Concurrent;
using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.TickEngine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling;

public sealed class SystematicRegistryRestartTests
{
    [Test]
    public async Task CheckpointFollowsDispatcherHandoff()
    {
        var attempts = new ConcurrentQueue<string>();
        var store = new DurableStore();
        var attemptsAtCheckpoint = -1;
        store.BeforeCheckpoint = () => attemptsAtCheckpoint = attempts.Count;
        await using var process = await Process.StartAsync(store, SystematicRegistryTests.Epoch, attempts, false, router: new InlineRouter());
        process.Clock.Advance(TimeSpan.FromMinutes(1));
        await process.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        await Assert.That(attemptsAtCheckpoint).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ThrowingHandoffDoesNotClaimDurableAcknowledgment(bool effectBeforeThrow)
    {
        var attempts = new ConcurrentQueue<string>();
        var store = new DurableStore();
        var clock = new FakeTimeProvider(SystematicRegistryTests.Epoch);
        using var registry = new ScheduleRegistry(store, clock);
        await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.FireAllMissed, new RecordingDispatcher(attempts, false));
        var sink = new Sink();
        using var loop = new ScheduleTickLoop(registry, store, clock, new ThrowingRouter(effectBeforeThrow), sink, NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions());
        var dispatch = typeof(ScheduleTickLoop).GetMethod("Dispatch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Exception? fault = null;
        try
        {
            dispatch.Invoke(loop, ["job", SystematicRegistryTests.Epoch.AddMinutes(1), SystematicRegistryTests.Epoch.AddMinutes(2)]);
        }
        catch (System.Reflection.TargetInvocationException exception)
        {
            fault = exception.InnerException;
        }

        await Assert.That(fault is SystematicRegistryTests.InjectedStoreException).IsTrue();
        await Assert.That(attempts.Count).IsEqualTo(effectBeforeThrow ? 1 : 0);
        await Assert.That(store.CheckpointAttempts).IsEqualTo(0);
        await Assert.That(store.Record!.LastFiredAt).IsNull();
    }

    private sealed class ThrowingRouter(bool effectBeforeThrow) : IJobDispatcherRouter
    {
        public void Dispatch(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct)
        {
            if (effectBeforeThrow)
            {
#pragma warning disable VSTHRD002
                dispatcher.DispatchAsync(context, ct).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            }

            throw new SystematicRegistryTests.InjectedStoreException();
        }
    }

    private sealed class InlineRouter : IJobDispatcherRouter
    {
        public void Dispatch(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct)
        {
#pragma warning disable VSTHRD002
            dispatcher.DispatchAsync(context, ct).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        }
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(2, false)]
    [Arguments(0, true)]
    [Arguments(1, true)]
    [Arguments(2, true)]
    public async Task CrashImageRestartsFromDurableOccurrence(int checkpointFault, bool dispatchFails)
    {
        var epoch = SystematicRegistryTests.Epoch;
        var attempts = new ConcurrentQueue<string>();
        var store = new DurableStore { Fault = checkpointFault };
        var first = await Process.StartAsync(store, epoch, attempts, dispatchFails);
        await using (first)
        {
            first.Clock.Advance(TimeSpan.FromMinutes(1));
            await first.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            // A dispatcher that throws has not acknowledged the fire, so the
            // occurrence stays uncheckpointed and the restart replays it.
            await Assert.That(store.CheckpointAttempts).IsEqualTo(dispatchFails ? 0 : 1);
            await Assert.That(store.CheckpointEffects).IsEqualTo(dispatchFails || checkpointFault == 1 ? 0 : 1);
            await Assert.That(attempts.Count).IsEqualTo(1);
        }

        var crashImage = store.Clone();
        var unclaimed = dispatchFails || checkpointFault == 1;
        await Assert.That(crashImage.Record!.LastFiredAt).IsEqualTo(unclaimed ? epoch : epoch.AddMinutes(1));
        var second = await Process.StartAsync(crashImage, epoch.AddMinutes(1), attempts, false);
        await using (second)
        {
            var expectedAttempts = unclaimed ? 2 : 1;
            await Assert.That(attempts.Count).IsEqualTo(expectedAttempts);
            await Assert.That(attempts.Distinct().Count()).IsEqualTo(1);
            second.Clock.Advance(TimeSpan.FromMinutes(1));
            await second.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            await Assert.That(attempts.Count).IsEqualTo(expectedAttempts + 1);
            await Assert.That(attempts.Distinct().Count()).IsEqualTo(2);
            await Assert.That(crashImage.Record!.LastFiredAt).IsEqualTo(epoch.AddMinutes(2));
        }
    }

    [Test]
    [Arguments("before-checkpoint")]
    [Arguments("after-checkpoint")]
    [Arguments("before-dispatch")]
    [Arguments("after-dispatch")]
    public async Task FrozenCrashBoundaryImage(string cut)
    {
        var epoch = SystematicRegistryTests.Epoch;
        var attempts = new ConcurrentQueue<string>();
        var store = new DurableStore { Fault = cut.Contains("dispatch", StringComparison.Ordinal) ? 1 : 0 };
        DurableStore? image = null;
        var attemptsAtCut = -1;
        void Capture(string phase)
        {
            if (phase == cut)
            {
                image = store.Clone();
                attemptsAtCut = attempts.Count;
            }
        }

        store.BeforeCheckpoint = () => Capture("before-checkpoint");
        store.AfterCheckpoint = () => Capture("after-checkpoint");
        await using (var first = await Process.StartAsync(store, epoch, attempts, false, Capture))
        {
            first.Clock.Advance(TimeSpan.FromMinutes(1));
            await first.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        }

        await Assert.That(image).IsNotNull();
        var committed = cut == "after-checkpoint";
        await Assert.That(image!.Record!.LastFiredAt).IsEqualTo(committed ? epoch.AddMinutes(1) : epoch);
        var restartedAttempts = new ConcurrentQueue<string>();
        await using var second = await Process.StartAsync(image, epoch.AddMinutes(1), restartedAttempts, false);
        await Assert.That(restartedAttempts.Count).IsEqualTo(committed ? 0 : 1);
        if (cut == "before-dispatch")
        {
            await Assert.That(attemptsAtCut).IsEqualTo(0);
        }

        if (cut == "after-dispatch")
        {
            await Assert.That(attemptsAtCut).IsEqualTo(1);
            await Assert.That(attempts.Single()).IsEqualTo(restartedAttempts.Single());
        }
    }

    [Test]
    public async Task ReplacementDiscardsStaleHeapGeneration()
    {
        var epoch = SystematicRegistryTests.Epoch;
        var attempts = new ConcurrentQueue<string>();
        var store = new DurableStore();
        await using var process = await Process.StartAsync(store, epoch, attempts, false);
        await process.Registry.UnregisterAsync("job");
        await process.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        await process.Registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(3)), MissedFirePolicy.FireAllMissed, new RecordingDispatcher(attempts, false));
        await process.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        process.Clock.Advance(TimeSpan.FromMinutes(1));
        await process.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        await Assert.That(attempts.Count).IsEqualTo(0);
        process.Clock.Advance(TimeSpan.FromMinutes(2));
        await process.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        await Assert.That(attempts.Single()).IsEqualTo("job/" + epoch.AddMinutes(3).Ticks);
    }

    internal sealed class DurableStore : IScheduleStore
    {
        public JobRecord? Record { get; set; }
        public int Fault { get; set; }
        public int CheckpointAttempts { get; private set; }
        public int CheckpointEffects { get; private set; }
        public Action? BeforeCheckpoint { get; set; }
        public Action? AfterCheckpoint { get; set; }
        public DurableStore Clone() => new() { Record = Record };
        public ValueTask SaveAsync(JobRecord job, CancellationToken ct)
        {
            Record = job;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string jobName, CancellationToken ct)
        {
            Record = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<JobRecord>>(Record is { } job ? [job] : []);
        public ValueTask RecordFiredAsync(string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct)
        {
            CheckpointAttempts++;
            BeforeCheckpoint?.Invoke();
            if (Fault == 1)
            {
                throw new SystematicRegistryTests.InjectedStoreException();
            }

            Record = Record! with { LastFiredAt = firedAt, NextFireAt = nextFireAt };
            CheckpointEffects++;
            AfterCheckpoint?.Invoke();
            if (Fault == 2)
            {
                throw new SystematicRegistryTests.InjectedStoreException();
            }

            return ValueTask.CompletedTask;
        }
    }

    internal sealed class RecordingDispatcher(ConcurrentQueue<string> attempts, bool fails, Action<string>? boundary = null) : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            boundary?.Invoke("before-dispatch");
            attempts.Enqueue(context.JobName + "/" + context.FireTime.Ticks);
            boundary?.Invoke("after-dispatch");
            if (fails)
            {
                throw new SystematicRegistryTests.InjectedStoreException();
            }

            return ValueTask.CompletedTask;
        }
    }

    internal sealed class Sink : ISchedulerEventSink
    {
        public void Publish<TEvent>(in TEvent schedulerEvent) where TEvent : struct
        {
        }
    }

    internal sealed class Process : IAsyncDisposable
    {
        private Process(FakeTimeProvider clock, ScheduleRegistry registry, ScheduleTickLoop loop)
        {
            Clock = clock;
            Registry = registry;
            Loop = loop;
        }

        public FakeTimeProvider Clock { get; }
        public ScheduleRegistry Registry { get; }
        public ScheduleTickLoop Loop { get; }
        public static async Task<Process> StartAsync(DurableStore store, DateTimeOffset now, ConcurrentQueue<string> attempts, bool fails, Action<string>? boundary = null, IJobDispatcherRouter? router = null)
        {
            var clock = new FakeTimeProvider(now);
            var registry = new ScheduleRegistry(store, clock);
            var saved = store.Record;
            await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.FireAllMissed, new RecordingDispatcher(attempts, fails, boundary));
            store.Record = saved ?? store.Record! with { LastFiredAt = now };
            while (registry.Commands.TryRead(out _))
            {
            }

            var sink = new Sink();
            var loop = new ScheduleTickLoop(registry, store, clock, router ?? new JobDispatcherRouter(sink), sink, NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions());
            await ((IHostedService)loop).StartAsync(CancellationToken.None);
            await loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            return new(clock, registry, loop);
        }

        public async ValueTask DisposeAsync()
        {
            await ((IHostedService)Loop).StopAsync(CancellationToken.None);
            Loop.Dispose();
            Registry.Dispose();
        }
    }
}
