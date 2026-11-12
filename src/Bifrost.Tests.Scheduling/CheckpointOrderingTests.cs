#pragma warning disable CS1591, SA1600, SA1633, VSTHRD003, VSTHRD111

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Registry;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling;

public sealed class CheckpointOrderingTests
{
    [Test]
    public async Task OlderAndDuplicateCheckpointsCannotRegressMetadata()
    {
        var store = new OrderingStore();
        using var registry = new ScheduleRegistry(store, new FakeTimeProvider(SystematicRegistryTests.Epoch));
        await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, new SystematicRegistryTests.NoopDispatcher());
        var generation = registry.TryGetRecord("job")!;
        var id = registry.RegistrationId("job");
        await registry.CheckpointAsync(generation, At(3), At(4), id);
        await registry.CheckpointAsync(generation, At(2), At(3), id);
        await registry.CheckpointAsync(generation, At(3), At(99), id);
        await Assert.That(store.Writes).IsEqualTo(1);
        await Assert.That(store.Record!.LastFiredAt).IsEqualTo(At(3));
        await Assert.That(store.Record.NextFireAt).IsEqualTo(At(4));
        await registry.UnregisterAsync("job");
        await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, new SystematicRegistryTests.NoopDispatcher());
        var replacementId = registry.RegistrationId("job");
        await registry.CheckpointAsync(generation, At(9), At(10), replacementId);
        await registry.CheckpointAsync(registry.TryGetRecord("job")!, At(1), At(2), replacementId);
        await Assert.That(store.Record!.LastFiredAt).IsEqualTo(At(1));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AmbiguousFailurePreventsOlderWriteAndAllowsSameOccurrenceRetry(bool afterEffect)
    {
        var store = new OrderingStore { ThrowOnce = true, AfterEffect = afterEffect };
        using var registry = new ScheduleRegistry(store, new FakeTimeProvider(SystematicRegistryTests.Epoch));
        await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, new SystematicRegistryTests.NoopDispatcher());
        var generation = registry.TryGetRecord("job")!;
        var id = registry.RegistrationId("job");
        await Assert.That(async () => await registry.CheckpointAsync(generation, At(3), At(4), id).ConfigureAwait(false)).Throws<IOException>();
        await registry.CheckpointAsync(generation, At(2), At(3), id);
        await Assert.That(store.Writes).IsEqualTo(1);
        await Assert.That(store.Record!.LastFiredAt).IsEqualTo(afterEffect ? At(3) : (DateTimeOffset?)null);
        await registry.CheckpointAsync(generation, At(3), At(4), id);
        await registry.CheckpointAsync(generation, At(4), At(5), id);
        await Assert.That(store.Record!.LastFiredAt).IsEqualTo(At(4));
        await Assert.That(registry.TryGetRecord("job")!.LastFiredAt).IsEqualTo(At(4));
    }

    [Test]
    public async Task TriggerDuringRegistration_DoesNotPostAnUnboundId()
    {
        var store = new HoldSaveStore();
        using var registry = new ScheduleRegistry(store, new FakeTimeProvider(SystematicRegistryTests.Epoch));
        var register = registry.RegisterAsync(
            "job",
            Cadence.Interval(TimeSpan.FromMinutes(1)),
            MissedFirePolicy.Coalesce,
            new SystematicRegistryTests.NoopDispatcher());
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try
        {
            await Assert.That(async () => await registry.TriggerAsync("job").ConfigureAwait(false))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            store.Release.TrySetResult();
        }

        await register.AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    [Test]
    public async Task CheckpointForThePreviousRegistration_DoesNotWriteTheReplacement()
    {
        var store = new OrderingStore();
        using var registry = new ScheduleRegistry(store, new FakeTimeProvider(SystematicRegistryTests.Epoch));
        await registry.RegisterAsync(
            "job",
            Cadence.Interval(TimeSpan.FromMinutes(1)),
            MissedFirePolicy.Coalesce,
            new SystematicRegistryTests.NoopDispatcher()).ConfigureAwait(false);
        var firstId = registry.RegistrationId("job");
        await registry.UnregisterAsync("job").ConfigureAwait(false);
        await registry.RegisterAsync(
            "job",
            Cadence.Interval(TimeSpan.FromMinutes(1)),
            MissedFirePolicy.Coalesce,
            new SystematicRegistryTests.NoopDispatcher()).ConfigureAwait(false);
        var replacement = registry.TryGetRecord("job")!;
        await registry.CheckpointAsync(replacement, At(9), At(10), firstId).ConfigureAwait(false);
        await Assert.That(store.Writes).IsEqualTo(0);
        await Assert.That(registry.TryGetRecord("job")!.LastFiredAt).IsNull();
    }

    private static DateTimeOffset At(int minute) => SystematicRegistryTests.Epoch.AddMinutes(minute);

    private sealed class OrderingStore : IScheduleStore
    {
        internal JobRecord? Record { get; private set; }
        internal int Writes { get; private set; }
        internal bool ThrowOnce { get; set; }
        internal bool AfterEffect { get; set; }
        public ValueTask SaveAsync(JobRecord job, CancellationToken ct)
        {
            this.Record = job;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string name, CancellationToken ct)
        {
            this.Record = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<JobRecord>>([]);

        public ValueTask RecordFiredAsync(string name, DateTimeOffset fired, DateTimeOffset? next, CancellationToken ct)
        {
            this.Writes++;
            var fail = this.ThrowOnce;
            this.ThrowOnce = false;
            if (!fail || this.AfterEffect) this.Record = this.Record! with { LastFiredAt = fired, NextFireAt = next };
            if (fail) throw new IOException("checkpoint effect uncertain");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HoldSaveStore : IScheduleStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask SaveAsync(JobRecord job, CancellationToken ct)
        {
            this.Entered.TrySetResult();
            return new ValueTask(this.Release.Task);
        }

        public ValueTask DeleteAsync(string name, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<JobRecord>>([]);

        public ValueTask RecordFiredAsync(string name, DateTimeOffset fired, DateTimeOffset? next, CancellationToken ct)
            => ValueTask.CompletedTask;
    }
}
