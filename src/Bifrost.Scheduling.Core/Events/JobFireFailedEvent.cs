// =============================================================================
// <copyright file="JobFireFailedEvent.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core.Events;

/// <summary>
/// Raised when a job fire does not succeed. This covers two distinct outcomes:
/// a thrown dispatch exception, and a non-exception admission rejection — for
/// example, the orchestrator returning <c>Rejected</c> at admission.
/// </summary>
/// <param name="JobName">The name of the job whose fire failed.</param>
/// <param name="FiredAt">The instant the fire was attempted.</param>
/// <param name="Exception">
/// The exception thrown by the dispatch, or <see langword="null"/> when the
/// failure was a non-exception admission rejection (see <paramref name="Reason"/>).
/// </param>
/// <param name="Reason">
/// A human-readable reason for a non-exception failure — for instance the
/// orchestrator's rejection reason rendered as text. <see langword="null"/> when
/// the failure is carried by <paramref name="Exception"/>.
/// </param>
public readonly record struct JobFireFailedEvent(
    string JobName,
    DateTimeOffset FiredAt,
    Exception? Exception,
    string? Reason = null);
