// =============================================================================
// <copyright file="PriorityComparerHelpers.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry/Concurrency/MultiQueue/PriorityComparerHelpers.cs)

namespace Bifrost.Concurrency.MultiQueue;

/// <summary>
/// Shared DR-6 comparer normalization, mirroring the private <c>InitializeComparer</c> helper
/// inside <see cref="PriorityQueue{TElement, TPriority}"/>. Extracted once so the queue shell and
/// every sub-queue normalize identically instead of duplicating the rule.
/// </summary>
internal static class PriorityComparerHelpers
{
    /// <summary>
    /// Normalizes a priority comparer for storage (DR-6). For a value-type
    /// <typeparamref name="TPriority"/> whose effective comparer is
    /// <see cref="Comparer{T}.Default"/>, returns <see langword="null"/> so hot paths can branch to
    /// the devirtualized <c>Comparer&lt;TPriority&gt;.Default.Compare</c> call (an inlined intrinsic
    /// for int/long/DateTime/enums). Reference-type priorities cannot devirtualize the default
    /// (dotnet/runtime#10050), so a null comparer is materialized to
    /// <see cref="Comparer{T}.Default"/> for the cached-field path.
    /// </summary>
    /// <typeparam name="TPriority">The priority type ordered by the comparer.</typeparam>
    /// <param name="comparer">The supplied comparer, or <see langword="null"/> for the default.</param>
    /// <returns>The normalized comparer to store: <see langword="null"/> selects the devirtualized default path.</returns>
    internal static IComparer<TPriority>? InitializeComparer<TPriority>(IComparer<TPriority>? comparer)
    {
        if (typeof(TPriority).IsValueType)
        {
            return ReferenceEquals(comparer, Comparer<TPriority>.Default) ? null : comparer;
        }

        return comparer ?? Comparer<TPriority>.Default;
    }
}
