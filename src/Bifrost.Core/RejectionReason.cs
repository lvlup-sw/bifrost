// =============================================================================
// <copyright file="RejectionReason.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>
/// Identifies why an enqueue attempt was rejected at admission.
/// </summary>
/// <remarks>
/// Carried by <see cref="EnqueueResult.Reason"/> when
/// <see cref="EnqueueResult.IsAccepted"/> is <see langword="false"/>.
/// </remarks>
public enum RejectionReason
{
    /// <summary>
    /// The queue's total capacity is exhausted; no class can admit work.
    /// </summary>
    CapacityExceeded,

    /// <summary>
    /// The per-class watermark for the work's <see cref="WorkClass"/> is
    /// exceeded; the class cannot admit more work even though total capacity
    /// may remain.
    /// </summary>
    WatermarkExceeded,

    /// <summary>
    /// The orchestrator is shutting down and no longer admits new work.
    /// </summary>
    Shutdown,

    /// <summary>
    /// The work's key already has the most pending items that keyed dispatch
    /// allows for one key, even though total capacity may remain. Pending items
    /// are queued and not yet running.
    /// </summary>
    /// <remarks>
    /// The global capacity check runs first, so a full queue reports
    /// <see cref="CapacityExceeded"/> instead. Rejections with this reason are
    /// counted but not routed to the dead-letter queue: the caller owns the
    /// retry or shed decision for a saturated key.
    /// </remarks>
    KeyLimitExceeded,
}
