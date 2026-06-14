// =============================================================================
// <copyright file="TickHealthMonitor.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Observability;

/// <summary>
/// The default <see cref="ITickHealthMonitor"/> (DR-8): records the last-tick instant
/// and a fixed-size rolling window of the most recent fire outcomes. The tick loop
/// updates it; the <see cref="SchedulerHealthCheck"/> reads it.
/// </summary>
/// <remarks>
/// The rolling window is a fixed-capacity ring buffer of the last
/// <see cref="WindowSize"/> outcomes, so the failure rate reflects only recent fires
/// and an old burst of failures rolls out once enough later fires arrive. All state
/// is guarded by a single lock; updates and reads are cheap and infrequent relative
/// to the cost of a fire, so contention is a non-issue.
/// </remarks>
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

    private long lastTickTicks = long.MinValue;
    private bool lastTickSet;
    private int count;
    private int head;
    private int failureCount;

    /// <inheritdoc/>
    public DateTimeOffset? LastTickAt
    {
        get
        {
            lock (this.gate)
            {
                return this.lastTickSet
                    ? new DateTimeOffset(this.lastTickTicks, TimeSpan.Zero)
                    : null;
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
    public void RecordTick(DateTimeOffset at)
    {
        lock (this.gate)
        {
            this.lastTickTicks = at.UtcTicks;
            this.lastTickSet = true;
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
