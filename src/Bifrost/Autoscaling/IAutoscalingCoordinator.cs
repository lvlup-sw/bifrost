// =============================================================================
// <copyright file="IAutoscalingCoordinator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling.Ports;

namespace Bifrost.Autoscaling;

/// <summary>
/// Coordinates between autoscaling components and the orchestrator.
/// </summary>
/// <remarks>
/// <para>
/// This interface implements the mediator pattern to resolve circular dependencies
/// between the autoscaling engine and the work orchestrator. Instead of the
/// autoscaling components depending directly on the orchestrator (which would
/// create circular dependencies), they depend on this coordinator interface.
/// </para>
/// <para>
/// The coordinator aggregates three ports:
/// <list type="bullet">
///   <item><description><see cref="IAutoscalingMetricsPort"/> - provides read-only metrics</description></item>
///   <item><description><see cref="IAutoscalingControlPort"/> - provides scaling operations</description></item>
///   <item><description><see cref="IAutoscalingEventsPort"/> - provides event publishing</description></item>
/// </list>
/// </para>
/// <para>
/// This design follows the Ports and Adapters (Hexagonal) architecture pattern,
/// where the ports define what the autoscaling domain needs, and the coordinator
/// adapts the orchestrator to provide those capabilities.
/// </para>
/// </remarks>
public interface IAutoscalingCoordinator :
    IAutoscalingMetricsPort,
    IAutoscalingControlPort,
    IAutoscalingEventsPort
{
    // This interface combines all three ports - no additional members needed.
    // The composition provides a single injection point for autoscaling components.
}