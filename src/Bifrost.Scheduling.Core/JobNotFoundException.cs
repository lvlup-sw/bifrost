// =============================================================================
// <copyright file="JobNotFoundException.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// Thrown when an operation references a job name that is not registered — for
/// example pausing, resuming, or triggering a job that does not exist.
/// </summary>
public sealed class JobNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JobNotFoundException"/> class.
    /// </summary>
    public JobNotFoundException()
        : base("No job with the specified name is registered.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JobNotFoundException"/> class for
    /// the given missing job name.
    /// </summary>
    /// <param name="jobName">The job name that could not be found.</param>
    public JobNotFoundException(string jobName)
        : base($"No job named '{jobName}' is registered.")
    {
        JobName = jobName;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JobNotFoundException"/> class for
    /// the given missing job name and an inner exception.
    /// </summary>
    /// <param name="jobName">The job name that could not be found.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public JobNotFoundException(string jobName, Exception innerException)
        : base($"No job named '{jobName}' is registered.", innerException)
    {
        JobName = jobName;
    }

    /// <summary>
    /// Gets the job name that could not be found, or <see langword="null"/> when the
    /// exception was created without one.
    /// </summary>
    public string? JobName { get; }
}
