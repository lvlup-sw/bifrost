// =============================================================================
// <copyright file="HandlerDecoratorExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Extension methods for adding custom handler decorators to the work orchestrator pipeline.
/// </summary>
public static class HandlerDecoratorExtensions
{
    /// <summary>
    /// Adds a custom handler decorator to the processing pipeline.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="factory">
    /// Factory that wraps the inner handler. Receives the service provider and the inner
    /// handler, and returns a decorated handler.
    /// </param>
    /// <param name="order">
    /// Optional explicit order. If null, auto-assigns the next available order.
    /// Lower values are applied first (closer to the actual handler).
    /// Built-in decorator orders: DLQ = auto, CompletionTracking = auto.
    /// </param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="factory"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Handler decorators wrap the <see cref="IWorkHandler{TWork}"/> before it is passed to the
    /// orchestrator. This allows cross-cutting concerns like logging, metrics, or validation
    /// to be applied at the handler level rather than the orchestrator level.
    /// </para>
    /// <para>
    /// Decorators are applied in ascending order: the decorator with the lowest order value
    /// is applied first (closest to the original handler), and the one with the highest order
    /// is outermost.
    /// </para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> WithHandlerDecorator<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Func<IServiceProvider, IWorkHandler<TWork>, IWorkHandler<TWork>> factory,
        int? order = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        var resolvedOrder = order ?? (builder.HandlerDecorators.Count == 0
            ? 0
            : builder.HandlerDecorators.Max(d => d.Order) + 1);

        builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<TWork>(
            Order: resolvedOrder,
            Factory: factory));

        return builder;
    }
}
