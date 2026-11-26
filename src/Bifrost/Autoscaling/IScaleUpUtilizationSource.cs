// =============================================================================
// <copyright file="IScaleUpUtilizationSource.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Autoscaling;

/// <summary>
/// Gives the autoscaling engine a separate utilization ratio for the scale-up decision.
/// </summary>
/// <remarks>
/// <para>
/// A coordinator implements this when some pending items can be blocked: admitted, but not
/// runnable by another worker until a running item releases its slot. Keyed dispatch is the case.
/// The scale-up ratio counts only the items that more workers could run. The scale-down decision
/// keeps using <see cref="Ports.IAutoscalingMetricsPort.GetUtilizationRatio"/>, the total pending
/// depth, so a blocked backlog neither adds workers nor removes the workers that are draining it.
/// </para>
/// <para>
/// A coordinator that does not implement this uses one ratio for both decisions.
/// </para>
/// </remarks>
internal interface IScaleUpUtilizationSource
{
    /// <summary>Calculates the utilization ratio that the scale-up decision compares to the high watermark.</summary>
    /// <returns>The scale-up backlog divided by capacity, or 0.0 when capacity is zero.</returns>
    double GetScaleUpUtilizationRatio();
}
