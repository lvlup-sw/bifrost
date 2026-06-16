// =============================================================================
// <copyright file="PriorityBinding.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>
/// Expresses the caller's intent for the concrete priority-queue binding used
/// by the work orchestrator. Resolved once at orchestrator construction from the
/// hardware and capacity context when <see cref="Auto"/> is chosen.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Auto"/> is the default and the recommended choice. It selects
/// <see cref="Locking"/> when the MultiQueue's expected rank error
/// (<c>(5/6)·n</c>, where <c>n = RoundUpToPowerOf2(4 × ProcessorCount)</c>)
/// reaches half the queue capacity — a regime where the relaxed dequeue cannot
/// honor priority order. Below that threshold it selects <see cref="MultiQueue"/>.
/// </para>
/// <para>
/// <see cref="Locking"/> and <see cref="MultiQueue"/> bypass the heuristic
/// entirely and select the binding unconditionally, for callers who know their
/// workload.
/// </para>
/// </remarks>
public enum PriorityBinding
{
    /// <summary>
    /// Hardware×capacity-aware automatic selection (the default). The orchestrator
    /// chooses <see cref="Locking"/> when the relaxed dequeue's expected rank error
    /// would materially degrade priority ordering; otherwise <see cref="MultiQueue"/>.
    /// The resolved choice is logged once at construction and exposed via
    /// <c>WorkOrchestrator.ResolvedBinding</c>.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Unconditionally selects the coarse-locking binary-heap binding
    /// (<see cref="DispatchStrategy.PriorityLocking"/>): exact ordering and exact
    /// admission boundaries under a global lock. Use when strict priority ordering
    /// or the by-construction starvation bound is required regardless of core count.
    /// </summary>
    Locking,

    /// <summary>
    /// Unconditionally selects the lock-free MultiQueue binding
    /// (<see cref="DispatchStrategy.PriorityMultiQueue"/>): relaxed (approximate)
    /// ordering under high concurrency. Use when throughput under many simultaneous
    /// producers matters more than strict priority ordering.
    /// </summary>
    MultiQueue,
}
