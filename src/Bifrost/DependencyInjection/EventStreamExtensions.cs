// =============================================================================
// <copyright file="EventStreamExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

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
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> WithEventStream<TWork>(
        this WorkOrchestratorBuilder<TWork> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Decorators.Add(new DecoratorRegistration<TWork>(
            Order: 50,
            Factory: (sp, inner) => new EventStreamOrchestrator<TWork>(
                inner,
                sp.GetRequiredService<ILogger<EventStreamOrchestrator<TWork>>>())));

        return builder;
    }
}
