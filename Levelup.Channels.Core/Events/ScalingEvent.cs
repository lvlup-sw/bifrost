// =============================================================================
// <copyright file="ScalingEvent.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Levelup.Channels.Core.Events;

/// <summary>
/// The type of scaling action taken.
/// </summary>
public enum ScalingAction
{
    /// <summary>
    /// No scaling action was taken.
    /// </summary>
    None,

    /// <summary>
    /// Workers were scaled up (increased).
    /// </summary>
    ScaleUp,

    /// <summary>
    /// Workers were scaled down (decreased).
    /// </summary>
    ScaleDown
}

/// <summary>
/// Event raised when autoscaling action occurs.
/// </summary>
/// <param name="Action">The type of scaling action taken.</param>
/// <param name="PreviousWorkers">The number of workers before scaling.</param>
/// <param name="CurrentWorkers">The number of workers after scaling.</param>
/// <param name="Utilization">The utilization metric that triggered scaling.</param>
/// <remarks>
/// This event is useful for monitoring autoscaling behavior and
/// tuning scaling thresholds.
/// </remarks>
public readonly record struct ScalingEvent(
    ScalingAction Action,
    int PreviousWorkers,
    int CurrentWorkers,
    double Utilization) : IOrchestratorEvent;
