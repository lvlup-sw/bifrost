// =============================================================================
// <copyright file="EventStreamParallelLimit.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using TUnit.Core.Interfaces;

namespace Bifrost.Tests.Decorators;

/// <summary>
/// Caps how many event-stream decorator tests run concurrently. Each of these tests spins one or
/// more real subscriber tasks (<c>Task.Run</c> reading <c>GetEventStreamAsync</c>) with wall-clock
/// hang guards. Run at TUnit's full default parallelism — amplified by the <c>--coverage</c>
/// instrumentation overhead on CI's few-core runners — dozens of these subscribers starve the thread
/// pool, and a delayed reader or publish continuation can outlast a hang guard, surfacing as a
/// <c>TimeoutException</c> (issue #44). The tests also register each subscription on the test thread
/// before they publish, so a late reader cannot miss an event. Bounding the concurrency keeps the
/// suite fast without serialising it entirely. Mirrors <c>TickEngineParallelLimit</c>.
/// </summary>
public sealed class EventStreamParallelLimit : IParallelLimit
{
    /// <inheritdoc/>
    public int Limit => Math.Max(2, Environment.ProcessorCount);
}
