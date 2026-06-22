// =============================================================================
// <copyright file="PriorityBinding.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>
/// Expresses the caller's intent for the concrete priority-queue binding used
/// by the work orchestrator. Resolved once at orchestrator construction.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Auto"/> is the default and always resolves to <see cref="MultiQueue"/>
/// — the lock-free relaxed queue. It never selects <see cref="Locking"/>; the
/// coarse-locking heap is a deliberate opt-in only.
/// </para>
/// <para>
/// <see cref="Locking"/> is the explicit opt-in for strict rank ordering at high
/// capacity. There is a documented tradeoff: the relaxed (lock-free) MultiQueue's
/// expected rank error (<c>(5/6)·n</c>, where <c>n = RoundUpToPowerOf2(4 × ProcessorCount)</c>)
/// grows with capacity, so a high-capacity <see cref="Auto"/> queue keeps relaxed
/// ordering unless the caller opts into <see cref="Locking"/>.
/// </para>
/// <para>
/// Each value selects its binding unconditionally: <see cref="Auto"/> and
/// <see cref="MultiQueue"/> both resolve to the MultiQueue; <see cref="Locking"/>
/// resolves to the locking heap.
/// </para>
/// </remarks>
public enum PriorityBinding
{
    /// <summary>
    /// Automatic selection (the default). Always resolves to <see cref="MultiQueue"/>
    /// — it never selects <see cref="Locking"/>. The resolved choice is logged once
    /// at construction and exposed via <c>WorkOrchestrator.ResolvedBinding</c>.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Unconditionally selects the coarse-locking binary-heap binding
    /// (<see cref="DispatchStrategy.PriorityLocking"/>): exact ordering and exact
    /// admission boundaries under a global lock. The explicit opt-in for strict rank
    /// ordering at high capacity — <see cref="Auto"/> never selects this, so callers
    /// who need exact priority order (rather than the MultiQueue's relaxed ordering,
    /// whose rank error grows with capacity) must request it deliberately. Use when
    /// strict priority ordering or the by-construction starvation bound is required
    /// regardless of core count.
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
