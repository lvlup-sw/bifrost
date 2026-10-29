// =============================================================================
// <copyright file="ConcurrentPriorityQueueOptions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Concurrency;

/// <summary>Explicit construction settings for a relaxed MultiQueue priority queue.</summary>
/// <typeparam name="TPriority">The priority type.</typeparam>
public sealed class ConcurrentPriorityQueueOptions<TPriority>
{
    /// <summary>Gets the hard item limit, or null for unbounded admission.</summary>
    public int? Capacity { get; init; }

    /// <summary>Gets the requested stripe count; null uses four times ProcessorCount, rounded up to a power of two.</summary>
    public int? SubQueueCount { get; init; }

    /// <summary>Gets the number of operations reusing a sampled selection. Must be positive.</summary>
    public int Stickiness { get; init; } = 1;

    /// <summary>Gets the per-stripe insertion/deletion buffer capacity, from zero (disabled) through sixteen.</summary>
    public int BufferCapacity { get; init; }

    /// <summary>Gets the priority comparer; null selects Comparer&lt;TPriority&gt;.Default.</summary>
    public IComparer<TPriority>? Comparer { get; init; }
}
