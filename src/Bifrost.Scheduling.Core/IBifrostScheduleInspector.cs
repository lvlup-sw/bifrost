// =============================================================================
// <copyright file="IBifrostScheduleInspector.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// A read-only inspection surface over the scheduler (DR-8). Consumers use this to
/// observe registered jobs and current metric values without the ability to mutate
/// schedule state — it deliberately exposes no register, unregister, pause, resume,
/// or trigger operation. Those live on the read/write <see cref="IScheduleRegistry"/>.
/// </summary>
/// <remarks>
/// This is the safe surface to hand to a diagnostics endpoint, dashboard, or admin
/// read view: it cannot change what the scheduler does.
/// </remarks>
public interface IBifrostScheduleInspector
{
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
    /// The job's descriptor, or <see langword="null"/> if no job with that name is
    /// registered.
    /// </returns>
    JobDescriptor? GetJob(string name);

    /// <summary>
    /// Returns a point-in-time snapshot of current scheduler metric values.
    /// </summary>
    /// <returns>The current metrics snapshot.</returns>
    ScheduleMetricsSnapshot GetMetricsSnapshot();
}
