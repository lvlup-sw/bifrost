// =============================================================================
// <copyright file="ThroughputModel.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry.Benchmarks/Throughput/ThroughputModel.cs);
// NaiveBaseline renamed to LockingBaseline per design DR-1 (NaiveConcurrentPriorityQueue -> LockingPriorityQueue).

namespace Bifrost.Benchmarks.Concurrency;

/// <summary>
/// The cross-thread workload shapes the fixed-window throughput harness sweeps.
/// Each shape stresses a different part of the relaxed MultiQueue design so a sweep tells a story,
/// not just a single number.
/// </summary>
public enum ThroughputWorkload
{
    /// <summary>
    /// Each thread alternates an enqueue with a dequeue, drawing priorities from a wide,
    /// thread-seeded random range. The steady-state mixed load: population stays roughly flat and
    /// every thread both produces and consumes, so contention is symmetric.
    /// </summary>
    UniformMixed5050,

    /// <summary>
    /// Half the threads enqueue only and half dequeue only — the workload that punishes designs
    /// (such as the k-LSM) whose producer and consumer paths fight over the same hot structure.
    /// Producers never dequeue and consumers never enqueue, so roles are disjoint per thread.
    /// </summary>
    SplitProducerConsumer,

    /// <summary>
    /// The same 50/50 mix as <see cref="UniformMixed5050"/> but priorities are drawn from only 1000
    /// distinct values, so many elements share a priority and the comparer ties resolve through the
    /// secondary ordering far more often — a stress on the narrow-key-range path.
    /// </summary>
    NarrowKeyRange,

    /// <summary>
    /// Each thread first pre-populates its share of a fixed total (1_000_000 / threadCount), then all
    /// threads dequeue only until the shared queue is empty or the window ends. Measures pure
    /// drain throughput; ops are counted as successful dequeues only.
    /// </summary>
    Drain,
}

/// <summary>
/// The queue implementation (and removal semantics) a throughput run drives.
/// </summary>
public enum ThroughputTarget
{
    /// <summary>
    /// The MultiQueue <c>ConcurrentPriorityQueue</c> using the relaxed two-choice <c>TryDequeue</c>:
    /// the near-linear-scaling throughput path with a bounded rank error.
    /// </summary>
    MultiQueueRelaxed,

    /// <summary>
    /// The MultiQueue <c>ConcurrentPriorityQueue</c> using the strict <c>TryDequeueMin</c>: the exact
    /// global-minimum path, an O(n) scan that carries no scalability claim — included so the sweep
    /// shows the cost of strictness against the relaxed path.
    /// </summary>
    MultiQueueDequeueMin,

    /// <summary>
    /// The <c>LockingPriorityQueue</c> global-lock baseline: every operation serializes on a
    /// single lock, the floor the relaxed design must beat to justify itself.
    /// </summary>
    LockingBaseline,
}

/// <summary>
/// The immutable result of one fixed-window throughput run: which target and workload ran, how many
/// threads, the wall-clock window, the aggregated operation total and rate, and the (false-sharing
/// padded, then unpadded back into a plain array) per-thread operation counts.
/// </summary>
/// <param name="Target">The queue implementation and removal semantics that were exercised.</param>
/// <param name="Workload">The cross-thread workload shape that was run.</param>
/// <param name="ThreadCount">The number of worker threads that participated.</param>
/// <param name="Stickiness">
/// The stickiness factor <c>s</c> the (relaxed) target was constructed with — <c>1</c> for the
/// pre-stickiness behavior. Recorded so a sweep over <c>s</c> is self-describing; <c>1</c> for the
/// locking baseline, which has no sub-queue sampling.
/// </param>
/// <param name="Window">The fixed wall-clock window the worker threads ran for.</param>
/// <param name="TotalOps">
/// The sum of every worker thread's operation count. For most workloads an "operation" is one loop
/// iteration's countable unit (an enqueue, or a dequeue attempt depending on workload); for
/// <see cref="ThroughputWorkload.Drain"/> it is a <i>successful</i> dequeue only.
/// </param>
/// <param name="OpsPerSecond"><see cref="TotalOps"/> divided by the elapsed window in seconds.</param>
/// <param name="PerThreadOps">
/// The per-thread operation counts, index <c>i</c> being worker thread <c>i</c>. Summing this list
/// equals <see cref="TotalOps"/>.
/// </param>
public sealed record ThroughputResult(
    ThroughputTarget Target,
    ThroughputWorkload Workload,
    int ThreadCount,
    int Stickiness,
    TimeSpan Window,
    long TotalOps,
    double OpsPerSecond,
    IReadOnlyList<long> PerThreadOps);
