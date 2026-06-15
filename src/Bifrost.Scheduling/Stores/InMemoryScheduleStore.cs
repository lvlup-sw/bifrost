// =============================================================================
// <copyright file="InMemoryScheduleStore.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.Stores;

/// <summary>
/// An in-memory <see cref="IScheduleStore"/> backed by a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. It performs no I/O and every
/// method completes synchronously.
/// </summary>
/// <remarks>
/// This store is single-instance only and holds no durable backing: all state is
/// lost on process restart. It is intended for tests, single-process scenarios, and
/// as the default when no durable store is configured — not for multi-instance or
/// crash-recoverable scheduling.
/// </remarks>
public sealed class InMemoryScheduleStore : IScheduleStore
{
    private readonly ConcurrentDictionary<string, JobRecord> jobs = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct)
    {
        IReadOnlyList<JobRecord> snapshot = [.. this.jobs.Values];
        return ValueTask.FromResult(snapshot);
    }

    /// <inheritdoc/>
    public ValueTask SaveAsync(JobRecord job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        this.jobs[job.Name] = job;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask RecordFiredAsync(
        string jobName,
        DateTimeOffset firedAt,
        DateTimeOffset? nextFireAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobName);

        // Atomic compare-and-swap retry loop. A naive TryGetValue-then-indexer-assign is
        // a non-atomic read-modify-write: a concurrent SaveAsync (or another
        // RecordFiredAsync) landing between the read and the write would be silently
        // clobbered by the stale snapshot we read. TryUpdate only commits when the stored
        // record is still the one we read (value equality on the record); on contention
        // we re-read the freshest record and re-apply the fire stamp, so no concurrent
        // update is lost.
        while (this.jobs.TryGetValue(jobName, out var existing))
        {
            var updated = existing with
            {
                LastFiredAt = firedAt,
                NextFireAt = nextFireAt,
            };

            if (this.jobs.TryUpdate(jobName, updated, existing))
            {
                break;
            }

            // Contended: another writer replaced the record between our read and our
            // CAS. Re-read and retry against the freshest record.
        }

        // No-op when the job is absent: a record-fired for an unknown job is ignored
        // rather than re-creating a phantom record. (The loop exits immediately when
        // TryGetValue returns false.)
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(string jobName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobName);

        this.jobs.TryRemove(jobName, out _);
        return ValueTask.CompletedTask;
    }
}
