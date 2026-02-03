// =============================================================================
// <copyright file="ScalingDecisionEventArgs.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Autoscaling;

/// <summary>
/// Event arguments for the <see cref="IAutoscalingEngine.ScalingDecisionMade"/> event.
/// </summary>
/// <param name="Decision">The scaling decision that was made.</param>
/// <param name="CorrelationId">A unique identifier for this evaluation cycle.</param>
/// <remarks>
/// <para>
/// This event is raised for every scaling evaluation, allowing subscribers
/// to track decisions over time for monitoring and debugging purposes.
/// </para>
/// <para>
/// The correlation ID can be used to correlate related log entries and
/// trace the execution of scaling decisions.
/// </para>
/// </remarks>
public sealed record ScalingDecisionEventArgs(
    ScalingDecision Decision,
    string CorrelationId);
