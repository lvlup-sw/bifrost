// =============================================================================
// <copyright file="DuplicateJobNameException.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// Thrown when a job is registered under a name that is already in use. Job
/// names are the registry's identity key, so they must be unique.
/// </summary>
public sealed class DuplicateJobNameException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicateJobNameException"/>
    /// class.
    /// </summary>
    public DuplicateJobNameException()
        : base("A job with the same name is already registered.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicateJobNameException"/>
    /// class for the given duplicate job name.
    /// </summary>
    /// <param name="jobName">The duplicate job name that triggered the conflict.</param>
    public DuplicateJobNameException(string jobName)
        : base($"A job named '{jobName}' is already registered.")
    {
        JobName = jobName;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicateJobNameException"/>
    /// class for the given duplicate job name and an inner exception.
    /// </summary>
    /// <param name="jobName">The duplicate job name that triggered the conflict.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public DuplicateJobNameException(string jobName, Exception innerException)
        : base($"A job named '{jobName}' is already registered.", innerException)
    {
        JobName = jobName;
    }

    /// <summary>
    /// Gets the duplicate job name that triggered the conflict, or
    /// <see langword="null"/> when the exception was created without one.
    /// </summary>
    public string? JobName { get; }
}
