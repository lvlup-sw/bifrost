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
/// Idle and stop-requested flags are packed into a single atomic state word so
/// idle-only stop claims (<see cref="TryRequestStopIfIdle"/>) never race with
/// <see cref="MarkBusy"/>, while <c>_stopGate</c> serializes retirement signaling
/// with disposal.
/// </para>
/// </remarks>
public sealed class WorkerInfo
{
    private const int IdleBit = 1;
    private const int StopBit = 2;

    private readonly Lock _stopGate = new();
    private readonly CancellationTokenSource _stopSource = new();
    private bool _stopSourceDisposed;

    // Bit 0 = IsIdle (initial: 1), Bit 1 = StopRequested (initial: 0).
    private int _state = IdleBit;

    internal CancellationToken RetirementToken => _stopSource.Token;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerInfo"/> class.
    /// </summary>
    /// <param name="workerId">The unique identifier for the worker.</param>
    /// <exception cref="ArgumentNullException">Thrown when workerId is null.</exception>
    public WorkerInfo(string workerId)
    {
        ArgumentNullException.ThrowIfNull(workerId);
        WorkerId = workerId;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Gets the unique identifier for this worker.
    /// </summary>
    /// <value>The worker identifier.</value>
    public string WorkerId { get; }

    /// <summary>
    /// Gets the timestamp when this worker was created.
    /// </summary>
    /// <value>The creation timestamp.</value>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the worker is currently idle.
    /// </summary>
    /// <value>True if the worker is idle; false if processing work.</value>
    public bool IsIdle
    {
        get => (Volatile.Read(ref _state) & IdleBit) != 0;
        set
        {
            if (value)
            {
                MarkIdle();
            }
            else
            {
                MarkBusy();
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the worker has been requested to stop.
    /// </summary>
    /// <value>True if the worker should stop; false otherwise.</value>
    public bool StopRequested => (Volatile.Read(ref _state) & StopBit) != 0;

    /// <summary>
    /// Marks the worker as busy (not idle).
    /// </summary>
    public void MarkBusy()
    {
        int current;
        int next;
        do
        {
            current = Volatile.Read(ref _state);
            next = current & ~IdleBit;
        }
        while (Interlocked.CompareExchange(ref _state, next, current) != current);
    }

    /// <summary>
    /// Marks the worker as idle.
    /// </summary>
    public void MarkIdle()
    {
        int current;
        int next;
        do
        {
            current = Volatile.Read(ref _state);
            next = current | IdleBit;
        }
        while (Interlocked.CompareExchange(ref _state, next, current) != current);
    }

    /// <summary>
    /// Requests the worker to stop gracefully.
    /// </summary>
    /// <returns><see langword="true"/> if this call initiated the stop.</returns>
    internal bool RequestStop()
    {
        lock (_stopGate)
        {
            if (_stopSourceDisposed)
            {
                return false;
            }

            int current;
            do
            {
                current = Volatile.Read(ref _state);
                if ((current & StopBit) != 0)
                {
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _state, current | StopBit, current) != current);

            _stopSource.Cancel();
            return true;
        }
    }

    /// <summary>
    /// Atomically requests the worker to stop only if it is currently idle and not
    /// already stopping.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if this call claimed the stop while the worker was idle;
    /// <see langword="false"/> if the worker was busy, already stopping, or disposed.
    /// </returns>
    internal bool TryRequestStopIfIdle()
    {
        lock (_stopGate)
        {
            if (_stopSourceDisposed)
            {
                return false;
            }

            while (true)
            {
                var current = Volatile.Read(ref _state);
                if ((current & (IdleBit | StopBit)) != IdleBit)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _state, current | StopBit, current) == current)
                {
                    _stopSource.Cancel();
                    return true;
                }
            }
        }
    }

    internal void DisposeStopSource()
    {
        lock (_stopGate)
        {
            if (_stopSourceDisposed)
            {
                return;
            }

            _stopSourceDisposed = true;
            _stopSource.Dispose();
        }
    }
}
