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
    /// The orchestrator also publishes one <see cref="WorkCompletedEvent{TWork}"/> per started work
    /// item from its terminal outcome stage. The stage runs after the handler pipeline exits, outside
    /// every handler decorator and outside dead-letter retries, so the event reports the item's final
    /// result (<c>Success</c> is <see langword="false"/> for a failed or canceled item, also when the
    /// failure was dead-lettered) and its duration covers every attempt. The behavior is the same for
    /// every builder call order.
    /// </para>
    /// <para>
    /// The events reach the stream through the builder-level <c>EventPublishCallback</c>, which uses
    /// the captured local <c>eventStreamOrchestrator</c>. That reference is set when the orchestrator
    /// factory runs during <c>Build()</c>, before any work can be enqueued. Other extensions (for
    /// example, <c>WithDeadLetterQueue</c>) publish through the same callback.
    /// </para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> WithEventStream<TWork>(
        this WorkOrchestratorBuilder<TWork> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        EventStreamOrchestrator<TWork>? eventStreamOrchestrator = null;

        builder.EventPublishCallback = evt => eventStreamOrchestrator?.PublishToSubscribers(evt);

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