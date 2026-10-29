// =============================================================================
// <copyright file="EnqueueResult.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>The state of an admission outcome.</summary>
public enum EnqueueStatus
{
    /// <summary>No operation has supplied an outcome.</summary>
    Uninitialized,

    /// <summary>The item entered the queue.</summary>
    Accepted,

    /// <summary>The item did not enter the queue.</summary>
    Rejected,
}

/// <summary>An immutable admission outcome. Acceptance is not a durability or execution guarantee.</summary>
public readonly record struct EnqueueResult
{
    private EnqueueResult(EnqueueStatus status, RejectionReason? reason)
    {
        Status = status;
        Reason = reason;
    }

    /// <summary>Gets the outcome state. The default value is uninitialized.</summary>
    public EnqueueStatus Status { get; }

    /// <summary>Gets whether the item was admitted.</summary>
    public bool IsAccepted => Status == EnqueueStatus.Accepted;

    /// <summary>Gets the rejection reason, or null for accepted and uninitialized values.</summary>
    public RejectionReason? Reason { get; }

    /// <summary>Gets an accepted outcome.</summary>
    public static EnqueueResult Accepted { get; } = new(EnqueueStatus.Accepted, null);

    /// <summary>Creates a rejected outcome.</summary>
    /// <param name="reason">The admission rejection reason.</param>
    /// <returns>A rejected outcome.</returns>
    public static EnqueueResult Rejected(RejectionReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new(EnqueueStatus.Rejected, reason);
    }
}
