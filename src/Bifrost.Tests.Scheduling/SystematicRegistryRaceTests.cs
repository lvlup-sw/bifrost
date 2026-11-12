#pragma warning disable VSTHRD111, CS1591, SA1600, SA1633, CA2007, VSTHRD003

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Registry;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling;

public sealed class SystematicRegistryRaceTests
{
    [Test]
    [Arguments("update", true)]
    [Arguments("update", false)]
    [Arguments("pause", true)]
    [Arguments("pause", false)]
    [Arguments("resume", true)]
    [Arguments("resume", false)]
    [Arguments("register-fault", true)]
    [Arguments("register-fault", false)]
    public async Task OldGenerationCannotOverwriteReplacement(string operation, bool afterEffect)
    {
        var store = new GateStore { AfterEffect = afterEffect };
        using var registry = new ScheduleRegistry(store, new FakeTimeProvider(SystematicRegistryTests.Epoch));
        var oldDispatcher = new SystematicRegistryTests.NoopDispatcher();
        if (operation != "register-fault")
        {
            await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, oldDispatcher);
            if (operation == "resume")
            {
                await registry.PauseAsync("job");
            }
        }

        store.Armed = true;
        store.Fail = operation == "register-fault";
        var old = operation switch
        {
            "update" => registry.UpdateAsync("job", Cadence.Interval(TimeSpan.FromMinutes(2)), MissedFirePolicy.Coalesce).AsTask(),
            "pause" => registry.PauseAsync("job").AsTask(),
            "resume" => registry.ResumeAsync("job").AsTask(),
            _ => registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, oldDispatcher).AsTask(),
        };
        await store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var replacementDispatcher = new SystematicRegistryTests.NoopDispatcher();
        var replace = ReplaceAsync();
        store.Release.TrySetResult();
        try
        {
            await old.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (SystematicRegistryTests.InjectedStoreException)
        {
        }

        await replace.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(registry.GetJob("job")?.Cadence).IsEqualTo(Cadence.Interval(TimeSpan.FromMinutes(9)));
        await Assert.That(registry.TryGetDispatcher("job")).IsEqualTo(replacementDispatcher);
        await Assert.That(store.Record!.Cadence).IsEqualTo(Cadence.Interval(TimeSpan.FromMinutes(9)));

        async Task ReplaceAsync()
        {
            await registry.UnregisterAsync("job");
            await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(9)), MissedFirePolicy.Coalesce, replacementDispatcher);
        }
    }

    [Test]
    public async Task CancelledWaiterDoesNotMutateOrLeakGate()
    {
        var store = new GateStore { Armed = true };
        using var registry = new ScheduleRegistry(store, new FakeTimeProvider(SystematicRegistryTests.Epoch));
        var registration = registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, new SystematicRegistryTests.NoopDispatcher()).AsTask();
        await store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var update = registry.UpdateAsync("job", Cadence.Interval(TimeSpan.FromMinutes(9)), MissedFirePolicy.Coalesce, cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        var cancelled = false;
        try
        {
            await update.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            store.Release.TrySetResult();
        }

        await registration.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(cancelled).IsTrue();
        await Assert.That(store.Record!.Cadence).IsEqualTo(Cadence.Interval(TimeSpan.FromMinutes(1)));
        await registry.PauseAsync("job").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(store.Record.State).IsEqualTo(JobState.Paused);
    }

    [Test]
    public async Task DisposedWaiterDoesNotReachStore()
    {
        var store = new GateStore { Armed = true };
        using var registry = new ScheduleRegistry(store, new FakeTimeProvider(SystematicRegistryTests.Epoch));
        var registration = registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, new SystematicRegistryTests.NoopDispatcher()).AsTask();
        await store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var update = registry.UpdateAsync("job", Cadence.Interval(TimeSpan.FromMinutes(9)), MissedFirePolicy.Coalesce).AsTask();
        await registry.DisposeAsync();
        store.Release.TrySetResult();
        var disposed = false;
        try
        {
            await update.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (ObjectDisposedException)
        {
            disposed = true;
        }

        try
        {
            await registration.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
        }

        await Assert.That(disposed).IsTrue();
        await Assert.That(store.Record!.Cadence).IsEqualTo(Cadence.Interval(TimeSpan.FromMinutes(1)));
    }

    private sealed class GateStore : IScheduleStore
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JobRecord? Record { get; private set; }
        public bool AfterEffect { get; set; }
        public bool Armed { get; set; }
        public bool Fail { get; set; }
        public async ValueTask SaveAsync(JobRecord job, CancellationToken ct)
        {
            if (!Armed)
            {
                Record = job;
                return;
            }

            Armed = false;
            if (AfterEffect)
            {
                Record = job;
            }

            Arrived.TrySetResult();
            await Release.Task.WaitAsync(ct);
            if (Fail)
            {
                throw new SystematicRegistryTests.InjectedStoreException();
            }

            if (!AfterEffect)
            {
                Record = job;
            }
        }

        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<JobRecord>>(Record is { } job ? [job] : []);
        public ValueTask DeleteAsync(string jobName, CancellationToken ct)
        {
            Record = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask RecordFiredAsync(string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
