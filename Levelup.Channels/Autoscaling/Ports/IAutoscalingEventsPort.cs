// =============================================================================
// <copyright file="IAutoscalingEventsPort.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Levelup.Channels.Core.Events;

namespace Levelup.Channels.Autoscaling.Ports;

/// <summary>
/// Provides event publishing capabilities for autoscaling operations.
/// </summary>
/// <remarks>
/// <para>
/// This port interface provides methods for publishing orchestrator events
/// and querying event stream metrics. Events are published to notify observers
/// of scaling actions, worker lifecycle changes, and other autoscaling activities.
/// </para>
/// <para>
/// The port pattern enables the autoscaling components to depend on an
/// abstraction rather than the concrete event stream implementation, resolving
/// circular dependency issues in the architecture.
/// </para>
/// </remarks>
public interface IAutoscalingEventsPort
{
    /// <summary>
    /// Publishes an orchestrator event to the event stream.
    /// </summary>
    /// <param name="orchestratorEvent">The event to publish.</param>
    /// <remarks>
    /// Events are typically published to a bounded channel and may be dropped
    /// if the channel is full. This is a fire-and-forget operation.
    /// </remarks>
    void PublishEvent(IOrchestratorEvent orchestratorEvent);

    /// <summary>
    /// Gets the number of events currently queued for delivery.
    /// </summary>
    /// <value>The count of queued events.</value>
    /// <remarks>
    /// This metric can be used to monitor event stream backpressure.
    /// </remarks>
    long QueuedEventCount { get; }

    /// <summary>
    /// Gets the number of active subscribers to the event stream.
    /// </summary>
    /// <value>The count of active subscribers.</value>
    /// <remarks>
    /// This metric can be used to determine if anyone is consuming events.
    /// </remarks>
    long ActiveSubscriberCount { get; }
}
