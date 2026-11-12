// =============================================================================
// <copyright file="HostedRestartTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.DependencyInjection;

/// <summary>Exercises restart through the real hosted registration path.</summary>
public class HostedRestartTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Attaching configured dispatchers cannot overwrite saved lifecycle or progress.</summary>
    [Test]
    [Arguments(JobState.Running)]
    [Arguments(JobState.Paused)]
    [Arguments(JobState.Faulted)]
    [Arguments(JobState.Completed)]
    public async Task StartupPreservesSavedRecord(JobState state)
    {
        var store = new InMemoryScheduleStore();
        var record = new JobRecord("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.FireAllMissed,
            state, Epoch, Epoch.AddMinutes(1), "inline", null, new Dictionary<string, string> { ["owner"] = "retained" });
        await store.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IScheduleStore>(store);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Epoch.AddMinutes(3)));
        services.AddScheduler(builder => builder.AddInlineJob("job").Every(TimeSpan.FromHours(1)).Run((_, _) => ValueTask.CompletedTask));
        await using var provider = services.BuildServiceProvider();
        var registration = provider.GetServices<IHostedService>().OfType<SchedulerJobRegistrationService>().Single();
        await registration.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var persisted = (await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false)).Single();
        await Assert.That(persisted).IsEqualTo(record);
        var live = provider.GetRequiredService<IScheduleRegistry>().GetJob("job")!.Value;
        await Assert.That(live.State).IsEqualTo(state);
        await Assert.That(live.LastFiredAt).IsEqualTo(Epoch);
        await Assert.That(live.Cadence).IsEqualTo(record.Cadence);
    }

    /// <summary>Missed occurrences remain recoverable when a fresh host attaches its dispatcher.</summary>
    [Test]
    public async Task StartupRecoversMissedOccurrenceIdentities()
    {
        var store = new InMemoryScheduleStore();
        await store.SaveAsync(new("job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.FireAllMissed,
            JobState.Running, Epoch, Epoch.AddMinutes(1), "inline", null, new Dictionary<string, string>()), CancellationToken.None).ConfigureAwait(false);
        var fires = new ConcurrentQueue<DateTimeOffset>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IScheduleStore>(store);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Epoch.AddMinutes(3).AddSeconds(30)));
        services.AddScheduler(builder => builder.AddInlineJob("job").Every(TimeSpan.FromMinutes(1))
            .WithMissedFirePolicy(MissedFirePolicy.FireAllMissed).Run((context, _) =>
            {
                fires.Enqueue(context.FireTime);
                return ValueTask.CompletedTask;
            }));
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        try
        {
            foreach (var service in hosted) await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await provider.GetRequiredService<ScheduleTickLoop>().WaitForIdleAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await Assert.That(fires.Order().ToArray()).IsEquivalentTo(new[] { Epoch.AddMinutes(1), Epoch.AddMinutes(2), Epoch.AddMinutes(3) });
        }
        finally
        {
            foreach (var service in hosted.Reverse()) await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Past one-shots obey the startup missed-fire policy.</summary>
    [Test]
    [Arguments(MissedFirePolicy.SkipMissed, 0, false)]
    [Arguments(MissedFirePolicy.SkipMissed, 0, true)]
    [Arguments(MissedFirePolicy.Coalesce, 1, false)]
    [Arguments(MissedFirePolicy.Coalesce, 1, true)]
    [Arguments(MissedFirePolicy.FireAllMissed, 1, false)]
    [Arguments(MissedFirePolicy.FireAllMissed, 1, true)]
    public async Task StartupReconcilesPastOneShot(MissedFirePolicy policy, int expected, bool checkpointFails)
    {
        var store = new InMemoryScheduleStore();
        await store.SaveAsync(new("job", Cadence.At(Epoch.AddMinutes(1)), policy, JobState.Running,
            null, Epoch.AddMinutes(1), "inline", null, new Dictionary<string, string>()), CancellationToken.None).ConfigureAwait(false);
        var count = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IScheduleStore>(checkpointFails ? new CheckpointFailureStore(store) : store);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Epoch.AddMinutes(2)));
        services.AddScheduler(builder => builder.AddInlineJob("job").At(Epoch.AddMinutes(1)).Run((_, _) =>
        {
            Interlocked.Increment(ref count);
            return ValueTask.CompletedTask;
        }));
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        try
        {
            foreach (var service in hosted) await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await provider.GetRequiredService<ScheduleTickLoop>().WaitForIdleAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await Assert.That(count).IsEqualTo(expected);
        }
        finally
        {
            foreach (var service in hosted.Reverse()) await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class CheckpointFailureStore(IScheduleStore inner) : IScheduleStore
    {
        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct) => inner.LoadAllAsync(ct);
        public ValueTask SaveAsync(JobRecord job, CancellationToken ct) => inner.SaveAsync(job, ct);
        public ValueTask DeleteAsync(string jobName, CancellationToken ct) => inner.DeleteAsync(jobName, ct);
        public ValueTask RecordFiredAsync(string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct)
            => ValueTask.FromException(new IOException("Injected checkpoint outage."));
    }
}
