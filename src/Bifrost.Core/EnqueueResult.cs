// =============================================================================
// <copyright file="EnqueueResult.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>
/// The admission outcome of an enqueue attempt.
/// </summary>
/// <remarks>
/// <para>
/// Rejection is an admission outcome: it occurs before the work item enters
/// the queue, so the at-least-once execution contract is unaffected — work
/// that was admitted is still executed at least once, and work that was
/// rejected was never admitted in the first place.
/// </para>
/// <para>
/// Rejected work routes to the dead-letter queue decorator when one is
/// configured, carrying the <see cref="Reason"/> for diagnosis and replay.
/// </para>
/// <para>
/// This is a readonly record struct for efficient, immutable representation
/// of admission outcomes without heap allocation.
/// </para>
/// </remarks>
public readonly record struct EnqueueResult
{
    private EnqueueResult(bool isAccepted, RejectionReason? reason)
    {
        IsAccepted = isAccepted;
        Reason = reason;
    }

    /// <summary>
    /// Gets the singleton result representing a successfully admitted enqueue.
    /// </summary>
    /// <value>
    /// A result with <see cref="IsAccepted"/> set to <see langword="true"/>
    /// and <see cref="Reason"/> set to <see langword="null"/>.
    /// </value>
    public static EnqueueResult Accepted { get; } = new(true, null);

    /// <summary>
    /// Gets a value indicating whether the work item was admitted.
    /// </summary>
    /// <value>
    /// <see langword="true"/> if the work item was admitted to the queue;
    /// otherwise, <see langword="false"/>.
    /// </value>
    public bool IsAccepted { get; }

    /// <summary>
    /// Gets the reason the work item was rejected at admission.
    /// </summary>
    /// <value>
    /// The rejection reason when <see cref="IsAccepted"/> is
    /// <see langword="false"/>; otherwise, <see langword="null"/>.
    /// </value>
    public RejectionReason? Reason { get; }

    /// <summary>
    /// Creates a result representing an enqueue rejected at admission.
    /// </summary>
    /// <param name="reason">The reason the work item was rejected.</param>
    /// <returns>
    /// A result with <see cref="IsAccepted"/> set to <see langword="false"/>
    /// and <see cref="Reason"/> set to <paramref name="reason"/>.
    /// </returns>
    public static EnqueueResult Rejected(RejectionReason reason) => new(false, reason);
}
