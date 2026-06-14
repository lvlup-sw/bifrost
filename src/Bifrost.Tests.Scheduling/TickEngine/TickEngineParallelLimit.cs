// =============================================================================
// <copyright file="TickEngineParallelLimit.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using TUnit.Core.Interfaces;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// Caps how many tick-engine tests run concurrently. Each tick-engine test starts a
/// real <c>ScheduleTickLoop</c> <c>BackgroundService</c>, and some hold a job dispatch
/// "in flight" by blocking a pool thread. Running dozens of these in parallel can
/// starve the thread pool — a blocked dispatch never gets a thread to run on, and the
/// loop's timer/continuation wakeups queue behind it. Bounding the concurrency keeps
/// the suite fully deterministic without serialising it entirely.
/// </summary>
public sealed class TickEngineParallelLimit : IParallelLimit
{
    /// <inheritdoc/>
    public int Limit => Math.Max(2, Environment.ProcessorCount);
}
