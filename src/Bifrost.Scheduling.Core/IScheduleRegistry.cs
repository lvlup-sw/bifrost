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
