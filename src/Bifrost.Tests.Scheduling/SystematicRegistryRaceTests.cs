#pragma warning disable VSTHRD111, CS1591, SA1600, SA1633, CA2007, VSTHRD003

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Registry;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling;

public sealed class SystematicRegistryRaceTests
{
    [Test]
    [Arguments("update")]
    [Arguments("pause")]
    [Arguments("resume")]
    [Arguments("register-fault")]
    public async Task OldGenerationCannotOverwriteReplacement(string operation)
    {
        var store = new GateStore();
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

    private sealed class GateStore : IScheduleStore
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JobRecord? Record { get; private set; }
        public bool Armed { get; set; }
        public bool Fail { get; set; }
        public async ValueTask SaveAsync(JobRecord job, CancellationToken ct)
        {
            Record = job;
            if (Armed)
            {
                Armed = false;
                Arrived.TrySetResult();
                await Release.Task.WaitAsync(ct);
                if (Fail)
                {
                    throw new SystematicRegistryTests.InjectedStoreException();
                }
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
