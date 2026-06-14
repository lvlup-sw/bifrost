// =============================================================================
// <copyright file="SoakModel.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

namespace Bifrost.Benchmarks.Concurrency;

/// <summary>
/// The priority work-queue binding a consumer-shaped soak run (design DR-8) drives. Both are
/// the same composition — priority structure + item-counting semaphore + class watermarks —
/// over different structures, and the soak exists to compare them in Bifrost's actual regime
/// (1–8 workers, seconds-long work items, low queue contention).
/// </summary>
public enum SoakBinding
{
    /// <summary>
    /// The exact-ordering lock-based binding (<c>LockingPriorityWorkQueue</c>): every dequeue
    /// returns the true minimum virtual-time key; all heap operations serialize on one lock.
    /// </summary>
    LockingPriority,

    /// <summary>
    /// The relaxed lock-free MultiQueue binding (<c>ConcurrentPriorityWorkQueue</c>): two-choice
    /// dequeue with a bounded expected rank error, built for high contention.
    /// </summary>
    MultiQueuePriority,
}

/// <summary>
/// The workload shape of one soak run (design DR-8) — every knob the <c>soak</c> CLI verb
/// exposes, with the DR-8 defaults: log-uniform 50 ms–5 s simulated work, a 20/30/50
/// Interactive/Default/Batch arrival mix, capacity 128, a 40–60 % occupancy target band, and
/// periodic Batch bursts that push into watermark territory. Worker count and binding are run
/// parameters, not workload shape, so they live outside this record.
/// </summary>
/// <param name="DurationSeconds">The wall-clock soak window per run, in seconds. Default 600.</param>
/// <param name="Capacity">The bounded queue capacity. Default 128.</param>
/// <param name="MinWorkMs">The minimum simulated work-item duration, in milliseconds. Default 50.</param>
/// <param name="MaxWorkMs">The maximum simulated work-item duration, in milliseconds. Default 5000.</param>
/// <param name="InteractiveShare">The Interactive fraction of offered arrivals. Default 0.20.</param>
/// <param name="DefaultShare">The Default fraction of offered arrivals. Default 0.30 (Batch takes the remainder).</param>
/// <param name="TargetOccupancyLow">The lower edge of the occupancy band the arrival controller holds, as a fraction of capacity. Default 0.40.</param>
/// <param name="TargetOccupancyHigh">The upper edge of the occupancy band, as a fraction of capacity. Default 0.60.</param>
/// <param name="BurstIntervalSeconds">The period between Batch bursts, in seconds. Default 30.</param>
/// <param name="BurstSize">The number of Batch items each burst enqueues back-to-back. Default 64 — sized to push a mid-band queue into watermark territory.</param>
/// <param name="InteractiveBoostSeconds">The Interactive boost window in seconds — the DR-5 starvation bound the probe checks against. Default 30.</param>
/// <param name="Seed">The base RNG seed; each producer derives its own deterministic stream from it. Default 42.</param>
public sealed record SoakConfig(
    double DurationSeconds = 600,
    int Capacity = 128,
    int MinWorkMs = 50,
    int MaxWorkMs = 5000,
    double InteractiveShare = 0.20,
    double DefaultShare = 0.30,
    double TargetOccupancyLow = 0.40,
    double TargetOccupancyHigh = 0.60,
    double BurstIntervalSeconds = 30,
    int BurstSize = 64,
    double InteractiveBoostSeconds = 30,
    int Seed = 42)
{
    /// <summary>
    /// Gets the Batch fraction of offered arrivals: whatever <see cref="InteractiveShare"/> and
    /// <see cref="DefaultShare"/> leave over (0.50 at the defaults).
    /// </summary>
    public double BatchShare => 1.0 - InteractiveShare - DefaultShare;

    /// <summary>
    /// Gets the mean simulated work-item duration in seconds. For a log-uniform draw on
    /// <c>[min, max]</c> the mean is <c>(max − min) / ln(max / min)</c> — ≈ 1.075 s at the
    /// 50 ms–5 s defaults — which sizes the arrival controller's saturation estimate.
    /// </summary>
    public double MeanWorkSeconds
        => MaxWorkMs == MinWorkMs
            ? MinWorkMs / 1000.0
            : (MaxWorkMs - MinWorkMs) / (Math.Log((double)MaxWorkMs / MinWorkMs) * 1000.0);
}

