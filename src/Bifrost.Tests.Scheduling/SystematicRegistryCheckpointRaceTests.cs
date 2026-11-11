#pragma warning disable VSTHRD111, CS1591, SA1600, SA1633, CA2007, VSTHRD002, VSTHRD003

using System.Reflection;
using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.TickEngine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling;

public sealed class SystematicRegistryCheckpointRaceTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReentrantMutationKeepsItsOwnSchedule(bool replace)
    {
        var store = new SystematicRegistryRestartTests.DurableStore();
        using var registry = new ScheduleRegistry(store, new FakeTimeProvider(SystematicRegistryTests.Epoch));
        var dispatcher = new CallbackDispatcher(async () =>
        {
            if (replace)
            {
                await registry.UnregisterAsync("job");
                await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(9)), MissedFirePolicy.Coalesce, new SystematicRegistryTests.NoopDispatcher());
            }
            else
            {
                await registry.UpdateAsync("job", Cadence.Interval(TimeSpan.FromMinutes(9)), MissedFirePolicy.Coalesce);
            }
        });
        await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, dispatcher);
        using var loop = CreateLoop(registry, store);
        Fire(loop);
        await Assert.That(store.Record!.NextFireAt).IsEqualTo(SystematicRegistryTests.Epoch.AddMinutes(9));
        await Assert.That(store.Record.LastFiredAt).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InFlightCheckpointCannotOverwriteReplacement(bool afterEffect)
    {
        var store = new CheckpointGateStore(afterEffect);
        using var registry = new ScheduleRegistry(store, new FakeTimeProvider(SystematicRegistryTests.Epoch));
        await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, new SystematicRegistryTests.NoopDispatcher());
        using var loop = CreateLoop(registry, store);
        Fire(loop);
        await store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var replace = Replace();
        store.Release.TrySetResult();
        await store.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await replace.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(store.Record!.NextFireAt).IsEqualTo(SystematicRegistryTests.Epoch.AddMinutes(9));
        await Assert.That(store.Record.LastFiredAt).IsNull();

        async Task Replace()
        {
            await registry.UnregisterAsync("job");
            await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(9)), MissedFirePolicy.Coalesce, new SystematicRegistryTests.NoopDispatcher());
        }
    }

    [Test]
    [Arguments("update")]
    [Arguments("pause-resume")]
    public async Task MutationPreservesCheckpointAcrossRestart(string operation)
    {
        var attempts = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var store = new SystematicRegistryRestartTests.DurableStore();
        await using (var first = await SystematicRegistryRestartTests.Process.StartAsync(store, SystematicRegistryTests.Epoch, attempts, false))
        {
            first.Clock.Advance(TimeSpan.FromMinutes(1));
            await first.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            if (operation == "update")
            {
                await first.Registry.UpdateAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.FireAllMissed);
            }
            else
            {
                await first.Registry.PauseAsync("job");
                await first.Registry.ResumeAsync("job");
            }

            await first.Loop.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        }

        var image = store.Clone();
        await using var second = await SystematicRegistryRestartTests.Process.StartAsync(image, SystematicRegistryTests.Epoch.AddMinutes(3), attempts, false);
        await Assert.That(attempts.Count).IsEqualTo(3);
        await Assert.That(image.Record!.LastFiredAt).IsEqualTo(SystematicRegistryTests.Epoch.AddMinutes(3));
    }

    [Test]
    public async Task QueuedCheckpointsPreserveLatestOccurrence()
    {
        var store = new CheckpointGateStore(false);
        using var registry = new ScheduleRegistry(store, new FakeTimeProvider(SystematicRegistryTests.Epoch));
        await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, new SystematicRegistryTests.NoopDispatcher());
        var generation = registry.TryGetRecord("job")!;
        var first = registry.CheckpointAsync(generation, SystematicRegistryTests.Epoch.AddMinutes(1), SystematicRegistryTests.Epoch.AddMinutes(2)).AsTask();
        await store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = registry.CheckpointAsync(generation, SystematicRegistryTests.Epoch.AddMinutes(2), SystematicRegistryTests.Epoch.AddMinutes(3)).AsTask();
        var third = registry.CheckpointAsync(generation, SystematicRegistryTests.Epoch.AddMinutes(3), SystematicRegistryTests.Epoch.AddMinutes(4)).AsTask();
        store.Release.TrySetResult();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(store.Record!.LastFiredAt).IsEqualTo(SystematicRegistryTests.Epoch.AddMinutes(3));
    }

    private static ScheduleTickLoop CreateLoop(ScheduleRegistry registry, IScheduleStore store)
        => new(registry, store, new FakeTimeProvider(SystematicRegistryTests.Epoch), new InlineRouter(), new SystematicRegistryRestartTests.Sink(), NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions());

    private static void Fire(ScheduleTickLoop loop, int minute = 1)
        => typeof(ScheduleTickLoop).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(loop, ["job", SystematicRegistryTests.Epoch.AddMinutes(minute), SystematicRegistryTests.Epoch.AddMinutes(minute + 1)]);

    private sealed class CallbackDispatcher(Func<ValueTask> action) : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) => action();
    }

    private sealed class InlineRouter : IJobDispatcherRouter
    {
        public void Dispatch(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct)
            => dispatcher.DispatchAsync(context, ct).GetAwaiter().GetResult();
    }

    private sealed class CheckpointGateStore(bool afterEffect) : IScheduleStore
    {
        public JobRecord? Record { get; private set; }
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int completed;
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

        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<JobRecord>>([]);
        public async ValueTask RecordFiredAsync(string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct)
        {
            var checkpoint = Record! with { LastFiredAt = firedAt, NextFireAt = nextFireAt };
            if (afterEffect) Record = checkpoint;
            Arrived.TrySetResult();
            await Release.Task;
            if (!afterEffect) Record = checkpoint;
            Finished.TrySetResult();
            if (Interlocked.Increment(ref completed) == 3) AllFinished.TrySetResult();
        }
    }
}
