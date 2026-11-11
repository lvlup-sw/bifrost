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

    private static ScheduleTickLoop CreateLoop(ScheduleRegistry registry, IScheduleStore store)
        => new(registry, store, new FakeTimeProvider(SystematicRegistryTests.Epoch), new InlineRouter(), new SystematicRegistryRestartTests.Sink(), NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions());

    private static void Fire(ScheduleTickLoop loop)
        => typeof(ScheduleTickLoop).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(loop, ["job", SystematicRegistryTests.Epoch.AddMinutes(1), SystematicRegistryTests.Epoch.AddMinutes(2)]);

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
        }
    }
}
