// =============================================================================
// <copyright file="EventStreamExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.Events;
using Bifrost.Decorators;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Extension methods for adding event streaming capability to the orchestrator.
/// </summary>
public static class EventStreamExtensions
{
    /// <summary>
    /// Adds event streaming capability to the orchestrator.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The builder to configure.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This adds the <see cref="EventStreamOrchestrator{TWork}"/> decorator which
    /// publishes events for work lifecycle operations. Events can be consumed via
    /// <see cref="Core.IEventStreamOrchestrator{TWork}.GetEventStreamAsync{TEvent}"/>.
    /// </para>
    /// <para>
    /// The decorator is applied with order 50, placing it between the core orchestrator
    /// and higher-order decorators like autoscaling.
    /// </para>
    /// <para>
    /// Additionally, a handler decorator is registered to publish
    /// <see cref="WorkCompletedEvent{TWork}"/> events when work items complete processing.
    /// The handler decorator uses late-binding to reference the orchestrator instance,
    /// allowing proper DI ordering where handler decorators are applied before orchestrator
    /// decorators.
    /// </para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> WithEventStream<TWork>(
        this WorkOrchestratorBuilder<TWork> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Use a captured reference for late-binding the orchestrator to the handler decorator.
        // The handler decorator is applied first (to the handler), then the orchestrator
        // decorator wraps the orchestrator. By the time the handler processes work,
        // the orchestrator reference is available.
        EventStreamOrchestrator<TWork>? eventStreamOrchestrator = null;

        // Add handler decorator for WorkCompletedEvent publishing
        builder.HandlerDecorators.Add((sp, handler) =>
            EventStreamOrchestrator<TWork>.CreateCompletionTrackingHandler(
                handler,
                evt => eventStreamOrchestrator?.PublishToSubscribers(evt)));

        // Add orchestrator decorator for event streaming
        builder.Decorators.Add(new DecoratorRegistration<TWork>(
            Order: 50,
            Factory: (sp, inner) =>
            {
                eventStreamOrchestrator = new EventStreamOrchestrator<TWork>(
                    inner,
                    sp.GetRequiredService<ILogger<EventStreamOrchestrator<TWork>>>());
                return eventStreamOrchestrator;
            }));

        return builder;
    }
}