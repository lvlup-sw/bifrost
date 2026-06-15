// =============================================================================
// <copyright file="IScheduleRegistry.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// The runtime control surface for scheduled jobs: register, unregister, pause,
/// resume, manually trigger, and inspect. This is the live registry the
/// scheduler drives; durable state flows through an <see cref="IScheduleStore"/>.
/// </summary>
/// <remarks>
/// <strong>Ergonomics contract (R10):</strong> Mutating operations
/// (<see cref="RegisterAsync"/>, <see cref="UpdateAsync"/>, <see cref="PauseAsync"/>,
/// <see cref="ResumeAsync"/>) never cause immediate execution as a side effect.
/// The next fire time is always computed from the cadence; <see cref="TriggerAsync"/>
/// is the only API that fires a job on demand. Registering a one-shot cadence whose
/// instant is in the past throws <see cref="ArgumentOutOfRangeException"/> rather
/// than silently firing or silently advancing — this prevents the quartznet#636/#2180
/// and Hangfire#1637 class of surprises.
/// </remarks>
public interface IScheduleRegistry
{
    /// <summary>
    /// Registers a new job and begins evaluating its cadence.
    /// </summary>
    /// <param name="name">The unique job name; the registry's identity key.</param>
    /// <param name="cadence">The schedule that determines when the job fires.</param>
    /// <param name="missedFirePolicy">
    /// How the scheduler reconciles occurrences missed while the job could not fire.
    /// </param>
    /// <param name="dispatcher">The dispatcher that runs the job's work on each fire.</param>
    /// <param name="ct">A token to cancel the registration.</param>
    /// <returns>A task that completes when the job is registered.</returns>
    /// <exception cref="DuplicateJobNameException">
    /// A job with the same <paramref name="name"/> is already registered.
    /// </exception>
    ValueTask RegisterAsync(
        string name,
        Cadence cadence,
        MissedFirePolicy missedFirePolicy,
        IJobDispatcher dispatcher,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a job from the registry, stopping further fires.
    /// </summary>
    /// <param name="name">The name of the job to remove.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// A task whose result is <see langword="true"/> if a job was removed, or
    /// <see langword="false"/> if no job with that name was registered.
    /// </returns>
    ValueTask<bool> UnregisterAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Suspends a job: its cadence is no longer evaluated and it does not fire
    /// until resumed.
    /// </summary>
    /// <param name="name">The name of the job to pause.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task that completes when the job is paused.</returns>
    ValueTask PauseAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Resumes a paused job, returning it to active scheduling.
    /// </summary>
    /// <param name="name">The name of the job to resume.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task that completes when the job is resumed.</returns>
    ValueTask ResumeAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing job's cadence and missed-fire policy without causing an
    /// immediate fire as a side effect (R10). The new <c>NextFireAt</c> is computed
    /// from the schedule only; <see cref="TriggerAsync"/> is the sole fire-on-demand API.
    /// </summary>
    /// <param name="name">The name of the job to update.</param>
    /// <param name="cadence">
    /// The new schedule. Relative one-shot cadences are resolved against the registry's
    /// injected <see cref="TimeProvider"/> at update time (same contract as
    /// <see cref="RegisterAsync"/>). A past one-shot instant throws.
    /// </param>
    /// <param name="missedFirePolicy">The updated missed-fire reconciliation policy.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task that completes when the update is persisted and signalled.</returns>
    /// <exception cref="JobNotFoundException">
    /// No job with the supplied <paramref name="name"/> is registered.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The cadence resolves to an instant in the past.
    /// </exception>
    ValueTask UpdateAsync(
        string name,
        Cadence cadence,
        MissedFirePolicy missedFirePolicy,
        CancellationToken ct = default);

    /// <summary>
    /// Fires a job immediately, out of band from its cadence.
    /// </summary>
    /// <param name="name">The name of the job to trigger.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task that completes when the trigger has been dispatched.</returns>
    ValueTask TriggerAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns a snapshot of every registered job.
    /// </summary>
    /// <returns>A read-only list of job descriptors; empty when none are registered.</returns>
    IReadOnlyList<JobDescriptor> GetJobs();

    /// <summary>
    /// Returns the descriptor for a single job.
    /// </summary>
    /// <param name="name">The name of the job to look up.</param>
    /// <returns>
    /// The job's descriptor, or <see langword="null"/> if no job with that name
    /// is registered.
    /// </returns>
    JobDescriptor? GetJob(string name);
}
