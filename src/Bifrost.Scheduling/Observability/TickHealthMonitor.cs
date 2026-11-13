// =============================================================================
// <copyright file="TickHealthMonitor.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Observability;

/// <summary>
/// The default <see cref="ITickHealthMonitor"/> (DR-8): records the loop's
/// discriminated <see cref="TickLivenessState"/> and a fixed-size rolling window of
/// the most recent fire outcomes. The tick loop updates it; the
/// <see cref="SchedulerHealthCheck"/> reads it.
/// </summary>
internal sealed class TickHealthMonitor : ITickHealthMonitor
{
    /// <summary>
    /// The number of recent fire outcomes the failure-rate window retains.
    /// </summary>
    public const int WindowSize = 100;

    private readonly object gate = new();

    // A ring buffer of recent outcomes: true = failure, false = success. The running
    // failureCount tracks the number of failures currently in the window so the rate
    // is computed without scanning.
    private readonly bool[] window = new bool[WindowSize];

    private TickLivenessState livenessState;
    private DateTimeOffset? lastTickAt;
    private int count;
    private int head;
    private int failureCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="TickHealthMonitor"/> class.
    /// </summary>
    /// <param name="timeProvider">
    /// Optional clock; when supplied, observation begins immediately in
    /// <see cref="TickLivenessKind.WaitingForFirstTick"/> so a loop that stalls before
    /// its first tick is detected.
    /// </param>
    public TickHealthMonitor(TimeProvider? timeProvider = null)
    {
        this.livenessState = timeProvider is not null
            ? TickLivenessState.WaitingForFirstTick(timeProvider.GetUtcNow())
            : TickLivenessState.Unstarted;
    }

    /// <inheritdoc/>
    public TickLivenessState LivenessState
    {
        get
        {
            lock (this.gate)
            {
                return this.livenessState;
            }
        }
    }

    /// <inheritdoc/>
    public DateTimeOffset? LastTickAt
    {
        get
        {
            lock (this.gate)
            {
                return this.lastTickAt;
            }
        }
    }

    /// <inheritdoc/>
    public double FailureRate
    {
        get
        {
            lock (this.gate)
            {
                return this.count == 0 ? 0d : (double)this.failureCount / this.count;
            }
        }
    }

    /// <inheritdoc/>
    public int RecentFireCount
    {
        get
        {
            lock (this.gate)
            {
                return this.count;
            }
        }
    }

    /// <inheritdoc/>
    public IdleWaitObservation IdleWait
    {
        get
        {
            lock (this.gate)
            {
                return this.livenessState.Kind switch
                {
                    TickLivenessKind.IdleEmpty => new IdleWaitObservation(IsWaiting: true, Deadline: null),
                    TickLivenessKind.IdleUntil => new IdleWaitObservation(IsWaiting: true, this.livenessState.Timestamp),
                    _ => new IdleWaitObservation(IsWaiting: false, Deadline: null),
                };
            }
        }
    }

    /// <inheritdoc/>
    public void RecordLoopStarted(DateTimeOffset at)
    {
        lock (this.gate)
        {
            if (this.livenessState.Kind == TickLivenessKind.Unstarted)
            {
                this.livenessState = TickLivenessState.WaitingForFirstTick(at);
            }
        }
    }

    /// <inheritdoc/>
    public void RecordTick(DateTimeOffset at)
    {
        lock (this.gate)
        {
            this.lastTickAt = at;
            this.livenessState = TickLivenessState.Active(at);
        }
    }

    /// <inheritdoc/>
    public void RecordIdleWait(DateTimeOffset? deadline)
    {
        lock (this.gate)
        {
            this.livenessState = deadline is { } wakeBy
                ? TickLivenessState.IdleUntil(wakeBy)
                : TickLivenessState.IdleEmpty;
        }
    }

    /// <inheritdoc/>
    public void RecordFireOutcome(bool success)
    {
        var isFailure = !success;
        lock (this.gate)
        {
            if (this.count == WindowSize)
            {
                // Window full: evict the oldest outcome before overwriting it.
                if (this.window[this.head])
                {
                    this.failureCount--;
                }
            }
            else
            {
                this.count++;
            }

            this.window[this.head] = isFailure;
            if (isFailure)
            {
                this.failureCount++;
            }

            this.head = (this.head + 1) % WindowSize;
        }
    }
}
