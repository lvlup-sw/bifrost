#pragma warning disable CS1591, SA1600, SA1633, VSTHRD003, VSTHRD111

using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Core;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling;

public sealed class JobMutationGatesTests
{
    [Test]
    public async Task SlowStoreForOneNameDoesNotBlockCollidingName()
    {
        var names = Enumerable.Range(0, 10000).Select(i => "job" + i)
            .GroupBy(name => (uint)StringComparer.Ordinal.GetHashCode(name) % 64).First(g => g.Count() > 1).Take(2).ToArray();
        var store = new SlowStore(names[0]);
        using var registry = new ScheduleRegistry(store, new FakeTimeProvider(SystematicRegistryTests.Epoch));
        var first = registry.RegisterAsync(names[0], Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, new SystematicRegistryTests.NoopDispatcher()).AsTask();
        await store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await registry.RegisterAsync(names[1], Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce, new SystematicRegistryTests.NoopDispatcher()).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(first.IsCompleted).IsFalse();
        }
        finally
        {
            store.Release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class SlowStore(string blocked) : IScheduleStore
    {
        internal TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask SaveAsync(JobRecord job, CancellationToken ct)
        {
            if (job.Name == blocked)
            {
                this.Arrived.TrySetResult();
                await this.Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        public ValueTask DeleteAsync(string name, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RecordFiredAsync(string name, DateTimeOffset fired, DateTimeOffset? next, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<JobRecord>>([]);
    }

    [Test]
    public async Task WaitersPreventRetirementAndCancellationReleasesReference()
    {
        var gates = new JobMutationGates();
        var first = await gates.EnterAsync("job");
        using var canceled = new CancellationTokenSource();
        var waiter = gates.EnterAsync("job", canceled.Token).AsTask();
        var next = gates.EnterAsync("job").AsTask();
        await canceled.CancelAsync();
        await Assert.That(async () => await waiter.ConfigureAwait(false)).Throws<OperationCanceledException>();
        await Assert.That(gates.Count).IsEqualTo(1);
        await Assert.That(next.IsCompleted).IsFalse();
        using (await gates.EnterAsync("independent"))
        {
            await Assert.That(gates.Count).IsEqualTo(2);
        }

        first.Dispose();
        first.Dispose();
        using (await next.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            await Assert.That(gates.Count).IsEqualTo(1);
        }

        await Assert.That(gates.Count).IsEqualTo(0);
        using (await gates.EnterAsync("job"))
        {
            await Assert.That(gates.Count).IsEqualTo(1);
        }

        await Assert.That(gates.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentChurnNeverCreatesTwoOwnersForOneName()
    {
        var gates = new JobMutationGates();
        var active = new int[8];
        var operations = Enumerable.Range(0, 32).Select(async actor =>
        {
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var key = (actor + iteration) % active.Length;
                using (await gates.EnterAsync(key.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                {
                    if (Interlocked.Increment(ref active[key]) != 1) throw new InvalidOperationException("Two owners");
                    await Task.Yield();
                    if (Interlocked.Decrement(ref active[key]) != 0) throw new InvalidOperationException("Owner lost");
                }
            }
        });
        await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(gates.Count).IsEqualTo(0);
    }
}
