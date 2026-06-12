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
}
