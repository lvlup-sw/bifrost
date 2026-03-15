// =============================================================================
// <copyright file="WorkerInfo.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Autoscaling;

/// <summary>
/// Contains information about a registered worker.
/// </summary>
/// <remarks>
/// <para>
/// Worker info tracks the lifecycle state of individual workers,
/// including when they were created and whether they should stop.
/// </para>
/// <para>
/// The <see cref="IsIdle"/> property is mutable to allow workers to
/// update their state as they process work items. Use <see cref="MarkBusy"/>
/// and <see cref="MarkIdle"/> for thread-safe state transitions.
/// </para>
/// </remarks>
public sealed class WorkerInfo
{
    private int _stopRequested; // 1 = stop requested, 0 = running (int for Interlocked)
    private volatile int _isIdle; // 1 = idle, 0 = busy (int for Interlocked)

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerInfo"/> class.
    /// </summary>
    /// <param name="workerId">The unique identifier for the worker.</param>
    /// <exception cref="ArgumentNullException">Thrown when workerId is null.</exception>
    /// <remarks>
    /// The worker is created in an idle state with <see cref="CreatedAt"/> set to the current UTC time.
    /// The workerId should be unique within the registry to avoid tracking conflicts.
    /// </remarks>
    public WorkerInfo(string workerId)
    {
        ArgumentNullException.ThrowIfNull(workerId);
        WorkerId = workerId;
        CreatedAt = DateTimeOffset.UtcNow;
        _isIdle = 1; // Start idle
    }

    /// <summary>
    /// Gets the unique identifier for this worker.
    /// </summary>
    /// <value>The worker identifier.</value>
    /// <remarks>
    /// The worker ID is immutable once set during construction. It should uniquely identify
    /// the worker within the <see cref="WorkerRegistry"/> for the lifetime of the worker.
    /// </remarks>
    public string WorkerId { get; }

    /// <summary>
    /// Gets the timestamp when this worker was created.
    /// </summary>
    /// <value>The creation timestamp.</value>
    /// <remarks>
    /// This timestamp is set to <see cref="DateTimeOffset.UtcNow"/> during construction and is
    /// immutable thereafter. It can be used for worker age calculations during scale-down decisions.
    /// </remarks>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the worker is currently idle.
    /// </summary>
    /// <value>True if the worker is idle; false if processing work.</value>
    /// <remarks>
    /// <para>
    /// Workers update this flag as they transition between idle and busy states.
    /// The autoscaling engine uses this to prioritize which workers to stop.
    /// </para>
    /// <para>
    /// Prefer using <see cref="MarkBusy"/> and <see cref="MarkIdle"/> for
    /// thread-safe state transitions.
    /// </para>
    /// </remarks>
    public bool IsIdle
    {
        get => _isIdle == 1;
        set => Interlocked.Exchange(ref _isIdle, value ? 1 : 0);
    }

    /// <summary>
    /// Gets a value indicating whether the worker has been requested to stop.
    /// </summary>
    /// <value>True if the worker should stop; false otherwise.</value>
    /// <remarks>
    /// This is a soft stop - the worker should complete its current work
    /// item before exiting. The flag is read by the worker loop to determine
    /// when to exit gracefully.
    /// </remarks>
    public bool StopRequested => Volatile.Read(ref _stopRequested) == 1;

    /// <summary>
    /// Marks the worker as busy (not idle).
    /// </summary>
    /// <remarks>
    /// This method is thread-safe and uses <see cref="Interlocked.Exchange(ref int, int)"/>
    /// to ensure atomic state transitions.
    /// </remarks>
    public void MarkBusy() => Interlocked.Exchange(ref _isIdle, 0);

    /// <summary>
    /// Marks the worker as idle.
    /// </summary>
    /// <remarks>
    /// This method is thread-safe and uses <see cref="Interlocked.Exchange(ref int, int)"/>
    /// to ensure atomic state transitions.
    /// </remarks>
    public void MarkIdle() => Interlocked.Exchange(ref _isIdle, 1);

    /// <summary>
    /// Requests the worker to stop gracefully.
    /// </summary>
    /// <remarks>
    /// This is a soft stop signal that sets <see cref="StopRequested"/> to true. The worker
    /// should complete its current work item before checking this flag and exiting gracefully.
    /// This method is thread-safe via <see cref="Interlocked.Exchange(ref int, int)"/>
    /// to ensure atomic state transitions, matching the pattern used by
    /// <see cref="MarkBusy"/> and <see cref="MarkIdle"/>.
    /// </remarks>
    internal void RequestStop()
    {
        Interlocked.Exchange(ref _stopRequested, 1);
    }
}