/// <summary>
/// The per-class slice of one soak run's results: admission counters, dispatch fairness
/// shares, and the queue-wait distribution (enqueue-timestamp to dequeue, via the envelope's
/// <c>EnqueuedAtTicks</c>).
/// </summary>
/// <param name="Class">The work class this row describes.</param>
/// <param name="Offered">The number of arrivals offered (every <c>TryEnqueue</c> attempt, bursts included).</param>
/// <param name="Rejected">The number of offered arrivals the binding refused (watermark shed or hard capacity).</param>
/// <param name="Dispatched">The number of items a worker dequeued during the window.</param>
/// <param name="OfferedShare">This class's fraction of all offered arrivals.</param>
/// <param name="DispatchShare">This class's fraction of all dispatches — compare against <paramref name="OfferedShare"/> for fairness.</param>
/// <param name="WaitP50Ms">The median queue wait, in milliseconds.</param>
/// <param name="WaitP95Ms">The 95th-percentile queue wait, in milliseconds.</param>
/// <param name="WaitP99Ms">The 99th-percentile queue wait, in milliseconds.</param>
/// <param name="WaitMaxMs">The maximum observed queue wait, in milliseconds.</param>
public sealed record SoakClassResult(
    WorkClass Class,
    long Offered,
    long Rejected,
    long Dispatched,
    double OfferedShare,
    double DispatchShare,
    double WaitP50Ms,
    double WaitP95Ms,
    double WaitP99Ms,
    double WaitMaxMs);

/// <summary>
/// The immutable result of one consumer-shaped soak run (design DR-8): per-class admission,
/// fairness, and queue-wait stats; queue-occupancy tracking against the target band;
/// allocation-stability samples (allocated-bytes rate and GC collection counts); and the
/// starvation probe comparing the worst Batch queue wait against the boost-window bound.
/// </summary>
/// <param name="Binding">The priority binding the run drove.</param>
/// <param name="Workers">The number of concurrent consumers.</param>
/// <param name="DurationSeconds">The configured soak window, in seconds.</param>
/// <param name="Capacity">The bounded queue capacity.</param>
/// <param name="Classes">The per-class result rows, ordered Interactive, Default, Batch.</param>
/// <param name="ResidualCount">The items still queued when the window closed (admitted but never dispatched).</param>
/// <param name="MeanOccupancyPct">The mean sampled queue occupancy, as a percentage of capacity.</param>
/// <param name="MaxOccupancyPct">The maximum sampled occupancy, as a percentage of capacity.</param>
/// <param name="InBandPct">The percentage of occupancy samples inside the configured target band.</param>
/// <param name="AllocBytesPerMinMean">The mean allocation rate across sampled intervals, in bytes per minute (process-wide, harness included).</param>
/// <param name="AllocBytesPerMinMin">The minimum per-interval allocation rate, in bytes per minute.</param>
/// <param name="AllocBytesPerMinMax">The maximum per-interval allocation rate, in bytes per minute — flat min/max spread = allocation-stable.</param>
/// <param name="Gen0Collections">Gen-0 GC collections during the run.</param>
/// <param name="Gen1Collections">Gen-1 GC collections during the run.</param>
/// <param name="Gen2Collections">Gen-2 GC collections during the run — should be at or near zero in steady state.</param>
/// <param name="MaxBatchWaitSeconds">The maximum observed Batch queue wait, in seconds — the starvation probe's measurement.</param>
/// <param name="BoostWindowSeconds">The configured Interactive boost window — the DR-5 priority-overtaking bound the probe references.</param>
/// <param name="DepthAdjustedBoundSeconds">
/// The measurable ceiling the probe checks: boost window + full-queue drain time
/// (<c>capacity × meanWork / workers</c>). The raw boost window bounds only the
/// priority-induced overtaking; an admitted item additionally waits behind whatever depth is
/// already queued, so the absolute wait is bounded by the sum, not by the window alone.
/// </param>
/// <param name="StarvationBoundHeld">Whether <paramref name="MaxBatchWaitSeconds"/> stayed within <paramref name="DepthAdjustedBoundSeconds"/>.</param>
public sealed record SoakResult(
    SoakBinding Binding,
    int Workers,
    double DurationSeconds,
    int Capacity,
    IReadOnlyList<SoakClassResult> Classes,
    int ResidualCount,
    double MeanOccupancyPct,
    double MaxOccupancyPct,
    double InBandPct,
    double AllocBytesPerMinMean,
    double AllocBytesPerMinMin,
    double AllocBytesPerMinMax,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double MaxBatchWaitSeconds,
    double BoostWindowSeconds,
    double DepthAdjustedBoundSeconds,
    bool StarvationBoundHeld);
