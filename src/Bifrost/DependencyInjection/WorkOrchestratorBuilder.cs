// =============================================================================
// <copyright file="WorkOrchestratorBuilder.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Builder for configuring <see cref="IWorkOrchestrator{TWork}"/> with optional decorators.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
public sealed class WorkOrchestratorBuilder<TWork>
{
    /// <summary>
    /// Gets the service collection for registering additional services.
    /// </summary>
    /// <value>The service collection.</value>
    internal IServiceCollection Services { get; }

    /// <summary>
    /// Gets the list of decorator registrations.
    /// </summary>
    internal List<DecoratorRegistration<TWork>> Decorators { get; } = [];

    /// <summary>
    /// Gets the list of handler decorator registrations applied before the handler is passed to the orchestrator.
    /// </summary>
    /// <value>A list of handler decorator registrations with ordering support.</value>
    internal List<HandlerDecoratorRegistration<TWork>> HandlerDecorators { get; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkOrchestratorBuilder{TWork}"/> class.
    /// </summary>
    /// <param name="services">The service collection to register services with.</param>
    internal WorkOrchestratorBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>
    /// Builds and registers the <see cref="IWorkOrchestrator{TWork}"/> with all configured decorators.
    /// </summary>
    public void Build()
    {
        // Validate unique orchestrator decorator orders
        var duplicateOrders = Decorators
            .GroupBy(d => d.Order)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateOrders.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate decorator order(s): {string.Join(", ", duplicateOrders)}. Each orchestrator decorator must have a unique Order value.");
        }

        // Validate unique handler decorator orders
        var duplicateHandlerOrders = HandlerDecorators
            .GroupBy(d => d.Order)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateHandlerOrders.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate handler decorator order(s): {string.Join(", ", duplicateHandlerOrders)}. Each handler decorator must have a unique Order value.");
        }

        // Sort decorators by order (innermost first)
        var orderedDecorators = Decorators.OrderBy(d => d.Order).ToList();

        // Sort handler decorators by order (innermost first)
        var orderedHandlerDecorators = HandlerDecorators.OrderBy(d => d.Order).ToList();

        Services.AddSingleton<IWorkOrchestrator<TWork>>(sp =>
        {
            // Resolve handler from DI and apply handler decorators in order
            IWorkHandler<TWork> handler = sp.GetRequiredService<IWorkHandler<TWork>>();

            foreach (var registration in orderedHandlerDecorators)
            {
                handler = registration.Factory(sp, handler);
            }

            // Start with base implementation using the decorated handler
            IWorkOrchestrator<TWork> orchestrator = new WorkOrchestrator<TWork>(
                handler,
                sp.GetRequiredService<IOptions<WorkOrchestratorOptions>>(),
                sp.GetRequiredService<ILogger<WorkOrchestrator<TWork>>>());

            // Apply orchestrator decorators in order
            foreach (var registration in orderedDecorators)
            {
                orchestrator = registration.Factory(sp, orchestrator);
            }

            return orchestrator;
        });
    }
}

/// <summary>
/// Registration for a decorator in the chain.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <param name="Order">The order in which to apply this decorator (lower = innermost).</param>
/// <param name="Factory">Factory function to create the decorator.</param>
internal sealed record DecoratorRegistration<TWork>(
    int Order,
    Func<IServiceProvider, IWorkOrchestrator<TWork>, IWorkOrchestrator<TWork>> Factory);

/// <summary>
/// Registration for a handler decorator in the chain.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <param name="Order">The order in which to apply this handler decorator (lower = innermost).</param>
/// <param name="Factory">Factory function to create the handler decorator.</param>
internal sealed record HandlerDecoratorRegistration<TWork>(
    int Order,
    Func<IServiceProvider, IWorkHandler<TWork>, IWorkHandler<TWork>> Factory);