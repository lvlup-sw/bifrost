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
/// Explicit <see cref="PriorityBinding.Locking"/> and
/// <see cref="PriorityBinding.MultiQueue"/> values are returned unconditionally
/// without consulting the heuristic.
/// </para>
/// <para>
/// <see cref="PriorityBinding.Auto"/> resolves by comparing the MultiQueue's
/// expected rank error to the queue capacity: when the error reaches half the
/// capacity (<see cref="AutoLockRankErrorFraction"/> = 0.5), the locking binding
/// is chosen because the relaxed dequeue can no longer honor priority order at
/// that scale. Below that threshold the MultiQueue is chosen.
/// </para>
/// <para>
/// The integer-safe predicate equivalent to <c>(5/6)·n ≥ capacity × 0.5</c>
/// is <c>5 × n ≥ 3 × capacity</c> (multiply both sides by 6, substitute
/// <c>0.5 = 3/6</c>). This avoids floating-point arithmetic on the hot path
/// and is exact for any integer inputs.
/// </para>
/// <para>
/// The sub-queue count <c>n</c> mirrors <c>ConcurrentPriorityQueue.s_defaultSubQueueCount</c>
/// exactly; <see cref="SubQueueCountFor"/> is exposed so the orchestrator and tests
/// can assert agreement between the resolver and the actual queue.
/// </para>
/// </remarks>
internal static class PriorityBindingResolver
{
    /// <summary>
    /// The rank-error fraction at which the resolver switches from MultiQueue to Locking.
    /// An <see cref="PriorityBinding.Auto"/> binding selects <see cref="DispatchStrategy.PriorityLocking"/>
    /// when <c>(5/6)·n ≥ capacity × AutoLockRankErrorFraction</c>. The integer-safe
    /// equivalent used in <see cref="Resolve"/> is <c>5·n ≥ 3·capacity</c>
    /// (clearing the division: multiply both sides by 6 and substitute 0.5 = 3/6).
    /// </summary>
    private const double AutoLockRankErrorFraction = 0.5;

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
        => (int)BitOperations.RoundUpToPowerOf2((uint)(4 * processorCount));

    /// <summary>
    /// Resolves a <see cref="PriorityBinding"/> intent to a concrete
    /// <see cref="DispatchStrategy"/>.
    /// </summary>
    /// <param name="requested">The caller's binding intent.</param>
    /// <param name="processorCount">
    /// The number of logical processors; used only when <paramref name="requested"/>
    /// is <see cref="PriorityBinding.Auto"/>. Pass <see cref="Environment.ProcessorCount"/>
    /// at the call site so the heuristic is unit-testable across hardware shapes.
    /// </param>
    /// <param name="capacity">
    /// The queue's bounded capacity; used only when <paramref name="requested"/>
    /// is <see cref="PriorityBinding.Auto"/>.
    /// </param>
    /// <returns>
    /// <see cref="DispatchStrategy.PriorityLocking"/> or
    /// <see cref="DispatchStrategy.PriorityMultiQueue"/>.
    /// </returns>
    internal static DispatchStrategy Resolve(PriorityBinding requested, int processorCount, int capacity)
        => requested switch
        {
            PriorityBinding.Locking => DispatchStrategy.PriorityLocking,
            PriorityBinding.MultiQueue => DispatchStrategy.PriorityMultiQueue,

            // Auto: pick Locking when rank error (5/6)·n reaches half the capacity.
            // Integer-safe predicate: 5·n >= 3·capacity  (equivalent to (5/6)·n >= capacity·0.5).
            _ => ResolveAuto(processorCount, capacity),
        };

    private static DispatchStrategy ResolveAuto(int processorCount, int capacity)
    {
        var n = SubQueueCountFor(processorCount);
        return 5 * n >= 3 * capacity
            ? DispatchStrategy.PriorityLocking
            : DispatchStrategy.PriorityMultiQueue;
    }
}
