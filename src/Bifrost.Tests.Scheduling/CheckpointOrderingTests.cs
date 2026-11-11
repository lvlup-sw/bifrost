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
        await registry.CheckpointAsync(generation, At(3), At(4));
        await registry.CheckpointAsync(generation, At(2), At(3));
        await registry.CheckpointAsync(generation, At(3), At(99));
        await Assert.That(store.Writes).IsEqualTo(1);
        await Assert.That(store.Record!.LastFiredAt).IsEqualTo(At(3));
        await Assert.That(store.Record.NextFireAt).IsEqualTo(At(4));
        await registry.UnregisterAsync("job");
        await registry.RegisterAsync("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, new SystematicRegistryTests.NoopDispatcher());
        await registry.CheckpointAsync(generation, At(9), At(10));
        await registry.CheckpointAsync(registry.TryGetRecord("job")!, At(1), At(2));
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
        await Assert.That(async () => await registry.CheckpointAsync(generation, At(3), At(4)).ConfigureAwait(false)).Throws<IOException>();
        await registry.CheckpointAsync(generation, At(2), At(3));
        await Assert.That(store.Writes).IsEqualTo(1);
        await Assert.That(store.Record!.LastFiredAt).IsEqualTo(afterEffect ? At(3) : (DateTimeOffset?)null);
        await registry.CheckpointAsync(generation, At(3), At(4));
        await registry.CheckpointAsync(generation, At(4), At(5));
        await Assert.That(store.Record!.LastFiredAt).IsEqualTo(At(4));
        await Assert.That(registry.TryGetRecord("job")!.LastFiredAt).IsEqualTo(At(4));
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
}
