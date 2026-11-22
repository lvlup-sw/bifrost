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
    /// <remarks>
    /// <para>
    /// The update is an atomic compare-and-swap retry loop. A naive TryGetValue-then-indexer-assign
    /// is a non-atomic read-modify-write: the stale snapshot would silently clobber a concurrent
    /// <see cref="SaveAsync"/> (or another <see cref="RecordFiredAsync"/>) that lands between the read
    /// and the write. <c>TryUpdate</c> commits only when the stored record is still the one that was
    /// read (value equality on the record). On contention, another writer replaced the record between
    /// the read and the CAS, so the loop reads the freshest record again and applies the fire stamp
    /// again. Thus no concurrent update is lost.
    /// </para>
    /// <para>
    /// When the job is absent, this method does nothing: a record-fired call for an unknown job is
    /// ignored and does not re-create a phantom record. The loop exits immediately when
    /// <c>TryGetValue</c> returns <see langword="false"/>.
    /// </para>
    /// </remarks>
    public ValueTask RecordFiredAsync(
        string jobName,
        DateTimeOffset firedAt,
        DateTimeOffset? nextFireAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobName);

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
        }

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
