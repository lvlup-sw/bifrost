// =============================================================================
// <copyright file="IScheduleStore.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// The durable persistence port for scheduled jobs. The scheduler reads the full
/// job set at startup and writes back as jobs are mutated and fired, so a
/// process restart resumes from the last persisted state.
/// </summary>
/// <remarks>
/// Implementations own their own durability and batching strategy — an
/// in-memory store, a write-through database store, and a batched store all
/// satisfy this contract. The scheduler does not assume any particular write
/// granularity beyond the per-method contracts below.
/// </remarks>
public interface IScheduleStore
{
    /// <summary>
    /// Loads every persisted job. Called once at scheduler startup to rehydrate
    /// the in-memory registry from durable state.
    /// </summary>
    /// <param name="ct">A token to cancel the load.</param>
    /// <returns>
    /// A snapshot of all persisted jobs; empty when the store holds none.
    /// </returns>
    ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct);

    /// <summary>
    /// Persists a job, inserting it or replacing the existing record with the
    /// same <see cref="JobRecord.Name"/>. Called on every job mutation —
    /// registration, pause, resume, and state transition.
    /// </summary>
    /// <param name="job">The job to persist.</param>
    /// <param name="ct">A token to cancel the save.</param>
    /// <returns>A task that completes when the job is durably saved.</returns>
    ValueTask SaveAsync(JobRecord job, CancellationToken ct);

    /// <summary>
    /// Records that a job fired, updating its last-fired and next-fire instants.
    /// Called after the scheduler hands the fire off to the dispatcher; the
    /// store decides whether to batch this update or write it through.
    /// </summary>
    /// <param name="jobName">The name of the job that fired.</param>
    /// <param name="firedAt">The instant the job fired.</param>
    /// <param name="nextFireAt">
    /// The job's next scheduled fire instant, or <see langword="null"/> when no
    /// further occurrence is scheduled.
    /// </param>
    /// <param name="ct">A token to cancel the update.</param>
    /// <returns>A task that completes when the fire is recorded.</returns>
    ValueTask RecordFiredAsync(
        string jobName,
        DateTimeOffset firedAt,
        DateTimeOffset? nextFireAt,
        CancellationToken ct);

    /// <summary>
    /// Removes a job from the store. Called when a job is unregistered.
    /// </summary>
    /// <param name="jobName">The name of the job to remove.</param>
    /// <param name="ct">A token to cancel the delete.</param>
    /// <returns>A task that completes when the job is removed.</returns>
    ValueTask DeleteAsync(string jobName, CancellationToken ct);
}
