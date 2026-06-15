// =============================================================================
// <copyright file="ScheduleTickLoop.Logging.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Microsoft.Extensions.Logging;

namespace Bifrost.Scheduling.TickEngine;

/// <summary>
/// Source-generated, AOT-safe log messages for <see cref="ScheduleTickLoop"/>.
/// </summary>
public sealed partial class ScheduleTickLoop
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Job '{JobName}' missed-fire backlog reached the catch-up cap of " +
            "{Cap}; older missed occurrences were discarded.")]
    private partial void LogMissedFireCapReached(string jobName, int cap);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "SchedulerOptions.MultiInstanceExpected is set with a non-exclusive " +
            "store: Bifrost scheduling is always-leader, so every instance will tick " +
            "every job and duplicate fires will occur. Run scheduling on exactly one " +
            "process until the storage adapter adds multi-instance coordination.")]
    private partial void LogMultiInstanceWarning();

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Critical,
        Message = "The scheduler tick loop faulted and will restart.")]
    private partial void LogTickLoopFaulted(Exception exception);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Critical,
        Message = "The scheduler tick loop exceeded {MaxRestarts} restarts within " +
            "{WindowSeconds}s and has transitioned to the faulted state; scheduling " +
            "has stopped.")]
    private partial void LogTickLoopGaveUp(int maxRestarts, double windowSeconds);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "The scheduler clock moved backwards (from {Previous} to {Current}); " +
            "re-arming against the current time to avoid a lost or duplicate fire.")]
    private partial void LogClockSkew(DateTimeOffset previous, DateTimeOffset current);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Checkpointing the fire of job '{JobName}' to the store failed; the " +
            "fire already dispatched and the next fire is unaffected (best-effort).")]
    private partial void LogCheckpointFailed(string jobName, Exception exception);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Error,
        Message = "Job '{JobName}': Cadence.ComputeNextFire threw; the job has been " +
            "marked Faulted and will not fire again until it is re-registered " +
            "(DR-10, Task 48).")]
    private partial void LogCadenceComputeNextFireFailed(string jobName, Exception exception);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Warning,
        Message = "Disposing the per-fire service scope for job '{JobName}' failed; the " +
            "fire already completed and scheduling is unaffected (best-effort).")]
    private partial void LogScopeDisposeFailed(string jobName, Exception exception);
}
