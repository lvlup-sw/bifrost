// =============================================================================
// <copyright file="WorkRejectedException.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>
/// Marker exception carried by dead-letter entries produced from admission
/// rejections, distinguishing them from entries produced by handler
/// failures.
/// </summary>
/// <remarks>
/// <para>
/// This exception is never thrown by the orchestrator: admission outcomes
/// surface as <see cref="EnqueueResult"/> values, not exceptions. It exists
/// only as the <c>Exception</c> component of a dead-lettered entry, so DLQ
/// consumers can pattern-match rejection entries (paired with an attempt
/// count of zero — rejected work was never admitted, so it was never
/// attempted) and read the <see cref="Reason"/> for diagnosis and replay
/// decisions.
/// </para>
/// <para>
/// Because instances are markers rather than thrown errors, the routing
/// decorator caches one instance per <see cref="RejectionReason"/> — no
/// per-rejection allocation, and no stack trace is ever captured.
/// </para>
/// </remarks>
public sealed class WorkRejectedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkRejectedException"/> class.
    /// </summary>
    public WorkRejectedException()
        : base("Work item rejected at admission.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkRejectedException"/> class
    /// with a custom message.
    /// </summary>
    /// <param name="message">The message that describes the rejection.</param>
    public WorkRejectedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkRejectedException"/> class
    /// with a custom message and an inner exception.
    /// </summary>
    /// <param name="message">The message that describes the rejection.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public WorkRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkRejectedException"/> class
    /// for the given admission rejection reason.
    /// </summary>
    /// <param name="reason">The reason the work item was rejected at admission.</param>
    public WorkRejectedException(RejectionReason reason)
        : base($"Work item rejected at admission: {reason}.")
    {
        Reason = reason;
    }

    /// <summary>
    /// Gets the reason the work item was rejected at admission.
    /// </summary>
    /// <value>
    /// The rejection reason; defaults to
    /// <see cref="RejectionReason.CapacityExceeded"/> when constructed without one.
    /// </value>
    public RejectionReason Reason { get; }
}
