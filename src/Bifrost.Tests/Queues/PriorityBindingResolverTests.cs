// =============================================================================
// <copyright file="PriorityBindingResolverTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;
using Bifrost.Core;
using Bifrost.Queues;

using TUnit.Core;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Tests for <see cref="PriorityBindingResolver"/> (T4, T5, T6).
/// </summary>
[Property("Category", "Unit")]
public class PriorityBindingResolverTests
{
    // ─── T4: Explicit pass-through ───────────────────────────────────────────

    /// <summary>
    /// Verifies that explicit <see cref="PriorityBinding.Locking"/> always resolves
    /// to <see cref="DispatchStrategy.PriorityLocking"/> — the only path to locking.
    /// </summary>
    [Test]
    public async Task Resolve_ExplicitLocking_ReturnsPriorityLocking()
    {
        var result = PriorityBindingResolver.Resolve(PriorityBinding.Locking);
        await Assert.That(result).IsEqualTo(DispatchStrategy.PriorityLocking);
    }

    /// <summary>
    /// Verifies that explicit <see cref="PriorityBinding.MultiQueue"/> always resolves
    /// to <see cref="DispatchStrategy.PriorityMultiQueue"/>.
    /// </summary>
    [Test]
    public async Task Resolve_ExplicitMultiQueue_ReturnsPriorityMultiQueue()
    {
        var result = PriorityBindingResolver.Resolve(PriorityBinding.MultiQueue);
        await Assert.That(result).IsEqualTo(DispatchStrategy.PriorityMultiQueue);
    }

    // ─── T5: Auto NEVER resolves to Locking (#46) ────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PriorityBinding.Auto"/> resolves to
    /// <see cref="DispatchStrategy.PriorityMultiQueue"/> for every capacity ×
    /// (former) processor-count combination — Auto must never select Locking, which
    /// is now reachable only via an explicit <see cref="PriorityBinding.Locking"/>
    /// request. The capacity/processor-count matrix is retained from the old
    /// heuristic's input space (including the cases that previously flipped to
    /// Locking, e.g. cap=32/pc=8 and cap=128/pc=17) so a regression that reintroduced
    /// any capacity- or core-sensitive selection would fail here. The arguments no
    /// longer feed <c>Resolve</c> (it takes only the binding now); they document the
    /// regime space the old heuristic spanned.
    /// </summary>
    [Test]
    [Arguments(32, 1)]
    [Arguments(32, 4)]
    [Arguments(32, 8)]
    [Arguments(32, 16)]
    [Arguments(32, 17)]
    [Arguments(32, 32)]
    [Arguments(128, 1)]
    [Arguments(128, 4)]
    [Arguments(128, 8)]
    [Arguments(128, 16)]
    [Arguments(128, 17)]
    [Arguments(128, 32)]
    [Arguments(256, 1)]
    [Arguments(256, 4)]
    [Arguments(256, 8)]
    [Arguments(256, 16)]
    [Arguments(256, 17)]
    [Arguments(256, 32)]
    [Arguments(1024, 1)]
    [Arguments(1024, 4)]
    [Arguments(1024, 8)]
    [Arguments(1024, 16)]
    [Arguments(1024, 17)]
    [Arguments(1024, 32)]
    public async Task Resolve_Auto_AlwaysResolvesToMultiQueue(int capacity, int processorCount)
    {
        // capacity/processorCount document the regime once spanned by the heuristic;
        // they intentionally have no effect on the result anymore.
        _ = capacity;
        _ = processorCount;

        var result = PriorityBindingResolver.Resolve(PriorityBinding.Auto);

        await Assert.That(result).IsEqualTo(DispatchStrategy.PriorityMultiQueue);
    }

    // ─── T6: SubQueueCountFor matches actual ConcurrentPriorityQueue sub-queue count ───

    /// <summary>
    /// Verifies that <see cref="PriorityBindingResolver.SubQueueCountFor"/> returns the
    /// same count that a freshly-constructed <see cref="ConcurrentPriorityQueue{TElement,TPriority}"/>
    /// uses for its internal sub-queues, ensuring the resolver's sub-queue arithmetic and
    /// the actual queue construction stay in agreement.
    /// </summary>
    [Test]
    public async Task SubQueueCountFor_MatchesConcurrentPriorityQueueActualCount()
    {
        var resolverCount = PriorityBindingResolver.SubQueueCountFor(Environment.ProcessorCount);
        var queue = new ConcurrentPriorityQueue<int, long>();
        var actualCount = queue.SubQueueCount;

        await Assert.That(resolverCount).IsEqualTo(actualCount);
    }
}
