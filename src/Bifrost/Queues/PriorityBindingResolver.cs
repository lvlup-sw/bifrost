// =============================================================================
// <copyright file="PriorityBindingResolver.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Numerics;

using Bifrost.Core;

namespace Bifrost.Queues;

/// <summary>
/// Maps a <see cref="PriorityBinding"/> intent to the concrete
/// <see cref="DispatchStrategy"/> that the work orchestrator will construct.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PriorityBinding.Auto"/> always resolves to
/// <see cref="DispatchStrategy.PriorityMultiQueue"/>: the lock-free relaxed queue
/// is the default. The coarse-locking heap
/// (<see cref="DispatchStrategy.PriorityLocking"/>) is reachable only via an
/// explicit <see cref="PriorityBinding.Locking"/> request — it is never selected
/// automatically.
/// </para>
/// <para>
/// Explicit <see cref="PriorityBinding.Locking"/> and
/// <see cref="PriorityBinding.MultiQueue"/> values are returned unconditionally.
/// </para>
/// <para>
/// <see cref="SubQueueCountFor"/> is exposed so the orchestrator and tests can
/// assert agreement between the resolver and the actual queue; the sub-queue count
/// <c>n</c> mirrors <c>ConcurrentPriorityQueue.s_defaultSubQueueCount</c> exactly.
/// </para>
/// </remarks>
internal static class PriorityBindingResolver
{
    /// <summary>
    /// Returns the default sub-queue count for a given processor count, mirroring
    /// <c>ConcurrentPriorityQueue.s_defaultSubQueueCount</c> exactly.
    /// </summary>
    /// <param name="processorCount">The number of logical processors available.</param>
    /// <returns>
    /// <c>RoundUpToPowerOf2(4 × <paramref name="processorCount"/>)</c> — the number
    /// of sub-queues that <c>ConcurrentPriorityQueue</c> would allocate with the
    /// given processor count.
    /// </returns>
    internal static int SubQueueCountFor(int processorCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processorCount);
        return (int)BitOperations.RoundUpToPowerOf2((uint)(4 * processorCount));
    }

    /// <summary>
    /// Resolves a <see cref="PriorityBinding"/> intent to a concrete
    /// <see cref="DispatchStrategy"/>.
    /// </summary>
    /// <param name="requested">The caller's binding intent.</param>
    /// <returns>
    /// <see cref="DispatchStrategy.PriorityLocking"/> only for an explicit
    /// <see cref="PriorityBinding.Locking"/>; otherwise
    /// <see cref="DispatchStrategy.PriorityMultiQueue"/> (including
    /// <see cref="PriorityBinding.Auto"/>).
    /// </returns>
    internal static DispatchStrategy Resolve(PriorityBinding requested)
        => requested switch
        {
            PriorityBinding.Locking => DispatchStrategy.PriorityLocking,

            // Auto and explicit MultiQueue both resolve to the relaxed lock-free queue.
            // Auto never selects Locking — that is a deliberate, explicit choice only.
            PriorityBinding.Auto or PriorityBinding.MultiQueue => DispatchStrategy.PriorityMultiQueue,

            // Any other value is an out-of-range cast (e.g. (PriorityBinding)999): fail
            // fast rather than silently defaulting it to a strategy the caller never named.
            _ => throw new ArgumentOutOfRangeException(nameof(requested), requested, "Unknown priority binding."),
        };
}
