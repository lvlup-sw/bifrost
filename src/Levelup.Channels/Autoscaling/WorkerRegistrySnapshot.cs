// =============================================================================
// <copyright file="WorkerRegistrySnapshot.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Levelup.Channels.Autoscaling;

/// <summary>
/// Represents an immutable snapshot of the worker registry state at a point in time.
/// </summary>
/// <param name="TotalWorkers">The total number of workers in the registry.</param>
/// <param name="ActiveWorkers">The number of workers that are actively running.</param>
/// <param name="IdleWorkers">The number of active workers that are idle (not processing work).</param>
/// <param name="BusyWorkers">The number of active workers that are busy (processing work).</param>
/// <param name="StoppingWorkers">The number of active workers that have been requested to stop.</param>
/// <param name="Timestamp">The timestamp when this snapshot was taken.</param>
/// <remarks>
/// <para>
/// This snapshot provides a consistent view of the worker registry state for diagnostics
/// and monitoring purposes. Since the registry is accessed concurrently, individual property
/// reads may observe different states; this snapshot captures all values atomically.
/// </para>
/// <para>
/// The invariant <c>IdleWorkers + BusyWorkers + StoppingWorkers == ActiveWorkers</c> should always hold.
/// Workers are categorized exclusively: idle workers not requested to stop, busy workers not
/// requested to stop, and workers that have been requested to stop (regardless of idle/busy state).
/// </para>
/// </remarks>
public sealed record WorkerRegistrySnapshot(
    int TotalWorkers,
    int ActiveWorkers,
    int IdleWorkers,
    int BusyWorkers,
    int StoppingWorkers,
    DateTimeOffset Timestamp);
