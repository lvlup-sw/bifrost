// =============================================================================
// <copyright file="SubQueuePopStatus.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry/Concurrency/MultiQueue/SubQueuePopStatus.cs)

namespace Bifrost.Concurrency.MultiQueue;

/// <summary>
/// The three-way outcome of <c>SubQueue.TryLockedPop</c>. Callers must distinguish an empty
/// sub-queue (which counts toward an empty-verification pass) from a contended one (where a
/// concurrent writer holds the lock — someone is making progress, so the caller resamples or
/// restarts its pass instead of concluding emptiness).
/// </summary>
internal enum SubQueuePopStatus
{
    /// <summary>The root entry was removed and returned.</summary>
    Success,

    /// <summary>The lock was acquired but the heap held no entries.</summary>
    Empty,

    /// <summary>The lock was held by another thread; nothing was observed or removed.</summary>
    Contended,
}
