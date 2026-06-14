// =============================================================================
// <copyright file="InMemoryScheduleStoreTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Stores;

namespace Bifrost.Tests.Scheduling.Stores;

/// <summary>
/// Tests for <see cref="InMemoryScheduleStore"/> (Task 18, DR-5): the in-memory
/// persistence port — load, save (with overwrite), delete, record-fired, and
/// concurrent-write safety.
/// </summary>
public sealed class InMemoryScheduleStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies an empty store loads an empty job set.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task LoadAll_WhenEmpty_ReturnsEmpty()
    {
        var store = new InMemoryScheduleStore();

        var jobs = await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);

        await Assert.That(jobs).IsEmpty();
    }

    /// <summary>
    /// Verifies a saved job is returned by a subsequent load.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Save_ThenLoadAll_ReturnsSavedJob()
    {
        var store = new InMemoryScheduleStore();
        var job = MakeJob("daily-report");

        await store.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
        var jobs = await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);

        await Assert.That(jobs).HasCount(1);
        await Assert.That(jobs[0]).IsEqualTo(job);
    }

    /// <summary>
    /// Verifies saving under an existing name overwrites the prior record.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Save_WithDuplicateName_Overwrites()
    {
        var store = new InMemoryScheduleStore();
        var original = MakeJob("daily-report", state: JobState.Running);
        var updated = original with { State = JobState.Paused };

        await store.SaveAsync(original, CancellationToken.None).ConfigureAwait(false);
        await store.SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);
        var jobs = await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);

        await Assert.That(jobs).HasCount(1);
        await Assert.That(jobs[0].State).IsEqualTo(JobState.Paused);
    }

    /// <summary>
    /// Verifies deleting an existing job removes it.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Delete_Existing_RemovesJob()
    {
        var store = new InMemoryScheduleStore();
        await store.SaveAsync(MakeJob("daily-report"), CancellationToken.None).ConfigureAwait(false);

        await store.DeleteAsync("daily-report", CancellationToken.None).ConfigureAwait(false);
        var jobs = await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);

        await Assert.That(jobs).IsEmpty();
    }

    /// <summary>
    /// Verifies deleting a non-existent job is a no-op (no throw, no effect).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Delete_NonExistent_IsNoOp()
    {
        var store = new InMemoryScheduleStore();
        await store.SaveAsync(MakeJob("daily-report"), CancellationToken.None).ConfigureAwait(false);

        await store.DeleteAsync("missing", CancellationToken.None).ConfigureAwait(false);
        var jobs = await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);

        await Assert.That(jobs).HasCount(1);
    }

    /// <summary>
    /// Verifies recording a fire updates the stored record's last-fired and
    /// next-fire instants.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RecordFired_UpdatesLastFiredAndNextFire()
    {
        var store = new InMemoryScheduleStore();
        var job = MakeJob("daily-report", lastFiredAt: null, nextFireAt: Now);
        await store.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);

        var firedAt = Now;
        var nextFireAt = Now + TimeSpan.FromHours(1);
        await store.RecordFiredAsync("daily-report", firedAt, nextFireAt, CancellationToken.None).ConfigureAwait(false);

        var jobs = await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.That(jobs).HasCount(1);
        await Assert.That(jobs[0].LastFiredAt).IsEqualTo(firedAt);
        await Assert.That(jobs[0].NextFireAt).IsEqualTo(nextFireAt);
    }

    /// <summary>
    /// Verifies recording a fire for a non-existent job is a no-op.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RecordFired_NonExistent_IsNoOp()
    {
        var store = new InMemoryScheduleStore();

        await store.RecordFiredAsync("missing", Now, Now + TimeSpan.FromHours(1), CancellationToken.None).ConfigureAwait(false);
        var jobs = await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);

        await Assert.That(jobs).IsEmpty();
    }

    /// <summary>
    /// Verifies concurrent saves from many tasks all land without corruption.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Save_Concurrent_AllPresentNoCorruption()
    {
        var store = new InMemoryScheduleStore();

        var tasks = Enumerable.Range(0, 100)
            .Select(i => store.SaveAsync(MakeJob($"job-{i}"), CancellationToken.None).AsTask());
        await Task.WhenAll(tasks).ConfigureAwait(false);

        var jobs = await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.That(jobs).HasCount(100);
        var names = jobs.Select(j => j.Name).OrderBy(n => n).ToList();
        await Assert.That(names).Contains("job-0");
        await Assert.That(names).Contains("job-99");
    }

    private static JobRecord MakeJob(
        string name,
        JobState state = JobState.Running,
        DateTimeOffset? lastFiredAt = null,
        DateTimeOffset? nextFireAt = null)
        => new(
            Name: name,
            Cadence: new IntervalCadence(TimeSpan.FromHours(1)),
            MissedFirePolicy: MissedFirePolicy.Coalesce,
            State: state,
            LastFiredAt: lastFiredAt,
            NextFireAt: nextFireAt ?? Now + TimeSpan.FromHours(1),
            DispatchKind: "custom",
            DispatcherTypeName: null,
            Metadata: new Dictionary<string, string>());
}
