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
    /// Lower values are applied first (closer to the actual handler). Orders of 1,000,000,000 and
    /// above are reserved for Bifrost's own handler stages.
    /// </param>
    /// <returns>The builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="factory"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="order"/> is in the reserved band.</exception>
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
    /// <para>
    /// Dead-letter retries and capture (<c>WithDeadLetterQueue</c>) and completion events
    /// (<c>WithEventStream</c>) are not handler decorators. They run in fixed slots outside the
    /// outermost handler decorator, so every handler decorator sees each attempt, whatever the call
    /// order.
    /// </para>
    /// <para>
    /// Handler resilience (<c>WithHandlerResilience</c> in <c>Bifrost.Resilience</c>) uses a reserved
    /// order, so it is always the outermost handler decorator. Every decorator added here runs inside
    /// it and sees each of its attempts. The automatic order ignores reserved orders, so a decorator
    /// added after <c>WithHandlerResilience</c> still runs inside it.
    /// </para>
    /// </remarks>
    public static WorkOrchestratorBuilder<TWork> WithHandlerDecorator<TWork>(
        this WorkOrchestratorBuilder<TWork> builder,
        Func<IServiceProvider, IWorkHandler<TWork>, IWorkHandler<TWork>> factory,
        int? order = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        if (order is { } explicitOrder && HandlerDecoratorOrders.IsReserved(explicitOrder))
        {
            throw new ArgumentOutOfRangeException(
                nameof(order),
                explicitOrder,
                $"Handler decorator orders of {HandlerDecoratorOrders.ReservedBandStart} and above are reserved for Bifrost handler stages.");
        }

        var resolvedOrder = order ?? NextUserOrder(builder);

        builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<TWork>(
            Order: resolvedOrder,
            Factory: factory));

        return builder;
    }

    /// <summary>Returns one more than the highest user order, or zero when there is none.</summary>
    /// <remarks>Reserved orders are skipped, so the result stays below the reserved band.</remarks>
    private static int NextUserOrder<TWork>(WorkOrchestratorBuilder<TWork> builder)
    {
        int? highest = null;
        foreach (var registration in builder.HandlerDecorators)
        {
            if (!HandlerDecoratorOrders.IsReserved(registration.Order) && (highest is null || registration.Order > highest))
                highest = registration.Order;
        }

        return highest is { } value ? value + 1 : 0;
    }
}
