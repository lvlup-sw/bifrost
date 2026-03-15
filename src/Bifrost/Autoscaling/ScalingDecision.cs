// =============================================================================
// <copyright file="ScalingDecision.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.Events;

namespace Bifrost.Autoscaling;

/// <summary>
/// Represents a scaling decision made by the autoscaling engine.
/// </summary>
/// <param name="Action">The scaling action to take.</param>
/// <param name="CurrentWorkers">The current number of workers.</param>
/// <param name="TargetWorkers">The target number of workers after scaling.</param>
/// <param name="UtilizationRatio">The current utilization ratio (0.0 to 1.0).</param>
/// <param name="Reason">A human-readable reason for the decision.</param>
/// <remarks>
/// <para>
/// This is a readonly record struct for efficient, immutable representation
/// of scaling decisions without heap allocation.
/// </para>
/// <para>
/// The decision includes all information needed to understand and execute
/// the scaling action, including the rationale.
/// </para>
/// </remarks>
public readonly record struct ScalingDecision(
    ScalingAction Action,
    int CurrentWorkers,
    int TargetWorkers,
    double UtilizationRatio,
    string Reason);